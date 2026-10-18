using Microsoft.Extensions.Configuration;
using Npgsql;
using PixelMmo.Server.Persistence;

namespace PixelMmo.Server.Tests;

internal static class TestDatabase
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("ConnectionStrings__GameDatabase");

    public static GameRepository CreateRepository() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GameDatabase"] = ConnectionString,
        })
        .Build());

    public static async Task SeedOwnerAsync(Guid ownerId)
    {
        var connectionString = ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("insert into auth.users (id) values (@id) on conflict (id) do nothing", connection);
        command.Parameters.AddWithValue("id", ownerId);
        await command.ExecuteNonQueryAsync();
    }
}
