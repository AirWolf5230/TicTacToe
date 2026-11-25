using TicTacToe.Core;
using Xunit;

namespace TicTacToe.Tests;

public sealed class GameEngineTests
{
    [Fact]
    public void FirstMoveIsXAndTurnChanges()
    {
        var state = GameEngine.MakeMove(GameEngine.Create(), 0);

        Assert.Equal("X", state.Board[0]);
        Assert.Equal(PlayerSymbol.O, state.CurrentTurn);
        Assert.Equal(GameStatus.InProgress, state.Status);
    }

    [Theory]
    [InlineData(GameStatus.XWon, "Победили крестики")]
    [InlineData(GameStatus.OWon, "Победили нолики")]
    public void GameStatusHasRussianDisplayText(GameStatus status, string expected) =>
        Assert.Equal(expected, GameEngine.GetStatusText(status));

    [Fact]
    public void EmptyCellsAreNormalizedWhenReadingOlderServerMessages()
    {
        var state = GameEngine.FromWire(
            [null!, "X", null!, "O", null!, null!, null!, null!, null!],
            "X",
            "InProgress");

        Assert.Equal("", state.Board[0]);
        Assert.Equal("X", state.Board[1]);
        Assert.Equal("", state.Board[2]);
    }

    [Theory]
    [InlineData(0, 1, 4, 2, 8)]
    [InlineData(0, 3, 1, 4, 2)]
    [InlineData(0, 1, 3, 2, 6)]
    public void WinningLineEndsTheGame(params int[] cells)
    {
        var state = GameEngine.Create();
        foreach (var cell in cells)
            state = GameEngine.MakeMove(state, cell);

        Assert.Equal(GameStatus.XWon, state.Status);
        Assert.True(state.IsFinished);
    }

    [Fact]
    public void FullBoardWithoutWinnerIsDraw()
    {
        var state = GameEngine.Create();
        foreach (var cell in new[] { 0, 1, 2, 4, 3, 5, 7, 6, 8 })
            state = GameEngine.MakeMove(state, cell);

        Assert.Equal(GameStatus.Draw, state.Status);
        Assert.Null(state.Winner);
    }

    [Fact]
    public void OccupiedCellIsRejectedWithoutChangingOriginalState()
    {
        var state = GameEngine.MakeMove(GameEngine.Create(), 4);

        Assert.Throws<InvalidOperationException>(() => GameEngine.MakeMove(state, 4));
        Assert.Equal("X", state.Board[4]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void OutOfRangeCellIsRejected(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GameEngine.MakeMove(GameEngine.Create(), index));

    [Fact]
    public void MoveAfterWinIsRejected()
    {
        var state = GameEngine.Create();
        foreach (var cell in new[] { 0, 3, 1, 4, 2 })
            state = GameEngine.MakeMove(state, cell);

        Assert.Throws<InvalidOperationException>(() => GameEngine.MakeMove(state, 8));
    }

    [Fact]
    public void WrongNetworkPlayerIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GameEngine.MakeMove(GameEngine.Create(), 0, PlayerSymbol.O));
    }

    [Fact]
    public void WaitingRoomDoesNotAcceptMoves()
    {
        Assert.Throws<InvalidOperationException>(() => GameEngine.MakeMove(GameEngine.Waiting(), 0));
    }
}
