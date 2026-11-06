using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DcsVoiceCom.DcsBios;

public sealed record DcsBiosEndpoints
{
    public string ExportMulticastGroup { get; init; } = "239.255.50.10";
    public int ExportPort { get; init; } = 5010;
    public string InputHost { get; init; } = "127.0.0.1";
    public int InputPort { get; init; } = 7778;

    /// <summary>
    /// Accept export packets from other computers too. Off by default: only this PC's own DCS-BIOS can then set the
    /// cockpit state. Turn on when DCS runs on another PC.
    /// </summary>
    public bool AllowRemoteExport { get; init; }
}

/// <summary>
/// UDP link to DCS-BIOS: listens to the export multicast stream into a <see cref="StateBuffer"/>
/// and sends input lines to the import port. DCS-BIOS is a separate GPL program; this class only
/// talks to it over the network.
/// </summary>
public sealed class DcsBiosConnection : IBiosSender, IAsyncDisposable
{
    /// <summary>_ACFT_NAME lives in the MetadataStart module at address 0x0000, 24 bytes.</summary>
    public const int AircraftNameAddress = 0x0000;
    public const int AircraftNameLength = 24;

    private readonly DcsBiosEndpoints _endpoints;
    private readonly ExportParser _parser;
    private readonly UdpClient _sender = new();
    private readonly IPEndPoint _inputEndpoint;
    private UdpClient? _receiver;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private string _aircraft = string.Empty;
    private long _frameCount;
    private HashSet<IPAddress> _localAddresses = [];
    private DateTime _localAddressesAt;
    private DateTime _lastErrorAt;

    public DcsBiosConnection(DcsBiosEndpoints? endpoints = null, StateBuffer? buffer = null)
    {
        _endpoints = endpoints ?? new DcsBiosEndpoints();
        State = buffer ?? new StateBuffer();
        _parser = new ExportParser(State);
        _parser.FrameSync += OnFrameSync;
        _inputEndpoint = new IPEndPoint(IPAddress.Parse(_endpoints.InputHost), _endpoints.InputPort);
    }

    public StateBuffer State { get; }

    /// <summary>Current _ACFT_NAME, empty when no mission is running.</summary>
    public string Aircraft => _aircraft;

    public long FrameCount => Interlocked.Read(ref _frameCount);

    public DateTimeOffset LastFrameAt { get; private set; }

    /// <summary>Raised with the new _ACFT_NAME (empty string when the mission ends).</summary>
    public event Action<string>? AircraftChanged;

    /// <summary>Raised after each complete export frame.</summary>
    public event Action? FrameReceived;

    /// <summary>
    /// Raised when handling a packet threw (usually an <see cref="AircraftChanged"/> or <see cref="FrameReceived"/>
    /// handler). Receiving goes on; repeats within 10 seconds are not reported.
    /// </summary>
    public event Action<Exception>? ReceiveError;

    public void Start()
    {
        if (_receiveLoop != null) return;

        var receiver = new UdpClient(AddressFamily.InterNetwork);
        // Other DCS-BIOS consumers (panels, SimHub, ...) listen on the same port.
        receiver.ExclusiveAddressUse = false;
        receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Any, _endpoints.ExportPort));
        receiver.JoinMulticastGroup(IPAddress.Parse(_endpoints.ExportMulticastGroup));
        _receiver = receiver;

        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public async Task SendAsync(string controlId, string argument, CancellationToken cancellationToken = default)
    {
        var line = Encoding.ASCII.GetBytes($"{controlId} {argument}\n");
        await _sender.SendAsync(line, _inputEndpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Feeds raw export bytes, as if received from the network. Used by tests and replay.</summary>
    public void Feed(ReadOnlySpan<byte> data) => _parser.Feed(data);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _receiver!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            if (!_endpoints.AllowRemoteExport && !IsLocal(result.RemoteEndPoint.Address)) continue;
            try
            {
                _parser.Feed(result.Buffer);
            }
            catch (Exception ex)
            {
                // An error in one packet's handlers must not stop live state for the rest of the session.
                ReportError(ex);
            }
        }
    }

    private void ReportError(Exception ex)
    {
        var now = DateTime.UtcNow;
        if (now - _lastErrorAt < TimeSpan.FromSeconds(10)) return;
        _lastErrorAt = now;
        try { ReceiveError?.Invoke(ex); } catch { /* a failing error handler must not stop the loop either */ }
    }

    /// <summary>
    /// True when <paramref name="address"/> belongs to this PC. The interface list is re-read at most every
    /// 5 seconds, for addresses that changed after start (DHCP, VPN).
    /// </summary>
    private bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || _localAddresses.Contains(address)) return true;
        if (DateTime.UtcNow - _localAddressesAt < TimeSpan.FromSeconds(5)) return false;
        _localAddressesAt = DateTime.UtcNow;
        _localAddresses = LocalAddresses();
        return _localAddresses.Contains(address);
    }

    private static HashSet<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private void OnFrameSync()
    {
        Interlocked.Increment(ref _frameCount);
        LastFrameAt = DateTimeOffset.UtcNow;

        var name = State.ReadString(AircraftNameAddress, AircraftNameLength);
        try
        {
            if (name != _aircraft)
            {
                _aircraft = name;
                AircraftChanged?.Invoke(name);
            }
            FrameReceived?.Invoke();
        }
        catch (Exception ex)
        {
            // Caught here so the rest of the packet is still parsed.
            ReportError(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_receiveLoop != null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _receiver?.Dispose();
        _sender.Dispose();
        _cts?.Dispose();
    }
}
