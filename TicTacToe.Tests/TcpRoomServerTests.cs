using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TicTacToe.Core;
using Xunit;

namespace TicTacToe.Tests;

public sealed class TcpRoomServerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ServerAssignsTurnsValidatesMovesAndBroadcastsFinishedGame()
    {
        await using var server = new TcpRoomServer();
        await server.StartAsync(IPAddress.Loopback, 0);
        FinishedRoom? finishedRoom = null;
        server.MatchFinished += room =>
        {
            finishedRoom = room;
            return Task.CompletedTask;
        };

        using var host = new TcpClient();
        using var guest = new TcpClient();
        using var extra = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var discoveryClient = new UdpClient(0))
        {
            var request = Encoding.UTF8.GetBytes("TICTACTOE_DISCOVER_V1");
            await discoveryClient.SendAsync(request, new IPEndPoint(IPAddress.Loopback, ServerDiscovery.DiscoveryPort), timeout.Token);
            var response = await discoveryClient.ReceiveAsync(timeout.Token);
            var advertisement = JsonSerializer.Deserialize<NetworkMessage>(response.Buffer, JsonOptions);
            Assert.Equal("ServerFound", advertisement?.Type);
            Assert.Equal(server.BoundPort, advertisement?.Port);
        }
        await host.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token);
        await guest.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token);
        using var hostReader = new StreamReader(host.GetStream());
        await using var hostWriter = new StreamWriter(host.GetStream()) { AutoFlush = true };
        using var guestReader = new StreamReader(guest.GetStream());
        await using var guestWriter = new StreamWriter(guest.GetStream()) { AutoFlush = true };

        await SendAsync(hostWriter, new NetworkMessage { Type = "CreateRoom", Name = "Host" }, timeout.Token);
        var created = await ReadAsync(hostReader, timeout.Token);
        var roomId = created.RoomId!;
        Assert.Equal("X", created.PlayerSymbol);
        Assert.Equal("Waiting", (await ReadAsync(hostReader, timeout.Token)).Status);

        await SendAsync(guestWriter, new NetworkMessage { Type = "JoinAnyRoom", Name = "Guest" }, timeout.Token);
        var joined = await ReadAsync(guestReader, timeout.Token);
        Assert.Equal("O", joined.PlayerSymbol);
        Assert.Equal("Host", joined.PlayerXName);
        Assert.Equal("Guest", joined.PlayerOName);
        var guestGameState = await ReadAsync(guestReader, timeout.Token);
        var hostGameState = await ReadAsync(hostReader, timeout.Token);
        Assert.Equal("InProgress", guestGameState.Status);
        Assert.Equal("InProgress", hostGameState.Status);
        Assert.Equal("Host", guestGameState.PlayerXName);
        Assert.Equal("Guest", guestGameState.PlayerOName);
        Assert.Equal("Host", hostGameState.PlayerXName);
        Assert.Equal("Guest", hostGameState.PlayerOName);

        await extra.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token);
        using var extraReader = new StreamReader(extra.GetStream());
        await using var extraWriter = new StreamWriter(extra.GetStream()) { AutoFlush = true };
        await SendAsync(extraWriter, new NetworkMessage { Type = "JoinRoom", RoomId = roomId, Name = "Third" }, timeout.Token);
        Assert.Contains("заполнена", (await ReadAsync(extraReader, timeout.Token)).Error, StringComparison.OrdinalIgnoreCase);

        await SendAsync(guestWriter, new NetworkMessage { Type = "MakeMove", RoomId = roomId, CellIndex = 0 }, timeout.Token);
        Assert.NotNull((await ReadAsync(guestReader, timeout.Token)).Error);

        NetworkMessage? finalState = null;
        foreach (var (writer, index) in new (StreamWriter Writer, int Index)[]
        {
            (hostWriter, 0), (guestWriter, 3),
            (hostWriter, 1), (guestWriter, 4),
            (hostWriter, 2)
        })
        {
            await SendAsync(writer, new NetworkMessage { Type = "MakeMove", RoomId = roomId, CellIndex = index }, timeout.Token);
            var hostState = await ReadAsync(hostReader, timeout.Token);
            var guestState = await ReadAsync(guestReader, timeout.Token);
            Assert.Equal(hostState.Status, guestState.Status);
            finalState = hostState;
        }

        Assert.Equal("XWon", finalState?.Status);
        Assert.Equal(5, finishedRoom?.Moves.Count);
        Assert.Equal(new[] { "X", "O", "X", "O", "X" }, finishedRoom?.Moves.Select(move => move.Symbol.ToString()));
        Assert.NotNull(finishedRoom?.MatchId);

        await SendAsync(hostWriter, new NetworkMessage { Type = "MakeMove", RoomId = roomId, CellIndex = 8 }, timeout.Token);
        Assert.NotNull((await ReadAsync(hostReader, timeout.Token)).Error);

        await SendAsync(hostWriter, new NetworkMessage
        {
            Type = "NewGame",
            RoomId = roomId,
            MatchId = finishedRoom!.MatchId
        }, timeout.Token);
        var hostRematch = await ReadAsync(hostReader, timeout.Token);
        var guestRematch = await ReadAsync(guestReader, timeout.Token);
        Assert.Equal("InProgress", hostRematch.Status);
        Assert.Equal("InProgress", guestRematch.Status);
        Assert.All(hostRematch.Board!, cell => Assert.Equal("", cell));
        Assert.NotEqual(finishedRoom.MatchId, hostRematch.MatchId);
        Assert.Equal(hostRematch.MatchId, guestRematch.MatchId);
    }

    [Fact]
    public async Task AutoMatchPairsPlayersIntoIndependentConcurrentGames()
    {
        await using var server = new TcpRoomServer { AutoMatchPlayers = true };
        await server.StartAsync(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var first = new TcpClient();
        using var second = new TcpClient();
        using var third = new TcpClient();
        using var fourth = new TcpClient();
        await Task.WhenAll(
            first.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token).AsTask(),
            second.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token).AsTask(),
            third.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token).AsTask(),
            fourth.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token).AsTask());

        using var firstReader = new StreamReader(first.GetStream());
        await using var firstWriter = new StreamWriter(first.GetStream()) { AutoFlush = true };
        using var secondReader = new StreamReader(second.GetStream());
        await using var secondWriter = new StreamWriter(second.GetStream()) { AutoFlush = true };
        using var thirdReader = new StreamReader(third.GetStream());
        await using var thirdWriter = new StreamWriter(third.GetStream()) { AutoFlush = true };
        using var fourthReader = new StreamReader(fourth.GetStream());
        await using var fourthWriter = new StreamWriter(fourth.GetStream()) { AutoFlush = true };

        await SendAsync(firstWriter, new NetworkMessage { Type = "JoinAnyRoom", Name = "First" }, timeout.Token);
        var firstJoined = await ReadAsync(firstReader, timeout.Token);
        Assert.Equal("X", firstJoined.PlayerSymbol);
        Assert.Equal("Waiting", (await ReadAsync(firstReader, timeout.Token)).Status);

        await SendAsync(secondWriter, new NetworkMessage { Type = "JoinAnyRoom", Name = "Second" }, timeout.Token);
        var secondJoined = await ReadAsync(secondReader, timeout.Token);
        Assert.Equal(firstJoined.RoomId, secondJoined.RoomId);
        Assert.Equal("O", secondJoined.PlayerSymbol);
        _ = await ReadAsync(firstReader, timeout.Token);
        Assert.Equal("InProgress", (await ReadAsync(secondReader, timeout.Token)).Status);

        await SendAsync(thirdWriter, new NetworkMessage { Type = "JoinAnyRoom", Name = "Third" }, timeout.Token);
        var thirdJoined = await ReadAsync(thirdReader, timeout.Token);
        Assert.Equal("X", thirdJoined.PlayerSymbol);
        Assert.Equal("Waiting", (await ReadAsync(thirdReader, timeout.Token)).Status);

        await SendAsync(fourthWriter, new NetworkMessage { Type = "JoinAnyRoom", Name = "Fourth" }, timeout.Token);
        var fourthJoined = await ReadAsync(fourthReader, timeout.Token);
        Assert.Equal(thirdJoined.RoomId, fourthJoined.RoomId);
        Assert.NotEqual(firstJoined.RoomId, thirdJoined.RoomId);
        Assert.Equal("O", fourthJoined.PlayerSymbol);
        Assert.Equal("InProgress", (await ReadAsync(thirdReader, timeout.Token)).Status);
        Assert.Equal("InProgress", (await ReadAsync(fourthReader, timeout.Token)).Status);

        await SendAsync(firstWriter, new NetworkMessage { Type = "MakeMove", CellIndex = 0 }, timeout.Token);
        var firstGameState = await ReadAsync(firstReader, timeout.Token);
        var secondGameState = await ReadAsync(secondReader, timeout.Token);
        Assert.Equal("X", firstGameState.Board![0]);
        Assert.Equal("", firstGameState.Board[4]);
        Assert.Equal("X", secondGameState.Board![0]);
        Assert.Equal("", secondGameState.Board[4]);

        await SendAsync(thirdWriter, new NetworkMessage { Type = "MakeMove", CellIndex = 4 }, timeout.Token);
        var thirdGameState = await ReadAsync(thirdReader, timeout.Token);
        var fourthGameState = await ReadAsync(fourthReader, timeout.Token);
        Assert.Equal("", thirdGameState.Board![0]);
        Assert.Equal("X", thirdGameState.Board[4]);
        Assert.Equal("", fourthGameState.Board![0]);
        Assert.Equal("X", fourthGameState.Board[4]);

        fourth.Close();
        var disconnected = await ReadAsync(thirdReader, timeout.Token);
        Assert.Equal("OpponentDisconnected", disconnected.Type);
        Assert.Equal("Waiting", disconnected.Status);
        Assert.Equal("Waiting", (await ReadAsync(thirdReader, timeout.Token)).Status);

        using var replacement = new TcpClient();
        await replacement.ConnectAsync(IPAddress.Loopback, server.BoundPort, timeout.Token);
        using var replacementReader = new StreamReader(replacement.GetStream());
        await using var replacementWriter = new StreamWriter(replacement.GetStream()) { AutoFlush = true };
        await SendAsync(replacementWriter, new NetworkMessage { Type = "JoinAnyRoom", Name = "Replacement" }, timeout.Token);
        var replacementJoined = await ReadAsync(replacementReader, timeout.Token);
        Assert.Equal(thirdJoined.RoomId, replacementJoined.RoomId);
        Assert.Equal("O", replacementJoined.PlayerSymbol);
        Assert.Equal("InProgress", (await ReadAsync(replacementReader, timeout.Token)).Status);
        Assert.Equal("InProgress", (await ReadAsync(thirdReader, timeout.Token)).Status);
    }

    private static Task SendAsync(StreamWriter writer, NetworkMessage message, CancellationToken cancellationToken) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions).AsMemory(), cancellationToken);

    private static async Task<NetworkMessage> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        Assert.NotNull(line);
        return JsonSerializer.Deserialize<NetworkMessage>(line!, JsonOptions)!;
    }
}
