using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TicTacToe.Core;
using TicTacToe.Data;
using TicTacToe.Services;
using MatchStatus = TicTacToe.Core.GameStatus;
using Symbol = TicTacToe.Core.PlayerSymbol;

namespace TicTacToe.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly LocalGameRepository _repository;
    private readonly NetworkGameClient _network;
    private readonly Task _initializationTask;
    private TcpRoomServer? _localServer;
    private GameState _state = GameEngine.Create();
    private readonly List<MoveRecord> _moves = [];
    private DateTime _startedAt = DateTime.UtcNow;
    private bool _saved;

    public ObservableCollection<CellViewModel> Cells { get; } = new(Enumerable.Range(0, 9).Select(index => new CellViewModel(index)));
    public ObservableCollection<HistoryRecord> History { get; } = [];
    public ObservableCollection<ServerAdvertisement> DiscoveredServers { get; } = [];

    [ObservableProperty] private string _playerName = "Игрок";
    [ObservableProperty] private string _opponentName = "Игрок O";
    [ObservableProperty] private string _serverAddress = Preferences.Default.Get("server_address", "192.168.1.100");
    [ObservableProperty] private string _portText = Preferences.Default.Get("server_port", "5000");
    [ObservableProperty] private string _roomCode = "";
    private string? _matchId;
    [ObservableProperty] private string _statusMessage = "Начните новую партию или создайте сетевую комнату.";
    [ObservableProperty] private string _gameStatusText = "Ваш ход: X";
    [ObservableProperty] private string _playerSymbolText = "X";
    [ObservableProperty] private string _statisticsText = "Матчей: 0   Побед: 0   Поражений: 0   Ничьих: 0   Побед: 0%";
    [ObservableProperty] private string _connectionMode = "Локальная игра";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _hasRoom;
    [ObservableProperty] private bool _isWaitingForOpponent;
    [ObservableProperty] private string _waitingServerText = "";
    [ObservableProperty] private string _connectionTimeoutText = Preferences.Default.Get("connection_timeout", "10");
    [ObservableProperty] private string _currentScreen = "profile";
    [ObservableProperty] private bool _isMenuVisible;
    [ObservableProperty] private bool _isConnectVisible;
    [ObservableProperty] private bool _isLocalServerVisible;
    [ObservableProperty] private bool _isStatisticsVisible;
    [ObservableProperty] private bool _isProfileVisible = true;
    [ObservableProperty] private bool _isPlayerNameRequired = true;
    [ObservableProperty] private bool _isGameVisible;
    [ObservableProperty] private bool _isSearching;

    public MainViewModel(LocalGameRepository repository, NetworkGameClient network)
    {
        _repository = repository;
        _network = network;
        _network.MessageReceived += HandleNetworkMessage;
        _initializationTask = InitializeAsync();
    }

    public bool CanLeaveProfile => !IsPlayerNameRequired;

    partial void OnIsPlayerNameRequiredChanged(bool value) =>
        OnPropertyChanged(nameof(CanLeaveProfile));

    partial void OnCurrentScreenChanged(string value)
    {
        IsMenuVisible = value == "menu";
        IsConnectVisible = value == "connect";
        IsLocalServerVisible = value == "local";
        IsStatisticsVisible = value == "statistics";
        IsProfileVisible = value == "profile";
        IsGameVisible = value == "game";
    }

    [RelayCommand] private void OpenConnect() => CurrentScreen = "connect";
    [RelayCommand] private void OpenLocalServer() => CurrentScreen = "local";
    [RelayCommand] private void OpenStatistics() => CurrentScreen = "statistics";
    [RelayCommand] private void OpenProfile() => CurrentScreen = "profile";
    [RelayCommand]
    private void BackToMenu()
    {
        if (!IsPlayerNameRequired)
            CurrentScreen = "menu";
    }

    private async Task InitializeAsync()
    {
        try
        {
            await Task.Run(_repository.InitializeAsync);
            PlayerName = await Task.Run(_repository.GetPlayerNameAsync);
            UpdateBoard(_state);
            await ReloadHistoryAsync();
            IsPlayerNameRequired = !await Task.Run(_repository.HasPlayerNameAsync);
            CurrentScreen = IsPlayerNameRequired ? "profile" : "menu";
            if (IsPlayerNameRequired)
                StatusMessage = "Введите ник, чтобы начать игру.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Не удалось открыть локальную базу: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task SavePlayerNameAsync()
    {
        await _initializationTask;
        await RunBusyAsync(async () =>
        {
            await Task.Run(() => _repository.SavePlayerNameAsync(PlayerName));
            PlayerName = PlayerName.Trim();
            IsPlayerNameRequired = false;
            StatusMessage = "Имя игрока сохранено.";
            CurrentScreen = "menu";
        });
    }

    [RelayCommand]
    private async Task StartLocalGameAsync()
    {
        await DisconnectNetworkAsync();
        ConnectionMode = "Локальная игра";
        OpponentName = "Игрок O";
        IsWaitingForOpponent = false;
        WaitingServerText = "";
        PlayerSymbolText = "X / O";
        _state = GameEngine.Create();
        _moves.Clear();
        _saved = false;
        _startedAt = DateTime.UtcNow;
        HasRoom = false;
        IsOnline = false;
        StatusMessage = "Локальная партия началась. Первым ходит X.";
        UpdateBoard(_state);
        CurrentScreen = "game";
    }

    [RelayCommand]
    private async Task StartNewGameAsync()
    {
        await _initializationTask;
        if (IsOnline && HasRoom)
        {
            if (!_state.IsFinished)
            {
                StatusMessage = "Новая игра станет доступна после завершения текущей партии.";
                return;
            }

            try
            {
                await _network.SendAsync(
                    new NetworkMessage { Type = "NewGame", RoomId = RoomCode, MatchId = _matchId },
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or SocketException)
            {
                StatusMessage = $"Не удалось начать новую игру: {exception.Message}";
            }
            return;
        }

        await StartLocalGameAsync();
    }

    [RelayCommand]
    private async Task MakeMoveAsync(int cellIndex)
    {
        await _initializationTask;
        if (IsBusy || _state.IsFinished)
            return;
        try
        {
            if (IsOnline)
            {
                if (_state.Status != MatchStatus.InProgress)
                    return;
                await _network.SendAsync(new NetworkMessage { Type = "MakeMove", RoomId = RoomCode, CellIndex = cellIndex }, CancellationToken.None);
                return;
            }

            var symbol = _state.CurrentTurn;
            _state = GameEngine.MakeMove(_state, cellIndex);
            _moves.Add(new MoveRecord(cellIndex, symbol));
            UpdateBoard(_state);
            if (_state.IsFinished)
            {
                GameStatusText = StatusFor(_state);
                await SaveResultAsync(null, "Локальная");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
        {
            StatusMessage = exception.Message;
        }
    }

    [RelayCommand]
    private async Task CreateWifiRoomAsync()
    {
        await _initializationTask;
        await RunBusyAsync(async () =>
        {
            await DisconnectNetworkAsync();
            _localServer = new TcpRoomServer();
            try
            {
                await _localServer.StartAsync(IPAddress.Any, 5000);
            }
            catch (SocketException)
            {
                await _localServer.DisposeAsync();
                _localServer = new TcpRoomServer();
                await _localServer.StartAsync(IPAddress.Any, 0);
            }

            using var timeout = CreateTimeout();
            await _network.ConnectAsync("127.0.0.1", _localServer.BoundPort, timeout.Token);
            ConnectionMode = "LocalHost";
            ServerAddress = GetLocalAddress();
            PortText = _localServer.BoundPort.ToString();
            IsOnline = true;
            HasRoom = true;
            PlayerSymbolText = "X";
            IsWaitingForOpponent = true;
            RoomCode = "";
            ResetToWaitingRoom();
            GameStatusText = "Ожидаем второго игрока…";
            UpdateWaitingServerText();
            StatusMessage = $"Комната создается. Адрес: {ServerAddress}:{PortText}.";
            _localServer.MatchFinished += async finished =>
            {
                await _repository.SaveFinishedGameAsync(
                    finished.MatchId, finished.PlayerX, finished.PlayerO, Symbol.X,
                    finished.State, "LocalHost", finished.StartedAt,
                    finished.Moves.Select(move => new MoveRecord(move.CellIndex, move.Symbol)).ToArray());
            };
            await _network.SendAsync(new NetworkMessage { Type = "CreateRoom", Name = PlayerName }, timeout.Token);
            CurrentScreen = "game";
        });
    }

    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        await _initializationTask;
        await RunBusyAsync(async () =>
        {
            await DisconnectNetworkAsync();
            using var timeout = CreateTimeout();
            await _network.ConnectAsync(ServerAddress, ParsePort(), timeout.Token);
            ConnectionMode = "Server";
            IsOnline = true;
            HasRoom = true;
            ResetToWaitingRoom();
            OpponentName = "";
            RoomCode = "";
            IsWaitingForOpponent = true;
            UpdateWaitingServerText();
            PersistNetworkSettings();
            StatusMessage = "Подключаемся и ищем соперника…";
            await _network.SendAsync(new NetworkMessage { Type = "JoinAnyRoom", Name = PlayerName }, timeout.Token);
        });
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        await DisconnectNetworkAsync();
        await StartLocalGameAsync();
        StatusMessage = "Вы вышли из комнаты.";
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        await _initializationTask;
        await ReloadHistoryAsync();
    }

    private async Task ReloadHistoryAsync()
    {
        var (records, statistics) = await Task.Run(async () =>
        {
            var historyRecords = await _repository.GetHistoryAsync();
            var playerStatistics = await _repository.GetStatisticsAsync();
            return (historyRecords, playerStatistics);
        });
        History.Clear();
        foreach (var record in records)
            History.Add(record);
        StatisticsText = $"Матчей: {statistics.Games}   Побед: {statistics.Wins}   Поражений: {statistics.Losses}   Ничьих: {statistics.Draws}   Побед: {statistics.WinRate}%";
    }

    private void HandleNetworkMessage(NetworkMessage message)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            switch (message.Type)
            {
                case "RoomCreated":
                    if (!string.IsNullOrWhiteSpace(message.RoomId))
                    {
                        _startedAt = DateTime.UtcNow;
                        _moves.Clear();
                        _saved = false;
                        _matchId = message.MatchId;
                        RoomCode = message.RoomId;
                        PlayerSymbolText = message.PlayerSymbol ?? PlayerSymbolText;
                        var opponentName = PlayerSymbolText == "X" ? message.PlayerOName : message.PlayerXName;
                        if (!string.IsNullOrWhiteSpace(opponentName))
                            OpponentName = opponentName;
                        StatusMessage = PlayerSymbolText == "O"
                            ? "Соперник найден. Игра начинается."
                            : ConnectionMode == "LocalHost"
                            ? $"Сервер запущен. Ожидаем соперника по адресу {ServerAddress}:{PortText}."
                            : "Подключились к серверу. Ожидаем соперника.";
                        CurrentScreen = "game";
                    }
                    break;
                case "GameState":
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(message.MatchId) && message.MatchId != _matchId)
                        {
                            _matchId = message.MatchId;
                            _startedAt = DateTime.UtcNow;
                            _moves.Clear();
                            _saved = false;
                        }
                        _state = GameEngine.FromWire(message.Board, message.CurrentTurn, message.Status);
                        var opponentName = PlayerSymbolText == "X" ? message.PlayerOName : message.PlayerXName;
                        if (!string.IsNullOrWhiteSpace(opponentName))
                            OpponentName = opponentName;
                        UpdateBoard(_state);
                        IsWaitingForOpponent = _state.Status == MatchStatus.Waiting;
                        GameStatusText = StatusFor(_state);
                        if (_state.Status == MatchStatus.InProgress)
                            StatusMessage = $"Ваш символ: {PlayerSymbolText}. Ход: {_state.CurrentTurn}.";
                        if (_state.IsFinished)
                            await SaveResultAsync(_matchId, ConnectionMode);
                        if (_state.Status == MatchStatus.InProgress)
                            CurrentScreen = "game";
                    }
                    catch (InvalidDataException exception)
                    {
                        StatusMessage = exception.Message;
                    }
                    break;
                case "Error":
                    StatusMessage = message.Error ?? "Сетевая ошибка.";
                    break;
                case "OpponentDisconnected":
                    StatusMessage = message.Error ?? "Соединение с соперником потеряно.";
                    _state = GameEngine.Waiting();
                    UpdateBoard(_state);
                    IsWaitingForOpponent = message.Status == "Waiting";
                    if (IsWaitingForOpponent)
                    {
                        UpdateWaitingServerText();
                        GameStatusText = "Соперник отключился. Ожидаем нового игрока…";
                    }
                    else
                    {
                        IsOnline = false;
                        HasRoom = false;
                        GameStatusText = "Соперник отключился";
                    }
                    break;
                case "LeftRoom":
                    HasRoom = false;
                    IsOnline = false;
                    IsWaitingForOpponent = false;
                    break;
            }
        });
    }

    [RelayCommand]
    private async Task DiscoverServersAsync()
    {
        if (IsSearching)
            return;
        IsSearching = true;
        try
        {
            StatusMessage = "Поиск серверов в локальной сети…";
            var servers = await ServerDiscovery.FindServersAsync(TimeSpan.FromSeconds(3));
            DiscoveredServers.Clear();
            foreach (var server in servers)
                DiscoveredServers.Add(server);
            if (servers.Count == 0)
            {
                StatusMessage = "Серверы не найдены. Проверьте Wi-Fi или введите IP-адрес вручную.";
                return;
            }
            ServerAddress = servers[0].Address;
            PortText = servers[0].Port.ToString();
            StatusMessage = servers.Count == 1
                ? $"Найден сервер: {ServerAddress}:{PortText}. Можно подключаться."
                : $"Найдено серверов: {servers.Count}. Выбран {ServerAddress}:{PortText}.";
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            StatusMessage = $"Не удалось выполнить поиск: {exception.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void SelectServer(ServerAdvertisement server)
    {
        ServerAddress = server.Address;
        PortText = server.Port.ToString();
        StatusMessage = $"Выбран сервер {ServerAddress}:{PortText}. Можно подключаться.";
    }

    private async Task SaveResultAsync(string? roomId, string mode)
    {
        if (_saved)
            return;
        _saved = true;
        try
        {
            var ownSymbol = Enum.TryParse<Symbol>(PlayerSymbolText, out var parsed) ? parsed : Symbol.X;
            var otherName = OpponentName;
            var historyMoves = _moves.ToArray();
            if (IsOnline && historyMoves.Length == 0)
            {
                var symbol = Symbol.X;
                foreach (var cell in _state.Board.Select((value, index) => (value, index)).Where(cell => !string.IsNullOrEmpty(cell.value)))
                {
                    historyMoves = historyMoves.Append(new MoveRecord(cell.index, symbol)).ToArray();
                    symbol = symbol == Symbol.X ? Symbol.O : Symbol.X;
                }
            }
            var completedState = _state;
            var completedMatchId = IsOnline ? _matchId : roomId;
            var playerName = PlayerName;
            var startedAt = _startedAt;
            await Task.Run(() => _repository.SaveFinishedGameAsync(
                completedMatchId,
                playerName, otherName, ownSymbol, completedState, mode, startedAt, historyMoves));
            await ReloadHistoryAsync();
            StatusMessage = $"{StatusFor(completedState)} Результат сохранен в истории.";
        }
        catch (Exception exception)
        {
            _saved = false;
            StatusMessage = $"Матч завершен, но сохранить результат не удалось: {exception.Message}";
        }
    }

    private void UpdateBoard(GameState state)
    {
        for (var index = 0; index < Cells.Count; index++)
        {
            Cells[index].Value = state.Board[index];
            Cells[index].IsEnabled = !state.IsFinished &&
                state.Status != MatchStatus.Waiting &&
                (string.IsNullOrEmpty(state.Board[index])) &&
                (!IsOnline || (Enum.TryParse<Symbol>(PlayerSymbolText, out var own) && state.CurrentTurn == own));
        }

        if (!state.IsFinished && state.Status != MatchStatus.Waiting)
            GameStatusText = $"Ход: {state.CurrentTurn}";
    }

    private void ResetToWaitingRoom()
    {
        _state = GameEngine.Waiting();
        _moves.Clear();
        _saved = false;
        UpdateBoard(_state);
    }

    private string StatusFor(GameState state) => state.Status switch
    {
        MatchStatus.Waiting => GameEngine.GetStatusText(state.Status),
        MatchStatus.XWon or MatchStatus.OWon or MatchStatus.Draw or MatchStatus.Abandoned =>
            $"{GameEngine.GetStatusText(state.Status)}!",
        _ => $"Ход: {state.CurrentTurn}"
    };

    private void UpdateWaitingServerText() =>
        WaitingServerText = $"Сервер: {ServerAddress}:{PortText}";

    private CancellationTokenSource CreateTimeout()
    {
        var timeout = int.TryParse(ConnectionTimeoutText, out var seconds) ? Math.Clamp(seconds, 3, 60) : 10;
        ConnectionTimeoutText = timeout.ToString();
        return new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
    }

    private int ParsePort()
    {
        if (!int.TryParse(PortText, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Порт должен быть числом от 1 до 65535.");
        return port;
    }

    private void PersistNetworkSettings()
    {
        Preferences.Default.Set("server_address", ServerAddress.Trim());
        Preferences.Default.Set("server_port", PortText);
        Preferences.Default.Set("connection_timeout", ConnectionTimeoutText);
    }

    private static string GetLocalAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address.Address))
                .Select(address => address.Address.ToString())
                .FirstOrDefault() ?? "IP-адрес Wi-Fi";
        }
        catch (NetworkInformationException)
        {
            return "IP-адрес Wi-Fi";
        }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectNetworkAsync()
    {
        try
        {
            await _network.DisposeAsync();
        }
        catch (ObjectDisposedException)
        {
        }
        if (_localServer is not null)
        {
            await _localServer.DisposeAsync();
            _localServer = null;
        }
        IsOnline = false;
        HasRoom = false;
    }

    public async ValueTask DisposeAsync()
    {
        _network.MessageReceived -= HandleNetworkMessage;
        await DisconnectNetworkAsync();
    }
}
