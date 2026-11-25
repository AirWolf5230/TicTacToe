using Microsoft.EntityFrameworkCore;
using TicTacToe.Core;

namespace TicTacToe.Data;

public sealed record MoveRecord(int CellIndex, PlayerSymbol Symbol);
public sealed record HistoryRecord(
    string PlayerX, string PlayerO, string Result, string ConnectionMode,
    DateTime StartedAt, string FinalBoard, string? RoomId)
{
    public string Participants => $"{PlayerX} (X)  —  {PlayerO} (O)";
    public string ResultDisplay => Enum.TryParse<GameStatus>(Result, out var status)
        ? GameEngine.GetStatusText(status)
        : Result;
    public string BoardDisplay => string.Join(" | ",
        System.Text.Json.JsonSerializer.Deserialize<string[]>(FinalBoard) ?? new string[9]);
}
public sealed record PlayerStatistics(int Games, int Wins, int Losses, int Draws)
{
    public int WinRate => Games == 0 ? 0 : (int)Math.Round(Wins * 100d / Games);
}

public sealed class LocalGameRepository
{
    private readonly string _databasePath = Path.Combine(FileSystem.AppDataDirectory, "tictactoe.db3");
    private readonly Guid _profileId;

    public LocalGameRepository()
    {
        var storedProfileId = Preferences.Default.Get("profile_id", "");
        _profileId = Guid.TryParse(storedProfileId, out var profileId) && profileId != Guid.Empty
            ? profileId
            : Guid.NewGuid();
        Preferences.Default.Set("profile_id", _profileId.ToString());
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        var player = await db.Players.FindAsync(_profileId);
        if (player is null)
        {
            db.Players.Add(new PlayerEntity
            {
                Id = _profileId,
                Name = Preferences.Default.Get("player_name", "Игрок"),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    public async Task<string> GetPlayerNameAsync()
    {
        await using var db = CreateContext();
        return await db.Players.Where(player => player.Id == _profileId)
            .Select(player => player.Name).FirstOrDefaultAsync() ?? "Игрок";
    }

    public Task<bool> HasPlayerNameAsync() =>
        Task.FromResult(!string.IsNullOrWhiteSpace(Preferences.Default.Get("player_name", "")));

    public async Task SavePlayerNameAsync(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 24)
            throw new ArgumentException("Имя должно содержать от 1 до 24 символов.");
        await using var db = CreateContext();
        var player = await db.Players.FindAsync(_profileId)
            ?? throw new InvalidOperationException("Профиль не найден.");
        player.Name = normalized;
        await db.SaveChangesAsync();
        Preferences.Default.Set("player_name", normalized);
    }

    public async Task SaveFinishedGameAsync(
        string? roomId, string playerName, string opponentName, PlayerSymbol playerSymbol,
        GameState state, string mode, DateTime startedAt, IReadOnlyList<MoveRecord> moves)
    {
        if (!state.IsFinished)
            throw new InvalidOperationException("Сохранять можно только завершенный матч.");
        await using var db = CreateContext();
        if (!string.IsNullOrWhiteSpace(roomId) &&
            await db.Games.AnyAsync(game => game.RoomId == roomId))
            return;

        var profile = await db.Players.FindAsync(_profileId)
            ?? throw new InvalidOperationException("Профиль не найден.");
        var opponent = await db.Players.FirstOrDefaultAsync(player => player.Name == opponentName && player.Id != _profileId);
        if (opponent is null)
        {
            opponent = new PlayerEntity { Id = Guid.NewGuid(), Name = opponentName, CreatedAt = DateTime.UtcNow };
            db.Players.Add(opponent);
        }
        var xId = playerSymbol == PlayerSymbol.X ? profile.Id : opponent.Id;
        var oId = playerSymbol == PlayerSymbol.O ? profile.Id : opponent.Id;
        Guid? winnerId = state.Winner is null ? null : state.Winner == playerSymbol ? profile.Id : opponent.Id;
        var gameId = Guid.NewGuid();
        var game = new GameEntity
        {
            Id = gameId,
            RoomId = string.IsNullOrWhiteSpace(roomId) ? null : roomId,
            PlayerXId = xId,
            PlayerOId = oId,
            WinnerId = winnerId,
            Result = state.Status.ToString(),
            ConnectionMode = mode,
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow,
            FinalBoard = System.Text.Json.JsonSerializer.Serialize(state.Board)
        };
        for (var index = 0; index < moves.Count; index++)
        {
            var move = moves[index];
            game.Moves.Add(new MoveEntity
            {
                GameId = gameId,
                PlayerId = move.Symbol == PlayerSymbol.X ? xId : oId,
                CellIndex = move.CellIndex,
                MoveNumber = index + 1,
                CreatedAt = DateTime.UtcNow
            });
        }
        db.Games.Add(game);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync(int limit = 30)
    {
        await using var db = CreateContext();
        var records = await db.Games.AsNoTracking()
            .Where(game => game.PlayerXId == _profileId || game.PlayerOId == _profileId)
            .OrderByDescending(game => game.FinishedAt)
            .Take(limit)
            .Select(game => new HistoryRecord(
                game.PlayerX.Name, game.PlayerO.Name, game.Result, game.ConnectionMode,
                game.StartedAt, game.FinalBoard, game.RoomId))
            .ToListAsync();
        return records;
    }

    public async Task<PlayerStatistics> GetStatisticsAsync()
    {
        await using var db = CreateContext();
        var games = db.Games.AsNoTracking()
            .Where(game => (game.PlayerXId == _profileId || game.PlayerOId == _profileId) && game.FinishedAt != null);
        var total = await games.CountAsync();
        var wins = await games.CountAsync(game => game.WinnerId == _profileId);
        var draws = await games.CountAsync(game => game.Result == GameStatus.Draw.ToString());
        return new PlayerStatistics(total, wins, total - wins - draws, draws);
    }

    private AppDbContext CreateContext() => new(_databasePath);
}
