using PixelMmo.Server.Game;
using Xunit;

namespace PixelMmo.Server.Tests;

public sealed class GameRepositoryTests
{
    [Fact]
    public async Task CharacterLimitSurvivesConcurrentCreates()
    {
        var repository = TestDatabase.CreateRepository();
        var owner = Guid.NewGuid();
        await TestDatabase.SeedOwnerAsync(owner);
        var attempts = Enumerable.Range(0, 12).Select(async index =>
        {
            try { await repository.CreateCharacterAsync(owner, $"Hero{index}", "Swordsman"); }
            catch (InvalidOperationException) { }
        });

        await Task.WhenAll(attempts);

        Assert.Equal(3, (await repository.GetCharactersAsync(owner)).Count);
    }

    [Fact]
    public async Task RepeatedRewardAndStaleAutosaveDoNotLoseOrDuplicateProgress()
    {
        var repository = TestDatabase.CreateRepository();
        var owner = Guid.NewGuid();
        await TestDatabase.SeedOwnerAsync(owner);
        var character = await repository.CreateCharacterAsync(owner, "Mira", "Mage");
        var first = await repository.AwardRewardAsync(character.Id, "drop:spawn-1", "moss-slime", 20, "slime_essence", 1);
        var duplicate = await repository.AwardRewardAsync(character.Id, "drop:spawn-1", "moss-slime", 20, "slime_essence", 1);
        await repository.SaveCharacterAsync(character with { X = 9, Experience = 0, Level = 1 });

        var reloaded = string.IsNullOrWhiteSpace(TestDatabase.ConnectionString) ? repository : TestDatabase.CreateRepository();
        var saved = Assert.Single(await reloaded.GetCharactersAsync(character.OwnerId));
        var essence = Assert.Single(await reloaded.GetInventoryAsync(character.Id), item => item.ItemKey == "slime_essence");
        Assert.True(first.Applied);
        Assert.False(duplicate.Applied);
        Assert.Equal(20L, saved.Experience);
        Assert.Equal(9, saved.X);
        Assert.Equal(1, essence.Quantity);
    }

    [Fact]
    public async Task StarterWeaponCanBeEquippedAndReturned()
    {
        var repository = TestDatabase.CreateRepository();
        var owner = Guid.NewGuid();
        await TestDatabase.SeedOwnerAsync(owner);
        var character = await repository.CreateCharacterAsync(owner, "Ivo", "Swordsman");

        var equipped = await repository.EquipAsync(character.Id, "iron_sword");
        var unequipped = await repository.UnequipAsync(character.Id, "weapon");

        Assert.Equal("iron_sword", Assert.Single(equipped.Equipment).ItemKey);
        Assert.Empty(unequipped.Equipment);
        Assert.Equal(1, Assert.Single(unequipped.Inventory).Quantity);
    }

    [Fact]
    public async Task StarterQuestRewardCanOnlyBeClaimedOnce()
    {
        var repository = TestDatabase.CreateRepository();
        var owner = Guid.NewGuid();
        await TestDatabase.SeedOwnerAsync(owner);
        var character = await repository.CreateCharacterAsync(owner, "Tavi", "Mage");
        await repository.AcceptStarterQuestAsync(character.Id);
        await repository.ProgressStarterQuestAsync(character.Id);

        var first = await repository.ClaimStarterQuestAsync(character.Id);
        var repeated = await repository.ClaimStarterQuestAsync(character.Id);

        Assert.True(first.Applied);
        Assert.False(repeated.Applied);
        Assert.Equal(30L, repeated.Character.Experience);
        Assert.Equal(1, Assert.Single(repeated.Inventory, item => item.ItemKey == "moonstone").Quantity);
    }

    [Fact]
    public void MapTravelRequiresTheMatchingGate()
    {
        var (townX, townY) = (7, 8);
        for (var step = 0; step < 7; step++)
        {
            var next = GameRules.Step(GameMaps.Town, townX, townY, 1, step < 4 ? -1 : 1);
            (townX, townY) = next;
        }
        var (fieldX, fieldY) = GameMaps.Spawn(GameMaps.Field);
        (fieldX, fieldY) = GameRules.Step(GameMaps.Field, fieldX, fieldY, -1, 1);

        Assert.Equal((14, 7), (townX, townY));
        Assert.Equal((0, 9), (fieldX, fieldY));
        Assert.True(GameMaps.CanTravel(GameMaps.Town, GameMaps.Field, 14, 7));
        Assert.False(GameMaps.CanTravel(GameMaps.Town, GameMaps.Field, 13, 7));
        Assert.True(GameMaps.CanTravel(GameMaps.Field, GameMaps.Town, 0, 9));
        Assert.False(GameMaps.CanTravel(GameMaps.Field, GameMaps.Town, 1, 9));
    }
}
