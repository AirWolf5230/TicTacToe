using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TicTacToe.Core;

public sealed class TcpRoomServer : IAsyncDisposable
{
    public const int DefaultPort = 5000;
    private const int MaxMessageLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _matchmakingSync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private UdpClient? _discoveryListener;
    private Task? _discoveryTask;
    public event Func<FinishedRoom, Task>? MatchFinished;
    public bool AutoMatchPlayers { get; set; }

    public async Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
            return;
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Порт должен быть от 0 до 65535.");
        _listener = new TcpListener(address, port);
        _listener.Start();
        _acceptTask = AcceptClientsAsync(_shutdown.Token);
        try
        {
            _discoveryListener = new UdpClient(ServerDiscovery.DiscoveryPort);
            _discoveryTask = DiscoverAsync(_shutdown.Token);
        }
        catch (SocketException)
        {
            _discoveryListener?.Dispose();
            _discoveryListener = null;
        }
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? DefaultPort;

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var session = new ClientSession(client);
        Room? joinedRoom = null;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                var line = await ReadLimitedLineAsync(reader, cancellationToken);
                if (line is null)
                    break;
                if (line.Length > MaxMessageLength)
                {
                    await session.SendAsync(new NetworkMessage { Type = "Error", Error = "Сообщение превышает допустимый размер." }, cancellationToken);
                    break;
                }

                NetworkMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<NetworkMessage>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    await session.SendAsync(new NetworkMessage { Type = "Error", Error = "Некорректный формат сообщения." }, cancellationToken);
                    continue;
                }
                if (message is null)
                    continue;

                var response = await ProcessMessageAsync(message, session, joinedRoom, cancellationToken);
                if (message.Type == "LeaveRoom")
                    joinedRoom = null;
                if (response.Room is not null)
                    joinedRoom = response.Room;
                if (response.Message is { Type: not "Error" })
                    await session.SendAsync(response.Message, cancellationToken);
                if (response.Room is not null && response.Broadcast)
                    await BroadcastStateAsync(response.Room, cancellationToken);
                if (response.Message is { Type: "Error" })
                    await session.SendAsync(response.Message, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidDataException)
        {
            try
            {
                await session.SendAsync(new NetworkMessage { Type = "Error", Error = "Сообщение превышает допустимый размер." }, cancellationToken);
            }
            catch (IOException)
            {
            }
        }
        catch (SocketException)
        {
        }
        finally
        {
            client.Dispose();
            if (joinedRoom is not null)
            {
                await NotifyDisconnectAsync(joinedRoom, session, cancellationToken);
            }
        }
    }

    private async Task<(NetworkMessage? Message, Room? Room, bool Broadcast)> ProcessMessageAsync(
        NetworkMessage message, ClientSession session, Room? currentRoom, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case "CreateRoom":
            {
                var roomId = CreateRoomId();
                var room = new Room(roomId, SanitizeName(message.Name), session, false);
                if (!_rooms.TryAdd(roomId, room))
                    return (Error("Не удалось создать комнату, попробуйте еще раз."), null, false);
                session.Room = room;
                session.Symbol = PlayerSymbol.X;
                return (new NetworkMessage { Type = "RoomCreated", RoomId = roomId, PlayerSymbol = "X", Port = BoundPort }, room, true);
            }
            case "JoinRoom":
            {
                if (currentRoom is not null)
                    return (Error("Вы уже находитесь в комнате."), null, false);
                if (string.IsNullOrWhiteSpace(message.RoomId) || !_rooms.TryGetValue(message.RoomId.Trim(), out var room))
                    return (Error("Комната не найдена."), null, false);
                lock (room.Sync)
                {
                    if (room.Guest is not null)
                        return (Error("Комната уже заполнена."), null, false);
                    room.Guest = session;
                    room.GuestName = SanitizeName(message.Name);
                    room.State = GameEngine.Create();
                    room.StartedAt = DateTime.UtcNow;
                    session.Room = room;
                    session.Symbol = PlayerSymbol.O;
                }
                return (new NetworkMessage
                {
                    Type = "RoomCreated",
                    RoomId = room.Id,
                    MatchId = room.MatchId,
                    PlayerSymbol = "O",
                    PlayerXName = room.HostName,
                    PlayerOName = room.GuestName
                }, room, true);
            }
            case "JoinAnyRoom":
            {
                if (currentRoom is not null)
                    return (Error("Вы уже подключены к игре."), null, false);
                return JoinAnyRoom(message, session);
            }
            case "MakeMove":
            {
                if (currentRoom is null || message.CellIndex is null || session.Symbol is null)
                    return (Error("Сначала подключитесь к комнате."), null, false);
                FinishedRoom? finished = null;
                try
                {
                    lock (currentRoom.Sync)
                    {
                        if (currentRoom.Guest is null)
                            return (Error("Ожидается подключение второго игрока."), null, false);
                        currentRoom.State = GameEngine.MakeMove(currentRoom.State, message.CellIndex.Value, session.Symbol);
                        currentRoom.Moves.Add(new CompletedMove(message.CellIndex.Value, session.Symbol.Value));
                        if (currentRoom.State.IsFinished)
                        {
                            currentRoom.FinishedAt = DateTime.UtcNow;
                            currentRoom.StartedAt ??= currentRoom.FinishedAt;
                            finished = new FinishedRoom(
                                currentRoom.Id, currentRoom.MatchId, currentRoom.HostName, currentRoom.GuestName!,
                                currentRoom.State, currentRoom.StartedAt.Value, currentRoom.FinishedAt.Value,
                                currentRoom.Moves.ToArray());
                        }
                    }
                    if (finished is not null && MatchFinished is not null)
                    {
                        try
                        {
                            await MatchFinished.Invoke(finished);
                        }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            return (Error($"Матч завершен, но сохранить результат не удалось: {exception.Message}."), currentRoom, true);
                        }
                    }
                    return (null, currentRoom, true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
                {
                    return (Error(exception.Message), null, false);
                }
            }
            case "NewGame":
            {
                if (currentRoom is null || session.Symbol is null)
                    return (Error("Сначала подключитесь к комнате."), null, false);
                lock (currentRoom.Sync)
                {
                    if (currentRoom.Guest is null)
                        return (Error("Ожидается подключение второго игрока."), null, false);
                    if (!currentRoom.State.IsFinished)
                    {
                        if (!string.IsNullOrWhiteSpace(message.MatchId) &&
                            message.MatchId != currentRoom.MatchId)
                            return (null, currentRoom, true);
                        return (Error("Новая игра доступна после завершения текущей партии."), null, false);
                    }
                    if (!string.IsNullOrWhiteSpace(message.MatchId) &&
                        message.MatchId != currentRoom.MatchId)
                        return (Error("Текущая партия уже изменилась. Обновите игру."), null, false);
                    currentRoom.State = GameEngine.Create();
                    currentRoom.Moves.Clear();
                    currentRoom.MatchId = Guid.NewGuid().ToString("N");
                    currentRoom.StartedAt = DateTime.UtcNow;
                    currentRoom.FinishedAt = null;
                }
                return (null, currentRoom, true);
            }
            case "Ping":
                return (new NetworkMessage { Type = "Pong" }, null, false);
            case "LeaveRoom":
                if (currentRoom is not null)
                {
                    await NotifyDisconnectAsync(currentRoom, session, cancellationToken);
                    session.Room = null;
                    return (new NetworkMessage { Type = "LeftRoom" }, null, false);
                }
                return (new NetworkMessage { Type = "LeftRoom" }, null, false);
            default:
                return (Error("Неизвестный тип сообщения."), null, false);
        }
    }

    private async Task BroadcastStateAsync(Room room, CancellationToken cancellationToken)
    {
        ClientSession? host;
        ClientSession? guest;
        GameState state;
        string? guestName;
        string matchId;
        lock (room.Sync)
        {
            host = room.Host;
            guest = room.Guest;
            state = room.State;
            guestName = room.GuestName;
            matchId = room.MatchId;
        }
        var update = new NetworkMessage
        {
            Type = "GameState",
            RoomId = room.Id,
            MatchId = matchId,
            Board = state.Board,
            CurrentTurn = state.CurrentTurn.ToString(),
            Status = state.Status.ToString(),
            PlayerXName = room.HostName,
            PlayerOName = guestName
        };
        if (host is not null)
            await host.SendAsync(update, cancellationToken);
        if (guest is not null)
            await guest.SendAsync(update, cancellationToken);
    }

    private async Task NotifyDisconnectAsync(Room room, ClientSession session, CancellationToken cancellationToken)
    {
        ClientSession? other;
        var roomClosed = false;
        lock (room.Sync)
        {
            if (room.Host == session)
            {
                other = room.Guest;
                room.Host = null;
                roomClosed = true;
            }
            else if (room.Guest == session)
            {
                other = room.Host;
                room.Guest = null;
                room.GuestName = null;
                room.State = GameEngine.Waiting();
                room.Moves.Clear();
                room.StartedAt = null;
                room.FinishedAt = null;
            }
            else
                return;
        }
        if (roomClosed)
            _rooms.TryRemove(room.Id, out _);
        if (other is not null)
        {
            try
            {
                await other.SendAsync(new NetworkMessage
                {
                    Type = "OpponentDisconnected",
                    Status = roomClosed ? "Closed" : "Waiting",
                    Error = "Соперник отключился."
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
            }
        }
        if (!roomClosed)
            await BroadcastStateAsync(room, cancellationToken);
    }

    private static NetworkMessage Error(string error) => new() { Type = "Error", Error = error };

    private (NetworkMessage? Message, Room? Room, bool Broadcast) JoinAnyRoom(
        NetworkMessage message, ClientSession session)
    {
        lock (_matchmakingSync)
        {
            var room = _rooms.Values
                .Where(candidate => candidate.Guest is null && candidate.Host is not null &&
                    (AutoMatchPlayers || !candidate.AutoMatch))
                .OrderBy(candidate => candidate.CreatedAt)
                .FirstOrDefault();

            if (room is not null)
            {
                lock (room.Sync)
                {
                    if (room.Guest is not null || room.Host is null)
                        return (Error("Соперник уже подключается. Повторите попытку."), null, false);
                    room.Guest = session;
                    room.GuestName = SanitizeName(message.Name);
                    room.State = GameEngine.Create();
                    room.StartedAt = DateTime.UtcNow;
                    session.Room = room;
                    session.Symbol = PlayerSymbol.O;
                }
                return (new NetworkMessage
                {
                    Type = "RoomCreated",
                    RoomId = room.Id,
                    MatchId = room.MatchId,
                    PlayerSymbol = "O",
                    PlayerXName = room.HostName,
                    PlayerOName = room.GuestName
                }, room, true);
            }

            if (!AutoMatchPlayers)
                return (Error("На сервере пока нет игры, ожидающей соперника."), null, false);

            var roomId = CreateRoomId();
            var newRoom = new Room(roomId, SanitizeName(message.Name), session, true);
            if (!_rooms.TryAdd(roomId, newRoom))
                return (Error("Не удалось подключиться к очереди. Повторите попытку."), null, false);
            session.Room = newRoom;
            session.Symbol = PlayerSymbol.X;
            return (new NetworkMessage
            {
                Type = "RoomCreated",
                RoomId = roomId,
                MatchId = newRoom.MatchId,
                PlayerSymbol = "X"
            }, newRoom, true);
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _discoveryListener is not null)
        {
            UdpReceiveResult request;
            try
            {
                request = await _discoveryListener.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (!ServerDiscovery.IsDiscoveryRequest(request.Buffer))
                continue;
            var response = JsonSerializer.SerializeToUtf8Bytes(
                new NetworkMessage { Type = "ServerFound", Name = "TicTacToe", Port = BoundPort }, JsonOptions);
            try
            {
                await _discoveryListener.SendAsync(response, request.RemoteEndPoint, cancellationToken);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
            }
        }
    }

    private static async Task<string?> ReadLimitedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return line.Length == 0 ? null : throw new InvalidDataException("Сообщение не завершено.");
            if (buffer[0] == '\n')
                return line.ToString().TrimEnd('\r');
            if (line.Length >= MaxMessageLength)
                throw new InvalidDataException("Сообщение превышает допустимый размер.");
            line.Append(buffer[0]);
        }
    }

    private static string SanitizeName(string? name)
    {
        var normalized = name?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "Игрок" : normalized[..Math.Min(normalized.Length, 24)];
    }

    private string CreateRoomId()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string id;
        do
        {
            id = new string(Enumerable.Range(0, 6).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());
        } while (_rooms.ContainsKey(id));
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener?.Stop();
        _discoveryListener?.Dispose();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_discoveryTask is not null)
        {
            try
            {
                await _discoveryTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        foreach (var room in _rooms.Values)
        {
            room.Host?.Client.Dispose();
            room.Guest?.Client.Dispose();
        }
        _rooms.Clear();
        _shutdown.Dispose();
    }

    private sealed class Room(string id, string hostName, ClientSession host, bool autoMatch)
    {
        public string Id { get; } = id;
        public string MatchId { get; set; } = Guid.NewGuid().ToString("N");
        public string HostName { get; } = hostName;
        public bool AutoMatch { get; } = autoMatch;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public string? GuestName { get; set; }
        public ClientSession? Host { get; set; } = host;
        public ClientSession? Guest { get; set; }
        public GameState State { get; set; } = GameEngine.Waiting();
        public List<CompletedMove> Moves { get; } = [];
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public object Sync { get; } = new();
    }

    private sealed class ClientSession(TcpClient client)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public TcpClient Client { get; } = client;
        public Room? Room { get; set; }
        public PlayerSymbol? Symbol { get; set; }

        public async Task SendAsync(NetworkMessage message, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var json = JsonSerializer.Serialize(message, JsonOptions);
                if (json.Length > MaxMessageLength)
                    throw new IOException("Слишком большое сообщение.");
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                await Client.GetStream().WriteAsync(bytes, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
