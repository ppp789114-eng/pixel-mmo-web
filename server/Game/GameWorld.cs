using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PixelMmo.Server.Persistence;

namespace PixelMmo.Server.Game;

public sealed class GameWorld : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GameRepository _repository;
    private readonly ILogger<GameWorld> _logger;
    private readonly ConcurrentDictionary<Guid, GameSession> _sessions = new();
    private readonly object _monsterGate = new();
    private readonly SemaphoreSlim _enterGate = new(1, 1);
    private readonly int _snapshotIntervalMs;
    private readonly int _moveCooldownMs;
    private readonly int _attackCooldownMs;
    private int _monsterHp = 24;
    private bool _monsterAlive = true;
    private DateTime _respawnAt = DateTime.MaxValue;
    private Guid _monsterSpawn = Guid.NewGuid();
    private DateTime _lastSaveAt = DateTime.UtcNow;

    public GameWorld(GameRepository repository, IConfiguration configuration, ILogger<GameWorld> logger)
    {
        _repository = repository;
        _logger = logger;
        _snapshotIntervalMs = Math.Clamp(configuration.GetValue("Game:SnapshotIntervalMs", 100), 50, 1000);
        _moveCooldownMs = Math.Clamp(configuration.GetValue("Game:MoveCooldownMs", 140), 80, 1000);
        _attackCooldownMs = Math.Clamp(configuration.GetValue("Game:AttackCooldownMs", 650), 250, 3000);
    }

    public async Task<GameSession> EnterAsync(Guid ownerId, GameCharacter character, WebSocket socket, CancellationToken cancellationToken)
    {
        await _enterGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(character.Id, out var previous))
            {
                try { await _repository.SaveCharacterAsync(previous.Character, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Could not save character {CharacterId} before reconnect", character.Id);
                }
                _sessions.TryRemove(new KeyValuePair<Guid, GameSession>(character.Id, previous));
                await TryCloseAsync(previous.Socket, WebSocketCloseStatus.NormalClosure, "reconnected", cancellationToken);
                character = await _repository.GetOwnedCharacterAsync(ownerId, character.Id, cancellationToken) ?? character;
            }
            var session = new GameSession(socket, ownerId, character);
            var entered = new EnteredWorldMessage(
                "entered-world", GameRules.ProtocolVersion, character.MapId, ToPlayer(character),
                await _repository.GetInventoryAsync(character.Id, cancellationToken),
                await _repository.GetEquipmentAsync(character.Id, cancellationToken),
                await _repository.GetStarterQuestAsync(character.Id, cancellationToken),
                PlayerSnapshot(character.MapId), MonsterSnapshot(character.MapId));
            await SendAsync(session, entered, cancellationToken);
            _sessions[character.Id] = session;
            return session;
        }
        finally { _enterGate.Release(); }
    }

    public async Task LeaveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var session = _sessions.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Socket, socket));
        if (session is null) return;
        _sessions.TryRemove(new KeyValuePair<Guid, GameSession>(session.Character.Id, session));
        try { await _repository.SaveCharacterAsync(session.Character, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not save character {CharacterId} on disconnect", session.Character.Id);
        }
    }

    public async Task HandleMessageAsync(GameSession session, JsonElement message, CancellationToken cancellationToken)
    {
        switch (ReadString(message, "type"))
        {
            case "move": await MoveAsync(session, message, cancellationToken); break;
            case "change-map": await ChangeMapAsync(session, message, cancellationToken); break;
            case "attack": await AttackAsync(session, message, starbolt: false, cancellationToken); break;
            case "cast":
                if (ReadString(message, "skillKey") != "starbolt") await SendErrorAsync(session, "unknown-skill", "That skill is not available.", cancellationToken);
                else await AttackAsync(session, message, starbolt: true, cancellationToken);
                break;
            case "chat": await ChatAsync(session, message, cancellationToken); break;
            case "interact": await InteractAsync(session, message, cancellationToken); break;
            case "equip": await EquipAsync(session, message, cancellationToken); break;
            case "unequip": await UnequipAsync(session, message, cancellationToken); break;
            default: await SendErrorAsync(session, "unknown-command", "That game command is not supported.", cancellationToken); break;
        }
    }

    public Task SendErrorAsync(GameSession session, string code, string message, CancellationToken cancellationToken) =>
        SendAsync(session, new ErrorMessage("error", code, message), cancellationToken);

    private async Task MoveAsync(GameSession session, JsonElement message, CancellationToken cancellationToken)
    {
        if (!TryReadInt(message, "dx", out var dx) || !TryReadInt(message, "dy", out var dy) || !TryReadInt(message, "sequence", out var sequence) ||
            (dx != -1 && dx != 1) || (dy != -1 && dy != 1) || sequence <= session.LastMoveSequence)
        {
            await SendErrorAsync(session, "invalid-move", "Movement must use one diagonal step.", cancellationToken);
            return;
        }
        session.LastMoveSequence = sequence;
        var now = DateTime.UtcNow;
        if ((now - session.LastMoveAt).TotalMilliseconds < _moveCooldownMs) return;
        session.LastMoveAt = now;
        var (x, y) = GameRules.Step(session.Character.MapId, session.Character.X, session.Character.Y, dx, dy);
        if (x != session.Character.X || y != session.Character.Y)
            session.Character = session.Character with { X = x, Y = y };
    }

    private async Task ChangeMapAsync(GameSession session, JsonElement message, CancellationToken cancellationToken)
    {
        var targetMap = ReadString(message, "mapId");
        if (!GameMaps.Exists(targetMap) || !GameMaps.CanTravel(session.Character.MapId, targetMap, session.Character.X, session.Character.Y))
        {
            await SendErrorAsync(session, "invalid-map-transition", "Move to the marked gate before changing maps.", cancellationToken);
            return;
        }
        var (x, y) = GameMaps.Spawn(targetMap);
        session.Character = session.Character with { MapId = targetMap, X = x, Y = y };
        await _repository.SaveCharacterAsync(session.Character, cancellationToken);
        await SendAsync(session, new MapChangedMessage("map-changed", targetMap, ToPlayer(session.Character), PlayerSnapshot(targetMap), MonsterSnapshot(targetMap)), cancellationToken);
        await BroadcastStateAsync(cancellationToken);
    }

    private async Task AttackAsync(GameSession session, JsonElement message, bool starbolt, CancellationToken cancellationToken)
    {
        var commandId = ReadString(message, "commandId");
        if (!Guid.TryParse(commandId, out _) || session.ProcessedCommands.Contains(commandId))
        {
            await SendErrorAsync(session, "invalid-command-id", "The attack command is invalid or already processed.", cancellationToken);
            return;
        }
        if (session.Character.MapId != GameMaps.Field || ReadString(message, "targetId") != "moss-slime-01")
        {
            await SendErrorAsync(session, "invalid-target", "That target is not in this field.", cancellationToken);
            return;
        }
        var now = DateTime.UtcNow;
        if ((now - session.LastAttackAt).TotalMilliseconds < _attackCooldownMs) return;
        if (starbolt && session.Character.Mp < GameRules.StarboltManaCost)
        {
            await SendErrorAsync(session, "not-enough-mana", "Not enough mana for Starbolt.", cancellationToken);
            return;
        }

        MonsterState monster;
        var accepted = false;
        var killed = false;
        string? spawnKey = null;
        lock (_monsterGate)
        {
            monster = CurrentMonster();
            var range = starbolt ? GameRules.StarboltRange : GameRules.BasicAttackRange;
            if (_monsterAlive && GameRules.IsometricDistance(session.Character.X, session.Character.Y, monster.X, monster.Y) <= range)
            {
                accepted = true;
                session.LastAttackAt = now;
                if (starbolt) session.Character = session.Character with { Mp = session.Character.Mp - GameRules.StarboltManaCost };
                _monsterHp = Math.Max(0, _monsterHp - GameRules.Damage(session.Character.Job, starbolt));
                if (_monsterHp == 0)
                {
                    _monsterAlive = false;
                    _respawnAt = now.AddSeconds(12);
                    killed = true;
                    spawnKey = _monsterSpawn.ToString("N");
                }
                monster = CurrentMonster();
            }
        }
        if (!accepted)
        {
            await SendErrorAsync(session, monster.Alive ? "out-of-range" : "target-defeated", monster.Alive ? "Move closer to the creature first." : "That creature has already been defeated.", cancellationToken);
            return;
        }
        RememberCommand(session, commandId);

        if (killed)
        {
            var rewardKey = $"moss-slime:{spawnKey}:{session.Character.Id:N}";
            try
            {
                var reward = await _repository.AwardRewardAsync(session.Character.Id, rewardKey, "moss-slime", 20, "slime_essence", 1, cancellationToken);
                session.Character = session.Character with { Experience = reward.Character.Experience, Level = reward.Character.Level };
                await _repository.ProgressStarterQuestAsync(session.Character.Id, cancellationToken);
                await SendAsync(session, new InventoryMessage("inventory", reward.Inventory), cancellationToken);
                await SendAsync(session, new QuestMessage("quest", await _repository.GetStarterQuestAsync(session.Character.Id, cancellationToken)), cancellationToken);
                await BroadcastChatAsync(session.Character.MapId, "SYSTEM", $"{session.Character.Name} defeated the Moss Slime and found Slime Essence!", true, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Could not grant Moss Slime reward to character {CharacterId}", session.Character.Id);
                await SendErrorAsync(session, "reward-save-failed", "The reward could not be saved. Please reconnect before retrying.", cancellationToken);
            }
        }
        else
        {
            var skillName = starbolt ? "Starbolt" : "Strike";
            await BroadcastChatAsync(session.Character.MapId, "SYSTEM", $"{session.Character.Name} used {skillName} on the Moss Slime.", true, cancellationToken);
        }
        await BroadcastStateAsync(cancellationToken);
    }

    private async Task InteractAsync(GameSession session, JsonElement message, CancellationToken cancellationToken)
    {
        if (session.Character.MapId != GameMaps.Town || ReadString(message, "targetId") != "elder-lyra" ||
            GameRules.IsometricDistance(session.Character.X, session.Character.Y, 5, 8) > 1)
        {
            await SendErrorAsync(session, "invalid-interaction", "Move next to Elder Lyra in Lumen Town to interact.", cancellationToken);
            return;
        }
        var quest = await _repository.GetStarterQuestAsync(session.Character.Id, cancellationToken);
        if (quest.Status == "inactive") quest = await _repository.AcceptStarterQuestAsync(session.Character.Id, cancellationToken);
        else if (quest.Status == "complete")
        {
            var reward = await _repository.ClaimStarterQuestAsync(session.Character.Id, cancellationToken);
            session.Character = session.Character with { Experience = reward.Character.Experience, Level = reward.Character.Level };
            quest = await _repository.GetStarterQuestAsync(session.Character.Id, cancellationToken);
            await SendAsync(session, new InventoryMessage("inventory", reward.Inventory), cancellationToken);
            await SendAsync(session, new QuestRewardMessage("quest-reward", 30, reward.Applied), cancellationToken);
            await BroadcastStateAsync(cancellationToken);
        }
        await SendAsync(session, new QuestMessage("quest", quest), cancellationToken);
    }

    private async Task EquipAsync(GameSession session, JsonElement message, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _repository.EquipAsync(session.Character.Id, ReadString(message, "itemKey"), cancellationToken);
            await SendAsync(session, new EquipmentMessage("equipment", result.Inventory, result.Equipment), cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await SendErrorAsync(session, "equip-failed", exception.Message, cancellationToken);
        }
    }

    private async Task UnequipAsync(GameSession session, JsonElement message, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _repository.UnequipAsync(session.Character.Id, ReadString(message, "slot"), cancellationToken);
            await SendAsync(session, new EquipmentMessage("equipment", result.Inventory, result.Equipment), cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await SendErrorAsync(session, "unequip-failed", exception.Message, cancellationToken);
        }
    }

    private async Task ChatAsync(GameSession session, JsonElement message, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if ((now - session.LastChatAt).TotalMilliseconds < 700) return;
        session.LastChatAt = now;
        var text = ReadString(message, "text").Trim();
        if (text.Length == 0) return;
        text = new string(text.Where(character => !char.IsControl(character)).Take(160).ToArray());
        if (text.Length > 0) await BroadcastChatAsync(session.Character.MapId, session.Character.Name, text, false, cancellationToken);
    }

    private async Task BroadcastChatAsync(string mapId, string name, string text, bool system, CancellationToken cancellationToken)
    {
        var message = new ChatMessage("chat", name, text, system);
        await Task.WhenAll(_sessions.Values.Where(session => session.Character.MapId == mapId).Select(session => SendAsync(session, message, cancellationToken)));
    }

    private async Task BroadcastStateAsync(CancellationToken cancellationToken)
    {
        foreach (var mapId in _sessions.Values.Select(session => session.Character.MapId).Distinct(StringComparer.Ordinal))
        {
            var message = new WorldStateMessage("world-state", mapId, PlayerSnapshot(mapId), MonsterSnapshot(mapId), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await Task.WhenAll(_sessions.Values.Where(session => session.Character.MapId == mapId).Select(session => SendAsync(session, message, cancellationToken)));
        }
    }

    private async Task SendAsync<T>(GameSession session, T message, CancellationToken cancellationToken)
    {
        if (session.Socket.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await session.SendGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Socket.State == WebSocketState.Open)
                await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException) { }
        finally { session.SendGate.Release(); }
    }

    private IReadOnlyList<PlayerState> PlayerSnapshot(string mapId) => _sessions.Values
        .Where(session => session.Character.MapId == mapId).Select(session => ToPlayer(session.Character)).ToArray();

    private IReadOnlyList<MonsterState> MonsterSnapshot(string mapId)
    {
        if (mapId != GameMaps.Field) return [];
        lock (_monsterGate) return [CurrentMonster()];
    }

    private MonsterState CurrentMonster() => new("moss-slime-01", "moss-slime", 9, 8, _monsterAlive ? _monsterHp : 0, 24, _monsterAlive);

    private static PlayerState ToPlayer(GameCharacter character) => new(
        character.Id, character.Name, character.Job, character.Level, character.Experience, character.MapId,
        character.X, character.Y, character.Hp, character.Mp, character.Gold);

    private static bool TryReadInt(JsonElement element, string property, out int result)
    {
        result = 0;
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out result);
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static void RememberCommand(GameSession session, string commandId)
    {
        session.ProcessedCommands.Add(commandId);
        session.ProcessedCommandOrder.Enqueue(commandId);
        if (session.ProcessedCommandOrder.Count > 512)
            session.ProcessedCommands.Remove(session.ProcessedCommandOrder.Dequeue());
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Open)
        {
            try { await socket.CloseAsync(status, description, cancellationToken); }
            catch (WebSocketException) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(_snapshotIntervalMs, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            var now = DateTime.UtcNow;
            lock (_monsterGate)
            {
                if (!_monsterAlive && now >= _respawnAt)
                {
                    _monsterAlive = true;
                    _monsterHp = 24;
                    _monsterSpawn = Guid.NewGuid();
                }
            }
            if (_sessions.IsEmpty) continue;
            try { await BroadcastStateAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { _logger.LogWarning(exception, "World snapshot broadcast failed"); }

            if ((now - _lastSaveAt).TotalSeconds >= 10)
            {
                _lastSaveAt = now;
                foreach (var session in _sessions.Values)
                {
                    try { await _repository.SaveCharacterAsync(session.Character, stoppingToken); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogWarning(exception, "Could not autosave character {CharacterId}", session.Character.Id);
                    }
                }
            }
        }
    }
}

public sealed record EnteredWorldMessage(string Type, int ProtocolVersion, string MapId, PlayerState Character, IReadOnlyList<InventoryItem> Inventory, IReadOnlyList<EquipmentState> Equipment, QuestState Quest, IReadOnlyList<PlayerState> Players, IReadOnlyList<MonsterState> Monsters);
public sealed record MapChangedMessage(string Type, string MapId, PlayerState Character, IReadOnlyList<PlayerState> Players, IReadOnlyList<MonsterState> Monsters);
public sealed record InventoryMessage(string Type, IReadOnlyList<InventoryItem> Inventory);
public sealed record EquipmentMessage(string Type, IReadOnlyList<InventoryItem> Inventory, IReadOnlyList<EquipmentState> Equipment);
public sealed record QuestMessage(string Type, QuestState Quest);
public sealed record QuestRewardMessage(string Type, long Experience, bool Applied);
public sealed record ChatMessage(string Type, string Name, string Text, bool System);
public sealed record ErrorMessage(string Type, string Code, string Message);
