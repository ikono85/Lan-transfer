using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace LanLink.Core.Discovery;

public sealed record DiscoveredPeer(string Address, string DeviceName, int Port, DateTime LastSeenUtc);

/// <summary>
/// Annonce ce PC par balises UDP (broadcast sur chaque interface) et écoute celles des autres.
/// Les balises ne sont pas authentifiées : elles servent uniquement à remplir la liste, la vraie
/// vérification se fait à la connexion (mot de passe).
/// </summary>
public sealed class PeerDiscovery : IAsyncDisposable
{
    public const int UdpPort = 45871;
    private const string Magic = "LANLINK1";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(12);

    private sealed record Beacon(string App, string Id, string Name, int Port);

    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly Func<string> _deviceName;
    private readonly int _tcpPort;
    private readonly int _udpPort;
    private readonly Dictionary<string, DiscoveredPeer> _peers = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _listener;
    private Task[] _tasks = Array.Empty<Task>();

    public event Action? PeersChanged;

    public PeerDiscovery(Func<string> deviceName, int tcpPort, int udpPort = UdpPort)
    {
        _deviceName = deviceName;
        _tcpPort = tcpPort;
        _udpPort = udpPort;
    }

    public void Start()
    {
        _listener = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, _udpPort));
        _tasks = new[] { Task.Run(ListenAsync), Task.Run(AnnounceAsync), Task.Run(ExpireAsync) };
    }

    public IReadOnlyList<DiscoveredPeer> Snapshot()
    {
        lock (_lock)
            return _peers.Values.OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _listener!.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (Parse(result.Buffer) is not { } beacon || beacon.Id == _instanceId) continue;
                var address = result.RemoteEndPoint.Address.MapToIPv4().ToString();
                lock (_lock)
                    _peers[address] = new DiscoveredPeer(address, beacon.Name, beacon.Port, DateTime.UtcNow);
                PeersChanged?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(500, _cts.Token).ConfigureAwait(false); }
        }
    }

    private static Beacon? Parse(byte[] data)
    {
        if (data.Length < Magic.Length + 2 || data.Length > 1024) return null;
        if (System.Text.Encoding.ASCII.GetString(data, 0, Magic.Length) != Magic) return null;
        try
        {
            var beacon = JsonSerializer.Deserialize<Beacon>(data.AsSpan(Magic.Length), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (beacon is null || beacon.App != "LanLink" || beacon.Port is < 1 or > 65535) return null;
            var name = new string(beacon.Name.Where(c => c >= 32).ToArray());
            return beacon with { Name = name.Length > 64 ? name[..64] : name };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentNullException or NullReferenceException) { return null; }
    }

    private async Task AnnounceAsync()
    {
        using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        while (!_cts.IsCancellationRequested)
        {
            var beacon = new Beacon("LanLink", _instanceId, _deviceName(), _tcpPort);
            var payload = System.Text.Encoding.ASCII.GetBytes(Magic)
                .Concat(JsonSerializer.SerializeToUtf8Bytes(beacon, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                .ToArray();
            foreach (var target in BroadcastAddresses())
            {
                try { await sender.SendAsync(payload, new IPEndPoint(target, _udpPort), _cts.Token).ConfigureAwait(false); }
                catch (SocketException) { /* interface indisponible */ }
                catch (OperationCanceledException) { return; }
            }
            try { await Task.Delay(Interval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Adresse de broadcast de chaque interface IPv4 active (le broadcast global ne sort que sur l'interface par défaut).</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bc);
            }
        }
    }

    private async Task ExpireAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(4), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            var changed = false;
            lock (_lock)
            {
                var limit = DateTime.UtcNow - Expiry;
                foreach (var key in _peers.Where(p => p.Value.LastSeenUtc < limit).Select(p => p.Key).ToList())
                    changed |= _peers.Remove(key);
            }
            if (changed) PeersChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Dispose();
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException) { }
        _cts.Dispose();
    }
}
