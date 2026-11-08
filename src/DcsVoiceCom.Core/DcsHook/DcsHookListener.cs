using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DcsVoiceCom.DcsHook;

/// <summary>
/// Receives the DCS hook's messages on 127.0.0.1 (local only) and keeps the current <see cref="DcsSession"/>.
/// </summary>
public sealed class DcsHookListener : IAsyncDisposable
{
    public const int DefaultPort = 7790;

    private readonly int _port;
    private readonly DcsSessionTracker _tracker = new();
    private readonly object _lock = new();
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DcsHookListener(int port = DefaultPort) => _port = port;

    public int Port => _port;

    public DcsSession? Session
    {
        get
        {
            lock (_lock) return _tracker.Session;
        }
    }

    /// <summary>Raised with the new session, or null when the mission ended.</summary>
    public event Action<DcsSession?>? SessionChanged;

    /// <summary>Starts listening. Throws <see cref="SocketException"/> when the port is taken.</summary>
    public void Start()
    {
        if (_loop != null) return;
        _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoop(_client, _cts.Token));
    }

    private async Task ReceiveLoop(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(5)); // wake up now and then to notice a quiet hook
            string? message = null;
            try
            {
                var result = await client.ReceiveAsync(wait.Token).ConfigureAwait(false);
                message = Encoding.UTF8.GetString(result.Buffer);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // e.g. ICMP "port unreachable" echoes on Windows; keep listening
            }
            Process(message, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>One message (or none, on a wake-up); raises <see cref="SessionChanged"/> when the session changed.</summary>
    internal void Process(string? message, DateTimeOffset now)
    {
        DcsSession? session;
        lock (_lock)
        {
            var changed = message != null && _tracker.Handle(message, now);
            changed |= _tracker.Tick(now);
            if (!changed) return;
            session = _tracker.Session;
        }
        SessionChanged?.Invoke(session);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _client?.Dispose();
        if (_loop != null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // already stopping
            }
        }
        _cts?.Dispose();
    }
}
