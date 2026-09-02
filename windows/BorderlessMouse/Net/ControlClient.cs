using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using BorderlessMouse.Protocol;

namespace BorderlessMouse.Net;

/// <summary>
/// Klient TCP kanału sterowania. Wysyłanie przez kolejkę (wątek zapisu
/// skleja wiele ramek w jeden write), odbiór na osobnym wątku.
/// Zdarzenia są wołane z wątków tła – UI musi je przekierować.
/// </summary>
public sealed class ControlClient : IDisposable
{
    public event Action<string>? Connected;          // nazwa Maca
    public event Action<string?>? Disconnected;      // powód (null = na żądanie)
    public event Action<MessageType, byte[]>? MessageReceived;
    public event Action<double>? RttMeasured;        // ms

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Channel<byte[]>? _outbox;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _connected;
    private int _disconnectSignalled;

    public bool IsConnected => _connected;
    public string RemoteAddress { get; private set; } = string.Empty;

    public async Task ConnectAsync(string host, int port, string localName, CancellationToken ct)
    {
        Disconnect(null);
        var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 65536, SendBufferSize = 65536 };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        var cts = new CancellationTokenSource();
        var outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _outbox = outbox;
            _cts = cts;
            _disconnectSignalled = 0;
            RemoteAddress = host;
        }

        _ = Task.Run(() => WriterLoop(_stream!, outbox.Reader, cts.Token));
        _ = Task.Run(() => ReaderLoop(_stream!, cts.Token));
        _ = Task.Run(() => PingLoop(cts.Token));
        Send(Frame.Hello(localName));
    }

    public void Disconnect(string? reason)
    {
        TcpClient? tcp;
        CancellationTokenSource? cts;
        Channel<byte[]>? outbox;
        bool wasConnected;
        lock (_gate)
        {
            tcp = _tcp;
            cts = _cts;
            outbox = _outbox;
            wasConnected = _connected;
            _tcp = null;
            _stream = null;
            _cts = null;
            _outbox = null;
            _connected = false;
        }
        outbox?.Writer.TryComplete();
        cts?.Cancel();
        try { tcp?.Close(); } catch { /* ignore */ }
        cts?.Dispose();
        if (tcp is not null && Interlocked.Exchange(ref _disconnectSignalled, 1) == 0 && (wasConnected || reason is not null))
        {
            Disconnected?.Invoke(reason);
        }
    }

    public void Send(byte[] frame)
    {
        var outbox = _outbox;
        outbox?.Writer.TryWrite(frame);
    }

    public void SendMouseMove(int dx, int dy) => Send(Frame.MouseMove(dx, dy));
    public void SendMouseButton(int button, bool down) => Send(Frame.MouseButton(button, down));
    public void SendMouseWheel(int dx, int dy) => Send(Frame.MouseWheel(dx, dy));
    public void SendKey(ushort scancode, ushort vk, bool extended, bool down, bool repeat) => Send(Frame.Key(scancode, vk, extended, down, repeat));
    public void SendEnter(ScreenEdge edge, float ratio) => Send(Frame.Enter(edge, ratio));
    public void SendReleaseAll() => Send(Frame.ReleaseAll());
    public void SendAudioStart(ushort udpPort) => Send(Frame.AudioStart(udpPort));
    public void SendAudioStop() => Send(Frame.AudioStop());
    public void SendClipboard(ClipboardContent content) => Send(Frame.Clipboard(content));

    private async Task WriterLoop(NetworkStream stream, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        var batch = new MemoryStream();
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.SetLength(0);
                while (reader.TryRead(out var frame))
                {
                    batch.Write(frame, 0, frame.Length);
                    if (batch.Length > 32 * 1024) break;
                }
                if (batch.Length > 0)
                {
                    await stream.WriteAsync(batch.GetBuffer().AsMemory(0, (int)batch.Length), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Disconnect("Błąd zapisu: " + ex.Message);
        }
    }

    private async Task ReaderLoop(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[65536];
        var inbox = new List<byte>(4096);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    Disconnect("Mac zamknął połączenie");
                    return;
                }
                inbox.AddRange(new ArraySegment<byte>(buffer, 0, n));
                var cursor = 0;
                while (inbox.Count - cursor >= 2)
                {
                    var type = (MessageType)inbox[cursor];
                    int len = inbox[cursor + 1];
                    var header = 2;
                    if (len == 0xFF)
                    {
                        // długa ramka: u32 length
                        if (inbox.Count - cursor < 6) break;
                        len = inbox[cursor + 2] | (inbox[cursor + 3] << 8) | (inbox[cursor + 4] << 16) | (inbox[cursor + 5] << 24);
                        header = 6;
                        if (len < 0 || len > ProtocolConstants.MaxLongFrame)
                        {
                            Disconnect("Nieprawidłowa ramka z Maca");
                            return;
                        }
                    }
                    if (inbox.Count - cursor < header + len) break;
                    var payload = inbox.GetRange(cursor + header, len).ToArray();
                    cursor += header + len;
                    Handle(type, payload);
                }
                if (cursor > 0) inbox.RemoveRange(0, cursor);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Disconnect("Błąd odczytu: " + ex.Message);
        }
    }

    private void Handle(MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.Welcome:
                _connected = true;
                Connected?.Invoke(Frame.ParseWelcome(payload));
                break;
            case MessageType.Pong:
                if (Frame.ParsePong(payload) is { } sent)
                {
                    var rtt = (_clock.ElapsedTicks - (long)sent) * 1000.0 / Stopwatch.Frequency;
                    RttMeasured?.Invoke(rtt);
                }
                break;
            case MessageType.Ping:
                Send(payload); // echo – Mac na razie nie pinguje, ale protokół to dopuszcza
                break;
            default:
                MessageReceived?.Invoke(type, payload);
                break;
        }
    }

    private async Task PingLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                Send(Frame.Ping((ulong)_clock.ElapsedTicks));
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => Disconnect(null);
}
