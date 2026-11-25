using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TicTacToe.Data;
using Xunit;

namespace TicTacToe.Tests;

public sealed class DataMigrationTests
{
    [Fact]
    public async Task RemovingLegacyAccountsPreservesGameHistory()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(databaseDirectory, "tictactoe.db3");
        try
        {
            Assert.False(Directory.Exists(databaseDirectory));
            await using (var db = new AppDbContext(databasePath))
            {
                await db.Database.MigrateAsync("20261027100000_AddLocalAccounts");
                var x = new PlayerEntity { Id = Guid.NewGuid(), Name = "X", CreatedAt = DateTime.UtcNow };
                var o = new PlayerEntity { Id = Guid.NewGuid(), Name = "O", CreatedAt = DateTime.UtcNow };
                var game = new GameEntity
                {
                    Id = Guid.NewGuid(),
                    PlayerX = x,
                    PlayerO = o,
                    PlayerXId = x.Id,
                    PlayerOId = o.Id,
                    Result = "Draw",
                    ConnectionMode = "LocalHost",
                    StartedAt = DateTime.UtcNow,
                    FinishedAt = DateTime.UtcNow,
                    FinalBoard = "[]"
                };
                db.Games.Add(game);
                await db.SaveChangesAsync();

                Assert.Equal(2, await db.Players.CountAsync());
                Assert.Equal(1, await db.Games.CountAsync());
                await db.Database.MigrateAsync();
                var accountTableCount = await db.Database
                    .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'Accounts'")
                    .SingleAsync();

                Assert.Equal(1, await db.Games.CountAsync());
                Assert.Equal(2, await db.Players.CountAsync());
                Assert.Equal(0, accountTableCount);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
            if (Directory.Exists(databaseDirectory))
                Directory.Delete(databaseDirectory, recursive: true);
        }
    }
}
