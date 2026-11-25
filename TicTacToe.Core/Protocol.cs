namespace TicTacToe.Core;

public sealed class NetworkMessage
{
    public string Type { get; set; } = "";
    public string? RequestId { get; set; }
    public string? RoomId { get; set; }
    public string? MatchId { get; set; }
    public string? Name { get; set; }
    public string? PlayerSymbol { get; set; }
    public string? PlayerXName { get; set; }
    public string? PlayerOName { get; set; }
    public string[]? Board { get; set; }
    public string? CurrentTurn { get; set; }
    public string? Status { get; set; }
    public int? CellIndex { get; set; }
    public string? Error { get; set; }
    public string? HostAddress { get; set; }
    public int? Port { get; set; }
}

public sealed record RoomConnection(string RoomId, PlayerSymbol Symbol);
public sealed record CompletedMove(int CellIndex, PlayerSymbol Symbol);
public sealed record FinishedRoom(
    string RoomId,
    string MatchId,
    string PlayerX,
    string PlayerO,
    GameState State,
    DateTime StartedAt,
    DateTime FinishedAt,
    IReadOnlyList<CompletedMove> Moves);
public sealed record ServerAdvertisement(string Address, int Port, string Name)
{
    public string DisplayAddress => $"{Name} — {Address}:{Port}";
}
