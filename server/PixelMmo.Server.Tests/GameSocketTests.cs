using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PixelMmo.Server.Game;
using PixelMmo.Server.Persistence;
using PixelMmo.Server.Transport;
using Xunit;

namespace PixelMmo.Server.Tests;

public sealed class GameSocketTests
{
    private static readonly TimeSpan SocketTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TwentyPlayersShareOneMapAndForgedRewardsAreRejected()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Supabase:Url"] = "https://auth.test",
            ["Supabase:PublishableKey"] = "test-key",
            ["ConnectionStrings:GameDatabase"] = TestDatabase.ConnectionString,
            ["Game:SnapshotIntervalMs"] = "1000",
            ["Game:MoveCooldownMs"] = "80",
        });
        builder.Services.AddHttpClient<SupabaseAuthService>().ConfigurePrimaryHttpMessageHandler(() => new StubAuthHandler());
        builder.Services.AddSingleton<GameRepository>();
        builder.Services.AddSingleton<GameWorld>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<GameWorld>());
        builder.Services.AddSingleton<GameSocketHandler>();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws/game", async (HttpContext context, GameSocketHandler handler) => await handler.HandleAsync(context));
        await app.StartAsync();
        var serverAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("Kestrel did not report its bound address.");
        var socketUri = new UriBuilder(serverAddress) { Scheme = "ws", Path = "/ws/game" }.Uri;

        var players = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => ConnectAndCreateAsync(socketUri, index)));
        try
        {
            for (var step = 0; step < 7; step++)
            {
                var dy = step < 4 ? -1 : 1;
                await Task.WhenAll(players.Select(player => SendMoveAsync(player, 1, dy)));
                await Task.Delay(100);
            }
            await Task.WhenAll(players.Select(player => SendAsync(player.Socket, new { type = "change-map", mapId = GameMaps.Field })));
            foreach (var player in players)
            {
                var changed = await ReceiveTypeAsync(player.Socket, "map-changed");
                Assert.Equal(GameMaps.Field, changed.RootElement.GetProperty("mapId").GetString());
                changed.Dispose();
            }

            for (var step = 0; step < 8; step++)
            {
                var dy = step % 2 == 0 ? -1 : 1;
                await Task.WhenAll(players.Select(player => SendMoveAsync(player, 1, dy)));
                await Task.Delay(100);
            }
            await Task.WhenAll(players.Select(player => SendAsync(player.Socket, new
            {
                type = "attack", targetId = "moss-slime-01", commandId = Guid.NewGuid().ToString(),
            })));
            await Task.WhenAll(players.Select(async player =>
            {
                using var snapshot = await ReceiveMatchingAsync(player.Socket, "world-state", item => item.GetProperty("players").GetArrayLength() == 20);
                Assert.Equal(GameMaps.Field, snapshot.RootElement.GetProperty("mapId").GetString());
                Assert.Equal(20, snapshot.RootElement.GetProperty("players").GetArrayLength());
            }));

            await SendAsync(players[0].Socket, new { type = "chat", text = "field-check" });
            foreach (var player in players)
            {
                using var chat = await ReceiveMatchingAsync(player.Socket, "chat", item => item.GetProperty("text").GetString() == "field-check");
                Assert.Equal(players[0].Name, chat.RootElement.GetProperty("name").GetString());
            }

            await SendAsync(players[0].Socket, new { type = "grant-reward", experience = 999999, itemKey = "moonstone" });
            using var rejected = await ReceiveMatchingAsync(players[0].Socket, "error", item => item.GetProperty("code").GetString() == "unknown-command");
            Assert.Equal("unknown-command", rejected.RootElement.GetProperty("code").GetString());

            players[0].Socket.Dispose();
            var reconnect = new ClientWebSocket();
            await reconnect.ConnectAsync(socketUri, CancellationToken.None);
            try
            {
                await SendAsync(reconnect, new { type = "auth", version = 2, accessToken = $"{players[0].OwnerId:N}.test-access-token" });
                (await ReceiveTypeAsync(reconnect, "auth-ok")).Dispose();
                await SendAsync(reconnect, new { type = "select-character", characterId = players[0].CharacterId });
                using var entered = await ReceiveTypeAsync(reconnect, "entered-world");
                var saved = entered.RootElement.GetProperty("character");
                Assert.Equal(GameMaps.Field, saved.GetProperty("mapId").GetString());
                Assert.Equal(9, saved.GetProperty("x").GetInt32());
                Assert.Equal(8, saved.GetProperty("y").GetInt32());
            }
            finally { reconnect.Dispose(); }
        }
        finally
        {
            foreach (var player in players) player.Socket.Dispose();
            await app.StopAsync();
        }
    }

    private static async Task<TestPlayer> ConnectAndCreateAsync(Uri socketUri, int index)
    {
        var owner = Guid.NewGuid();
        await TestDatabase.SeedOwnerAsync(owner);
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUri, CancellationToken.None);
        await SendAsync(socket, new { type = "auth", version = 2, accessToken = $"{owner:N}.test-access-token" });
        try { (await ReceiveTypeAsync(socket, "auth-ok")).Dispose(); }
        catch (TimeoutException exception) { throw new TimeoutException($"Player {index} did not receive the initial auth response.", exception); }
        var name = $"Hero{index}";
        await SendAsync(socket, new { type = "create-character", name, job = "Swordsman" });
        Guid characterId;
        using (var response = await ReceiveTypeAsync(socket, "auth-ok"))
            characterId = Guid.Parse(response.RootElement.GetProperty("characters")[0].GetProperty("id").GetString()!);
        await SendAsync(socket, new { type = "select-character", characterId });
        using (var entered = await ReceiveTypeAsync(socket, "entered-world")) { }
        return new TestPlayer(socket, owner, characterId, name);
    }

    private static Task SendMoveAsync(TestPlayer player, int dx, int dy) =>
        SendAsync(player.Socket, new { type = "move", sequence = ++player.Sequence, dx, dy });

    private static async Task SendAsync(WebSocket socket, object value)
    {
        using var timeout = new CancellationTokenSource(SocketTimeout);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, timeout.Token);
    }

    private static Task<JsonDocument> ReceiveTypeAsync(WebSocket socket, string type) =>
        ReceiveMatchingAsync(socket, type, _ => true);

    private static async Task<JsonDocument> ReceiveMatchingAsync(WebSocket socket, string type, Func<JsonElement, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(SocketTimeout);
        var buffer = new byte[8192];
        var output = new ArrayBufferWriter<byte>();
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token); }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for WebSocket message '{type}'.", exception);
            }
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Socket closed before expected message.");
            output.Write(buffer.AsSpan(0, result.Count));
            if (!result.EndOfMessage) continue;
            using var document = JsonDocument.Parse(output.WrittenMemory.ToArray());
            output.Clear();
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var responseType) && responseType.GetString() == "error" && type != "error")
                throw new InvalidOperationException($"Server returned an error while waiting for '{type}': {root.GetRawText()}");
            if (root.TryGetProperty("type", out var actualType) && actualType.GetString() == type && predicate(root))
                return JsonDocument.Parse(root.GetRawText());
        }
    }

    private sealed class TestPlayer(WebSocket socket, Guid ownerId, Guid characterId, string name)
    {
        public WebSocket Socket { get; } = socket;
        public Guid OwnerId { get; } = ownerId;
        public Guid CharacterId { get; } = characterId;
        public string Name { get; } = name;
        public int Sequence { get; set; }
    }

    private sealed class StubAuthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var userId = token.Length >= 32 && Guid.TryParseExact(token[..32], "N", out var parsed) ? parsed : Guid.Empty;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { id = userId }), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}

