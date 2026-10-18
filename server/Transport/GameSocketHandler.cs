using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using PixelMmo.Server.Game;
using PixelMmo.Server.Persistence;

namespace PixelMmo.Server.Transport;

public sealed class GameSocketHandler(
    SupabaseAuthService auth,
    GameRepository repository,
    GameWorld world,
    ILogger<GameSocketHandler> logger)
{
    private const int MaxMessageBytes = 8 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Console.WriteLine("WebSocket accepted.");
        GameSession? session = null;
        try
        {
            var firstFrame = await ReceiveFrameAsync(socket, context.RequestAborted);
            Console.WriteLine($"Auth frame received: {firstFrame?.Length ?? 0} bytes.");
            if (firstFrame is null) return;
            using var authDocument = ParseFrame(firstFrame);
            var authMessage = authDocument.RootElement;
            if (!IsObject(authMessage) || ReadString(authMessage, "type") != "auth")
            {
                await RejectAsync(socket, "authentication-required", "Authenticate in the first WebSocket message.", context.RequestAborted);
                return;
            }
            if (!TryReadInt(authMessage, "version", out var version) || version != GameRules.ProtocolVersion)
            {
                await RejectAsync(socket, "protocol-version", "This client and server protocol version do not match.", context.RequestAborted);
                return;
            }
            var token = ReadString(authMessage, "accessToken");
            if (token.Length is < 20 or > 4096)
            {
                await RejectAsync(socket, "invalid-token", "A valid Supabase access token is required.", context.RequestAborted);
                return;
            }

            Guid? ownerId;
            try { ownerId = await auth.ValidateAccessTokenAsync(token, context.RequestAborted); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(exception, "Supabase access-token validation failed");
                await RejectAsync(socket, "auth-unavailable", "Authentication is temporarily unavailable.", context.RequestAborted);
                return;
            }
            Console.WriteLine($"Auth validated: {ownerId is not null}.");
            if (ownerId is null)
            {
                await RejectAsync(socket, "invalid-token", "Your login has expired. Sign in again.", context.RequestAborted);
                return;
            }
            var characters = await repository.GetCharactersAsync(ownerId.Value, context.RequestAborted);
            Console.WriteLine($"Sending auth-ok with {characters.Count} characters.");
            await SendAsync(socket, new AuthOkMessage("auth-ok", GameRules.ProtocolVersion, characters), context.RequestAborted);

            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var frame = await ReceiveFrameAsync(socket, context.RequestAborted);
                if (frame is null) break;
                JsonDocument document;
                try { document = ParseFrame(frame); }
                catch (JsonException)
                {
                    await SendAsync(socket, new ErrorMessage("error", "invalid-json", "Send one valid JSON object per message."), context.RequestAborted);
                    continue;
                }

                using (document)
                {
                    var message = document.RootElement;
                    if (!IsObject(message) || ReadString(message, "type").Length == 0)
                    {
                        await SendAsync(socket, new ErrorMessage("error", "invalid-message", "A message type is required."), context.RequestAborted);
                        continue;
                    }
                    if (session is null)
                    {
                        await HandleCharacterMenuAsync(socket, ownerId.Value, message, context.RequestAborted);
                        if (message.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "select-character" &&
                            message.TryGetProperty("characterId", out var idValue) && Guid.TryParse(idValue.GetString(), out var characterId))
                        {
                            var character = await repository.GetOwnedCharacterAsync(ownerId.Value, characterId, context.RequestAborted);
                            if (character is not null)
                            {
                                session = await world.EnterAsync(ownerId.Value, character, socket, context.RequestAborted);
                            }
                        }
                    }
                    else
                    {
                        await world.HandleMessageAsync(session, message, context.RequestAborted);
                    }
                }
            }
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "Game WebSocket disconnected");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (JsonException exception)
        {
            logger.LogInformation(exception, "Rejected malformed WebSocket authentication frame");
            await RejectAsync(socket, "invalid-auth-message", "The first message must be a valid authentication object.", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected game WebSocket failure");
            await RejectAsync(socket, "server-error", "The game server could not process this connection.", CancellationToken.None);
        }
        finally
        {
            if (session is not null) await world.LeaveAsync(socket, CancellationToken.None);
        }
    }

    private async Task HandleCharacterMenuAsync(WebSocket socket, Guid ownerId, JsonElement message, CancellationToken cancellationToken)
    {
        try
        {
            switch (ReadString(message, "type"))
            {
                case "create-character":
                    var name = ReadString(message, "name");
                    var job = ReadString(message, "job");
                    await repository.CreateCharacterAsync(ownerId, name, job, cancellationToken);
                    await SendAsync(socket, new AuthOkMessage("auth-ok", GameRules.ProtocolVersion, await repository.GetCharactersAsync(ownerId, cancellationToken)), cancellationToken);
                    break;
                case "select-character":
                    if (!Guid.TryParse(ReadString(message, "characterId"), out var characterId) || await repository.GetOwnedCharacterAsync(ownerId, characterId, cancellationToken) is null)
                        await SendAsync(socket, new ErrorMessage("error", "character-not-found", "That character does not belong to this account."), cancellationToken);
                    break;
                default:
                    await SendAsync(socket, new ErrorMessage("error", "select-character", "Create or select a character before entering the world."), cancellationToken);
                    break;
            }
        }
        catch (InvalidOperationException exception)
        {
            await SendAsync(socket, new ErrorMessage("error", "character-invalid", exception.Message), cancellationToken);
        }
    }

    private static JsonDocument ParseFrame(byte[] frame) => JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });

    private static async Task<byte[]?> ReceiveFrameAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var output = new ArrayBufferWriter<byte>();
        var buffer = new byte[1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new WebSocketException("Only text messages are accepted.");
            if (output.WrittenCount + result.Count > MaxMessageBytes) throw new WebSocketException("Message exceeds the size limit.");
            output.Write(buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage) return output.WrittenMemory.ToArray();
        }
    }

    private static async Task SendAsync<T>(WebSocket socket, T message, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (message is AuthOkMessage) Console.WriteLine($"Auth-ok payload: {Convert.ToHexString(bytes)}");
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task RejectAsync(WebSocket socket, string code, string message, CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await SendAsync(socket, new ErrorMessage("error", code, message), cancellationToken);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, code, cancellationToken);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
        }
    }

    private static bool IsObject(JsonElement value) => value.ValueKind == JsonValueKind.Object;

    private static bool TryReadInt(JsonElement element, string property, out int result)
    {
        result = 0;
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out result);
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}

public sealed record AuthOkMessage(string Type, int ProtocolVersion, IReadOnlyList<GameCharacter> Characters);

