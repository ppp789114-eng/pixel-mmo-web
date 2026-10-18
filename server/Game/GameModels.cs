namespace PixelMmo.Server.Game;

public sealed record GameCharacter(
    Guid Id,
    Guid OwnerId,
    string Name,
    string Job,
    int Level,
    long Experience,
    string MapId,
    int X,
    int Y,
    int Hp,
    int Mp,
    long Gold);

public sealed record InventoryItem(string ItemKey, int Quantity);

public sealed record PlayerState(
    Guid Id,
    string Name,
    string Job,
    int Level,
    long Experience,
    string MapId,
    int X,
    int Y,
    int Hp,
    int Mp,
    long Gold);

public sealed record MonsterState(
    string Id,
    string Kind,
    int X,
    int Y,
    int Hp,
    int MaxHp,
    bool Alive);

public sealed record WorldStateMessage(
    string Type,
    string MapId,
    IReadOnlyList<PlayerState> Players,
    IReadOnlyList<MonsterState> Monsters,
    long ServerTime);

public sealed record EquipmentState(string Slot, string ItemKey);

public sealed record QuestState(string QuestKey, string Status, int Progress);

public sealed record RewardResult(GameCharacter Character, IReadOnlyList<InventoryItem> Inventory, bool Applied);

public sealed record EquipmentResult(IReadOnlyList<InventoryItem> Inventory, IReadOnlyList<EquipmentState> Equipment);

public static class GameMaps
{
    public const string Town = "lumen-town";
    public const string Field = "verdant-field";

    public static bool Exists(string mapId) => mapId is Town or Field;

    public static (int X, int Y) Spawn(string mapId) => mapId switch
    {
        Town => (7, 8),
        Field => (1, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(mapId)),
    };

    public static bool CanTravel(string from, string to, int x, int y) =>
        (from == Town && to == Field && x == 14 && y == 7) ||
        (from == Field && to == Town && x == 0 && y == 9);
}

public static class EquipmentRules
{
    public static string StarterWeapon(string job) => job == "Mage" ? "oak_staff" : "iron_sword";

    public static string? SlotFor(string itemKey) => itemKey is "oak_staff" or "iron_sword" ? "weapon" : null;
}

public static class GameRules
{
    public const int ProtocolVersion = 2;
    public const int MapSize = 16;
    public const int BasicAttackRange = 2;
    public const int StarboltRange = 5;
    public const int StarboltManaCost = 6;

    public static bool IsWalkable(int x, int y) =>
        IsWalkable(GameMaps.Field, x, y);

    public static bool IsWalkable(string mapId, int x, int y) =>
        x >= 0 && y >= 0 && x < MapSize && y < MapSize && (mapId != GameMaps.Field || !(x <= 2 && y <= 3));

    public static (int X, int Y) Step(int x, int y, int dx, int dy)
        => Step(GameMaps.Field, x, y, dx, dy);

    public static (int X, int Y) Step(string mapId, int x, int y, int dx, int dy)
    {
        var safeX = Math.Clamp(dx, -1, 1);
        var safeY = Math.Clamp(dy, -1, 1);
        if (Math.Abs(safeX) != 1 || Math.Abs(safeY) != 1) return (x, y);
        var candidate = (X: x + safeX, Y: y + safeY);
        return IsWalkable(mapId, candidate.X, candidate.Y) ? candidate : (x, y);
    }

    public static int IsometricDistance(int x1, int y1, int x2, int y2) =>
        Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    public static int Damage(string job, bool starbolt) => starbolt ? 14 : job == "Mage" ? 5 : 8;

    public static int LevelForExperience(long experience)
    {
        var level = 1;
        var remaining = experience;
        while (level < 99 && remaining >= level * 100L)
        {
            remaining -= level * 100L;
            level++;
        }
        return level;
    }

    public static bool IsValidCharacterName(string name) =>
        name.Length is >= 3 and <= 18 && name.All(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '-');

    public static bool IsValidJob(string job) => job is "Swordsman" or "Mage";
}
