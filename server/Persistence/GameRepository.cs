using Npgsql;
using PixelMmo.Server.Game;

namespace PixelMmo.Server.Persistence;

public sealed class GameRepository
{
    private readonly string? _connectionString;
    private readonly object _memoryGate = new();
    private readonly Dictionary<Guid, GameCharacter> _characters = [];
    private readonly Dictionary<Guid, Dictionary<string, int>> _inventory = [];
    private readonly Dictionary<Guid, Dictionary<string, string>> _equipment = [];
    private readonly Dictionary<Guid, QuestState> _quests = [];
    private readonly HashSet<(Guid CharacterId, string RewardKey)> _receipts = [];

    public GameRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("GameDatabase");
        if (string.IsNullOrWhiteSpace(_connectionString)) _connectionString = null;
    }

    public bool IsPersistent => _connectionString is not null;

    public async Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate)
                return _characters.Values.Where(character => character.OwnerId == ownerId).OrderBy(character => character.Name).ToArray();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id, owner_id, name, job, level, experience, map_id, x, y, hp, mp, gold
            from public.game_characters where owner_id = @owner order by created_at
            """, connection);
        command.Parameters.AddWithValue("owner", ownerId);
        var characters = new List<GameCharacter>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) characters.Add(ReadCharacter(reader));
        return characters;
    }

    public async Task<GameCharacter> CreateCharacterAsync(Guid ownerId, string name, string job, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (!GameRules.IsValidCharacterName(name)) throw new InvalidOperationException("Name must be 3–18 letters, numbers, spaces, underscores, or hyphens.");
        if (!GameRules.IsValidJob(job)) throw new InvalidOperationException("Choose a supported starting job.");

        if (!IsPersistent)
        {
            lock (_memoryGate)
            {
                if (_characters.Values.Count(character => character.OwnerId == ownerId) >= 3)
                    throw new InvalidOperationException("You can create up to 3 characters.");
                if (_characters.Values.Any(character => character.OwnerId == ownerId && string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("You already have a character with that name.");
                var created = NewCharacter(ownerId, name, job);
                _characters.Add(created.Id, created);
                _inventory.Add(created.Id, new Dictionary<string, int>(StringComparer.Ordinal) { [EquipmentRules.StarterWeapon(job)] = 1 });
                return created;
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Serialize account character creation so count-then-insert cannot exceed the cap.
        await using (var accountLock = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@owner::text, 0))", connection, transaction))
        {
            accountLock.Parameters.AddWithValue("owner", ownerId);
            await accountLock.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var count = new NpgsqlCommand("select count(*) from public.game_characters where owner_id = @owner", connection, transaction))
        {
            count.Parameters.AddWithValue("owner", ownerId);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L) >= 3)
                throw new InvalidOperationException("You can create up to 3 characters.");
        }

        var character = NewCharacter(ownerId, name, job);
        try
        {
            await using var command = new NpgsqlCommand("""
                insert into public.game_characters (id, owner_id, name, job, level, experience, map_id, x, y, hp, mp, gold)
                values (@id, @owner, @name, @job, @level, @experience, @map, @x, @y, @hp, @mp, @gold)
                """, connection, transaction);
            AddCharacterParameters(command, character);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using (var item = new NpgsqlCommand("insert into public.game_inventory (character_id, item_key, quantity) values (@character, @item, 1)", connection, transaction))
            {
                item.Parameters.AddWithValue("character", character.Id);
                item.Parameters.AddWithValue("item", EquipmentRules.StarterWeapon(job));
                await item.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return character;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("You already have a character with that name.");
        }
    }

    public async Task<GameCharacter?> GetOwnedCharacterAsync(Guid ownerId, Guid characterId, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate)
                return _characters.TryGetValue(characterId, out var character) && character.OwnerId == ownerId ? character : null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id, owner_id, name, job, level, experience, map_id, x, y, hp, mp, gold
            from public.game_characters where id = @id and owner_id = @owner
            """, connection);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("owner", ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    public async Task<IReadOnlyList<InventoryItem>> GetInventoryAsync(Guid characterId, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate) return InventoryFor(characterId);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select item_key, quantity from public.game_inventory where character_id = @id order by item_key", connection);
        command.Parameters.AddWithValue("id", characterId);
        var items = new List<InventoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new InventoryItem(reader.GetString(0), reader.GetInt32(1)));
        return items;
    }

    public async Task<IReadOnlyList<EquipmentState>> GetEquipmentAsync(Guid characterId, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate) return EquipmentFor(characterId);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select slot, item_key from public.game_character_equipment where character_id = @id order by slot", connection);
        command.Parameters.AddWithValue("id", characterId);
        var equipment = new List<EquipmentState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) equipment.Add(new EquipmentState(reader.GetString(0), reader.GetString(1)));
        return equipment;
    }

    public async Task<QuestState> GetStarterQuestAsync(Guid characterId, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate) return _quests.GetValueOrDefault(characterId) ?? new QuestState("first-slime", "inactive", 0);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select quest_key, status, progress from public.game_character_quests where character_id = @id and quest_key = 'first-slime'", connection);
        command.Parameters.AddWithValue("id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new QuestState(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)) : new QuestState("first-slime", "inactive", 0);
    }

    public async Task<QuestState> AcceptStarterQuestAsync(Guid characterId, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate)
            {
                if (!_quests.ContainsKey(characterId)) _quests[characterId] = new QuestState("first-slime", "active", 0);
                return _quests[characterId];
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into public.game_character_quests (character_id, quest_key, status, progress)
            values (@id, 'first-slime', 'active', 0) on conflict (character_id, quest_key) do nothing
            """, connection);
        command.Parameters.AddWithValue("id", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetStarterQuestAsync(characterId, cancellationToken);
    }

    public async Task<QuestState> ProgressStarterQuestAsync(Guid characterId, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate)
            {
                if (_quests.GetValueOrDefault(characterId)?.Status == "active")
                    _quests[characterId] = new QuestState("first-slime", "complete", 1);
                return _quests.GetValueOrDefault(characterId) ?? new QuestState("first-slime", "inactive", 0);
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            update public.game_character_quests set status = 'complete', progress = 1, updated_at = now()
            where character_id = @id and quest_key = 'first-slime' and status = 'active'
            """, connection);
        command.Parameters.AddWithValue("id", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetStarterQuestAsync(characterId, cancellationToken);
    }

    public async Task<RewardResult> ClaimStarterQuestAsync(Guid characterId, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate)
            {
                var quest = _quests.GetValueOrDefault(characterId) ?? new QuestState("first-slime", "inactive", 0);
                if (quest.Status != "complete")
                    return new RewardResult(_characters[characterId], InventoryFor(characterId), false);
                _quests[characterId] = quest with { Status = "claimed" };
                return ApplyMemoryReward(characterId, $"quest:first-slime:{characterId:N}", 30, "moonstone", 1);
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var quest = new NpgsqlCommand("""
            update public.game_character_quests set status = 'claimed', updated_at = now()
            where character_id = @id and quest_key = 'first-slime' and status = 'complete'
            returning quest_key
            """, connection, transaction))
        {
            quest.Parameters.AddWithValue("id", characterId);
            if (await quest.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                var current = await GetCharacterByIdAsync(characterId, cancellationToken) ?? throw new InvalidOperationException("Character not found.");
                return new RewardResult(current, await GetInventoryAsync(characterId, cancellationToken), false);
            }
        }

        var reward = await ApplyDatabaseRewardAsync(connection, transaction, characterId, $"quest:first-slime:{characterId:N}", "quest", 30, "moonstone", 1, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reward with { Inventory = await GetInventoryAsync(characterId, cancellationToken) };
    }

    public async Task<EquipmentResult> EquipAsync(Guid characterId, string itemKey, CancellationToken cancellationToken = default)
    {
        var slot = EquipmentRules.SlotFor(itemKey) ?? throw new InvalidOperationException("That item cannot be equipped.");
        if (!IsPersistent)
        {
            lock (_memoryGate)
            {
                if (!_inventory.TryGetValue(characterId, out var inventory) || inventory.GetValueOrDefault(itemKey) < 1)
                    throw new InvalidOperationException("That item is not in your inventory.");
                var equipment = _equipment.GetValueOrDefault(characterId) ?? (_equipment[characterId] = new Dictionary<string, string>(StringComparer.Ordinal));
                if (equipment.TryGetValue(slot, out var previous)) inventory[previous] = inventory.GetValueOrDefault(previous) + 1;
                inventory[itemKey]--;
                if (inventory[itemKey] == 0) inventory.Remove(itemKey);
                equipment[slot] = itemKey;
                return new EquipmentResult(InventoryFor(characterId), EquipmentFor(characterId));
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var item = new NpgsqlCommand("select quantity from public.game_inventory where character_id = @id and item_key = @item for update", connection, transaction))
        {
            item.Parameters.AddWithValue("id", characterId);
            item.Parameters.AddWithValue("item", itemKey);
            if ((int?)await item.ExecuteScalarAsync(cancellationToken) is not > 0)
                throw new InvalidOperationException("That item is not in your inventory.");
        }
        string? previousItem = null;
        await using (var current = new NpgsqlCommand("select item_key from public.game_character_equipment where character_id = @id and slot = @slot for update", connection, transaction))
        {
            current.Parameters.AddWithValue("id", characterId);
            current.Parameters.AddWithValue("slot", slot);
            previousItem = (string?)await current.ExecuteScalarAsync(cancellationToken);
        }
        await using (var updateItem = new NpgsqlCommand("""
            update public.game_inventory set quantity = quantity - 1, updated_at = now()
            where character_id = @id and item_key = @item
            """, connection, transaction))
        {
            updateItem.Parameters.AddWithValue("id", characterId);
            updateItem.Parameters.AddWithValue("item", itemKey);
            await updateItem.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deleteEmpty = new NpgsqlCommand("delete from public.game_inventory where character_id = @id and item_key = @item and quantity = 0", connection, transaction))
        {
            deleteEmpty.Parameters.AddWithValue("id", characterId);
            deleteEmpty.Parameters.AddWithValue("item", itemKey);
            await deleteEmpty.ExecuteNonQueryAsync(cancellationToken);
        }
        if (previousItem is not null) await ReturnItemAsync(connection, transaction, characterId, previousItem, cancellationToken);
        await using (var setGear = new NpgsqlCommand("""
            insert into public.game_character_equipment (character_id, slot, item_key) values (@id, @slot, @item)
            on conflict (character_id, slot) do update set item_key = excluded.item_key, updated_at = now()
            """, connection, transaction))
        {
            setGear.Parameters.AddWithValue("id", characterId);
            setGear.Parameters.AddWithValue("slot", slot);
            setGear.Parameters.AddWithValue("item", itemKey);
            await setGear.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new EquipmentResult(await GetInventoryAsync(characterId, cancellationToken), await GetEquipmentAsync(characterId, cancellationToken));
    }

    public async Task<EquipmentResult> UnequipAsync(Guid characterId, string slot, CancellationToken cancellationToken = default)
    {
        if (slot != "weapon") throw new InvalidOperationException("That equipment slot is not available.");
        if (!IsPersistent)
        {
            lock (_memoryGate)
            {
                if (_equipment.TryGetValue(characterId, out var equipment) && equipment.Remove(slot, out var previous))
                {
                    var inventory = _inventory[characterId];
                    inventory[previous] = inventory.GetValueOrDefault(previous) + 1;
                }
                return new EquipmentResult(InventoryFor(characterId), EquipmentFor(characterId));
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? previousItem;
        await using (var current = new NpgsqlCommand("delete from public.game_character_equipment where character_id = @id and slot = @slot returning item_key", connection, transaction))
        {
            current.Parameters.AddWithValue("id", characterId);
            current.Parameters.AddWithValue("slot", slot);
            previousItem = (string?)await current.ExecuteScalarAsync(cancellationToken);
        }
        if (previousItem is not null) await ReturnItemAsync(connection, transaction, characterId, previousItem, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EquipmentResult(await GetInventoryAsync(characterId, cancellationToken), await GetEquipmentAsync(characterId, cancellationToken));
    }

    public async Task SaveCharacterAsync(GameCharacter character, CancellationToken cancellationToken = default)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate)
                if (_characters.ContainsKey(character.Id)) _characters[character.Id] = _characters[character.Id] with
                {
                    MapId = character.MapId, X = character.X, Y = character.Y, Hp = character.Hp, Mp = character.Mp,
                };
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            update public.game_characters
            set map_id = @map, x = @x, y = @y, hp = @hp, mp = @mp, updated_at = now()
            where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", character.Id);
        command.Parameters.AddWithValue("map", character.MapId);
        command.Parameters.AddWithValue("x", character.X);
        command.Parameters.AddWithValue("y", character.Y);
        command.Parameters.AddWithValue("hp", character.Hp);
        command.Parameters.AddWithValue("mp", character.Mp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RewardResult> AwardRewardAsync(Guid characterId, string rewardKey, string rewardKind, long experience, string itemKey, int quantity, CancellationToken cancellationToken = default)
    {
        if (experience < 0 || quantity <= 0 || string.IsNullOrWhiteSpace(rewardKey) || string.IsNullOrWhiteSpace(itemKey))
            throw new ArgumentOutOfRangeException(nameof(quantity), "Reward values must be valid and positive.");
        if (!IsPersistent)
        {
            lock (_memoryGate) return ApplyMemoryReward(characterId, rewardKey, experience, itemKey, quantity);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var receipt = new NpgsqlCommand("""
            insert into public.game_reward_receipts (reward_key, character_id, reward_kind)
            values (@key, @character, @kind) on conflict (reward_key) do nothing returning reward_key
            """, connection, transaction))
        {
            receipt.Parameters.AddWithValue("key", rewardKey);
            receipt.Parameters.AddWithValue("character", characterId);
            receipt.Parameters.AddWithValue("kind", rewardKind);
            if (await receipt.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                var unchanged = await GetCharacterByIdAsync(characterId, cancellationToken) ?? throw new InvalidOperationException("Character not found.");
                return new RewardResult(unchanged, await GetInventoryAsync(characterId, cancellationToken), false);
            }
        }

        var result = await ApplyDatabaseRewardAsync(connection, transaction, characterId, rewardKey, rewardKind, experience, itemKey, quantity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result with { Inventory = await GetInventoryAsync(characterId, cancellationToken) };
    }

    private RewardResult ApplyMemoryReward(Guid characterId, string rewardKey, long experience, string itemKey, int quantity)
    {
        if (!_characters.TryGetValue(characterId, out var current)) throw new InvalidOperationException("Character not found.");
        if (!_receipts.Add((characterId, rewardKey))) return new RewardResult(current, InventoryFor(characterId), false);
        var updated = current with { Experience = current.Experience + experience };
        updated = updated with { Level = GameRules.LevelForExperience(updated.Experience) };
        _characters[characterId] = updated;
        if (!_inventory.TryGetValue(characterId, out var inventory)) _inventory[characterId] = inventory = new Dictionary<string, int>(StringComparer.Ordinal);
        inventory[itemKey] = inventory.GetValueOrDefault(itemKey) + quantity;
        return new RewardResult(updated, InventoryFor(characterId), true);
    }

    private static async Task<RewardResult> ApplyDatabaseRewardAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid characterId, string rewardKey, string rewardKind, long experience, string itemKey, int quantity, CancellationToken cancellationToken)
    {
        GameCharacter character;
        await using (var select = new NpgsqlCommand("""
            select id, owner_id, name, job, level, experience, map_id, x, y, hp, mp, gold
            from public.game_characters where id = @id for update
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("id", characterId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Character not found.");
            character = ReadCharacter(reader);
        }

        character = character with { Experience = character.Experience + experience };
        character = character with { Level = GameRules.LevelForExperience(character.Experience) };
        await using (var update = new NpgsqlCommand("update public.game_characters set level = @level, experience = @experience, updated_at = now() where id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("level", character.Level);
            update.Parameters.AddWithValue("experience", character.Experience);
            update.Parameters.AddWithValue("id", character.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var item = new NpgsqlCommand("""
            insert into public.game_inventory (character_id, item_key, quantity) values (@character, @item, @quantity)
            on conflict (character_id, item_key) do update set quantity = public.game_inventory.quantity + excluded.quantity, updated_at = now()
            """, connection, transaction))
        {
            item.Parameters.AddWithValue("character", characterId);
            item.Parameters.AddWithValue("item", itemKey);
            item.Parameters.AddWithValue("quantity", quantity);
            await item.ExecuteNonQueryAsync(cancellationToken);
        }
        if (rewardKind == "quest")
        {
            await using var receipt = new NpgsqlCommand("insert into public.game_reward_receipts (reward_key, character_id, reward_kind) values (@key, @character, @kind) on conflict (reward_key) do nothing", connection, transaction);
            receipt.Parameters.AddWithValue("key", rewardKey);
            receipt.Parameters.AddWithValue("character", characterId);
            receipt.Parameters.AddWithValue("kind", rewardKind);
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }
        return new RewardResult(character, [], true);
    }

    private static async Task ReturnItemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid characterId, string itemKey, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into public.game_inventory (character_id, item_key, quantity) values (@id, @item, 1)
            on conflict (character_id, item_key) do update set quantity = public.game_inventory.quantity + 1, updated_at = now()
            """, connection, transaction);
        command.Parameters.AddWithValue("id", characterId);
        command.Parameters.AddWithValue("item", itemKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<GameCharacter?> GetCharacterByIdAsync(Guid characterId, CancellationToken cancellationToken)
    {
        if (!IsPersistent)
        {
            lock (_memoryGate) return _characters.GetValueOrDefault(characterId);
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id, owner_id, name, job, level, experience, map_id, x, y, hp, mp, gold
            from public.game_characters where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    private IReadOnlyList<InventoryItem> InventoryFor(Guid characterId) =>
        _inventory.TryGetValue(characterId, out var items) ? items.Where(item => item.Value > 0).Select(item => new InventoryItem(item.Key, item.Value)).OrderBy(item => item.ItemKey).ToArray() : [];

    private IReadOnlyList<EquipmentState> EquipmentFor(Guid characterId) =>
        _equipment.TryGetValue(characterId, out var items) ? items.Select(item => new EquipmentState(item.Key, item.Value)).OrderBy(item => item.Slot).ToArray() : [];

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static GameCharacter NewCharacter(Guid ownerId, string name, string job)
    {
        var (x, y) = GameMaps.Spawn(GameMaps.Town);
        return new GameCharacter(Guid.NewGuid(), ownerId, name, job, 1, 0, GameMaps.Town, x, y, 100, 40, 0);
    }

    private static GameCharacter ReadCharacter(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
        reader.GetInt64(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt64(11));

    private static void AddCharacterParameters(NpgsqlCommand command, GameCharacter character)
    {
        command.Parameters.AddWithValue("id", character.Id);
        command.Parameters.AddWithValue("owner", character.OwnerId);
        command.Parameters.AddWithValue("name", character.Name);
        command.Parameters.AddWithValue("job", character.Job);
        command.Parameters.AddWithValue("level", character.Level);
        command.Parameters.AddWithValue("experience", character.Experience);
        command.Parameters.AddWithValue("map", character.MapId);
        command.Parameters.AddWithValue("x", character.X);
        command.Parameters.AddWithValue("y", character.Y);
        command.Parameters.AddWithValue("hp", character.Hp);
        command.Parameters.AddWithValue("mp", character.Mp);
        command.Parameters.AddWithValue("gold", character.Gold);
    }
}
