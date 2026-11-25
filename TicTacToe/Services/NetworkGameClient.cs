using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TicTacToe.Core;

namespace TicTacToe.Services;

public sealed class NetworkGameClient : IAsyncDisposable
{
    private const int MaxMessageLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;

    public event Action<NetworkMessage>? MessageReceived;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Введите IP-адрес или имя сервера.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Порт должен быть от 1 до 65535.");
        await DisposeConnectionAsync();
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host.Trim(), port, cancellationToken);
            _client = client;
            var stream = client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            _readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readTask = ReadMessagesAsync(_readCancellation.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task SendAsync(NetworkMessage message, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("Сначала подключитесь к серверу.");
        var json = JsonSerializer.Serialize(message, JsonOptions);
        if (json.Length > MaxMessageLength)
            throw new InvalidDataException("Сообщение превышает допустимый размер.");
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLimitedLineAsync(_reader!, cancellationToken);
                if (line is null)
                {
                    MessageReceived?.Invoke(new NetworkMessage { Type = "OpponentDisconnected", Error = "Соединение с сервером закрыто." });
                    return;
                }
                if (line.Length > MaxMessageLength)
                {
                    MessageReceived?.Invoke(new NetworkMessage { Type = "Error", Error = "Получено слишком большое сообщение." });
                    return;
                }
                try
                {
                    var message = JsonSerializer.Deserialize<NetworkMessage>(line, JsonOptions);
                    if (message is not null)
                        MessageReceived?.Invoke(message);
                }
                catch (JsonException)
                {
                    MessageReceived?.Invoke(new NetworkMessage { Type = "Error", Error = "Сервер прислал некорректный ответ." });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            MessageReceived?.Invoke(new NetworkMessage { Type = "OpponentDisconnected", Error = $"Соединение потеряно: {exception.Message}" });
        }
        catch (SocketException exception)
        {
            MessageReceived?.Invoke(new NetworkMessage { Type = "OpponentDisconnected", Error = $"Сетевая ошибка: {exception.Message}" });
        }
    }

    private async Task DisposeConnectionAsync()
    {
        if (_readCancellation is not null)
        {
            await _readCancellation.CancelAsync();
            if (_readTask is not null)
            {
                try { await _readTask; }
                catch (OperationCanceledException) { }
            }
            _readCancellation.Dispose();
        }
        _client?.Dispose();
        _client = null;
        _reader = null;
        _writer = null;
        _readCancellation = null;
        _readTask = null;
    }

    private static async Task<string?> ReadLimitedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return line.Length == 0 ? null : throw new InvalidDataException("Получено незавершенное сообщение.");
            if (buffer[0] == '\n')
                return line.ToString().TrimEnd('\r');
            if (line.Length >= MaxMessageLength)
                throw new InvalidDataException("Получено слишком большое сообщение.");
            line.Append(buffer[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync();
    }
}
