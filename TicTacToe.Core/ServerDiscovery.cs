using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TicTacToe.Core;

public static class ServerDiscovery
{
    public const int DiscoveryPort = 5001;
    private const string DiscoveryRequest = "TICTACTOE_DISCOVER_V1";

    public static async Task<IReadOnlyList<ServerAdvertisement>> FindServersAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient(0) { EnableBroadcast = true };
        var payload = Encoding.UTF8.GetBytes(DiscoveryRequest);
        await udp.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var servers = new Dictionary<string, ServerAdvertisement>(StringComparer.OrdinalIgnoreCase);
        while (!timeoutSource.IsCancellationRequested)
        {
            try
            {
                var response = await udp.ReceiveAsync(timeoutSource.Token);
                var message = JsonSerializer.Deserialize<NetworkMessage>(response.Buffer);
                if (message is { Type: "ServerFound", Port: > 0 })
                {
                    var advertisement = new ServerAdvertisement(response.RemoteEndPoint.Address.ToString(), message.Port.Value, message.Name ?? "TicTacToe");
                    servers[$"{advertisement.Address}:{advertisement.Port}"] = advertisement;
                }
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                break;
            }
            catch (JsonException)
            {
            }
            catch (SocketException)
            {
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return servers.Values.ToArray();
    }

    internal static bool IsDiscoveryRequest(ReadOnlySpan<byte> payload) =>
        payload.SequenceEqual(Encoding.UTF8.GetBytes(DiscoveryRequest));
}
