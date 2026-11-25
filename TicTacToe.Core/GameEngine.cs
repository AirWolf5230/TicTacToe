namespace TicTacToe.Core;

public enum PlayerSymbol
{
    X,
    O
}

public enum GameStatus
{
    Waiting,
    InProgress,
    XWon,
    OWon,
    Draw,
    Abandoned
}

public sealed record GameState(
    string[] Board,
    PlayerSymbol CurrentTurn,
    GameStatus Status)
{
    public PlayerSymbol? Winner => Status switch
    {
        GameStatus.XWon => PlayerSymbol.X,
        GameStatus.OWon => PlayerSymbol.O,
        _ => null
    };

    public bool IsFinished => Status is GameStatus.XWon or GameStatus.OWon or GameStatus.Draw or GameStatus.Abandoned;
}

public static class GameEngine
{
    private static readonly int[][] WinningLines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [0, 4, 8], [2, 4, 6]
    ];

    public static GameState Create() => new(EmptyBoard(), PlayerSymbol.X, GameStatus.InProgress);

    public static GameState Waiting() => new(EmptyBoard(), PlayerSymbol.X, GameStatus.Waiting);

    private static string[] EmptyBoard() => Enumerable.Repeat(string.Empty, 9).ToArray();

    public static string GetStatusText(GameStatus status) => status switch
    {
        GameStatus.Waiting => "Ожидаем соперника…",
        GameStatus.InProgress => "Игра продолжается",
        GameStatus.XWon => "Победили крестики",
        GameStatus.OWon => "Победили нолики",
        GameStatus.Draw => "Ничья",
        GameStatus.Abandoned => "Матч прерван",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static GameState MakeMove(GameState state, int cellIndex, PlayerSymbol? player = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status != GameStatus.InProgress)
            throw new InvalidOperationException("Матч уже завершен или еще не начался.");
        if (cellIndex is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(cellIndex), "Номер клетки должен быть от 0 до 8.");
        if (!string.IsNullOrEmpty(state.Board[cellIndex]))
            throw new InvalidOperationException("Эта клетка уже занята.");
        if (player is not null && player != state.CurrentTurn)
            throw new InvalidOperationException("Сейчас ход другого игрока.");

        var board = (string[])state.Board.Clone();
        board[cellIndex] = state.CurrentTurn.ToString();
        var winner = WinningLines.FirstOrDefault(line =>
            board[line[0]] == board[line[1]] &&
            board[line[1]] == board[line[2]] &&
            !string.IsNullOrEmpty(board[line[0]]));
        if (winner is not null)
            return new GameState(board, state.CurrentTurn, state.CurrentTurn == PlayerSymbol.X ? GameStatus.XWon : GameStatus.OWon);
        if (board.All(cell => !string.IsNullOrEmpty(cell)))
            return new GameState(board, state.CurrentTurn, GameStatus.Draw);

        return new GameState(board, state.CurrentTurn == PlayerSymbol.X ? PlayerSymbol.O : PlayerSymbol.X, GameStatus.InProgress);
    }

    public static GameState FromWire(string[]? board, string? currentTurn, string? status)
    {
        if (board is not { Length: 9 } || board.Any(cell => cell is not (null or "" or "X" or "O")))
            throw new InvalidDataException("Сервер прислал некорректное игровое поле.");
        if (!Enum.TryParse<PlayerSymbol>(currentTurn, out var turn) ||
            !Enum.TryParse<GameStatus>(status, out var gameStatus))
            throw new InvalidDataException("Сервер прислал некорректное состояние игры.");
        return new GameState(board.Select(cell => cell ?? string.Empty).ToArray(), turn, gameStatus);
    }
}
