using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using BorderlessMouse.Protocol;
using BorderlessMouse.Security;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Net;

/// <summary>
/// Klient uwierzytelnionego kanału sterowania. Handshake udowadnia obu stronom
/// znajomość kodu parowania, a ramki aplikacyjne są chronione AES-256-GCM.
/// </summary>
public sealed class ControlClient : IDisposable
{
    public event Action<string>? Connected;
    public event Action<string?>? Disconnected;
    public event Action<MessageType, byte[]>? MessageReceived;
    public event Action<double>? RttMeasured;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Channel<byte[]>? _outbox;
    private CancellationTokenSource? _cts;
    private SecureSession? _session;
    private byte[]? _pairingKey;
    private byte[]? _clientNonce;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _connected;
    private int _disconnectSignalled;

    public bool IsConnected => _connected;
    public string RemoteAddress { get; private set; } = string.Empty;
    public byte[]? AudioKey => _connected && _session is { } session ? session.AudioKey.ToArray() : null;
    public ulong? AudioSessionId => _connected ? _session?.AudioSessionId : null;

    public async Task ConnectAsync(string host, int port, string localName, ReadOnlyMemory<byte> pairingKey, CancellationToken ct)
    {
        if (pairingKey.Length != 16) throw new InvalidOperationException(T("Najpierw wpisz kod parowania wyświetlany na Macu.", "Enter the pairing code shown on the Mac first."));
        Disconnect(null);
        var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 65536, SendBufferSize = 65536 };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        var cts = new CancellationTokenSource();
        var outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var nonce = ControlCrypto.RandomNonce();
        lock (_gate)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _outbox = outbox;
            _cts = cts;
            _session = null;
            _pairingKey = pairingKey.ToArray();
            _clientNonce = nonce;
            _disconnectSignalled = 0;
            RemoteAddress = ((System.Net.IPEndPoint)tcp.Client.RemoteEndPoint!).Address.ToString();
        }

        _ = Task.Run(() => WriterLoop(_stream!, outbox.Reader, cts.Token));
        _ = Task.Run(() => ReaderLoop(_stream!, cts.Token));
        _ = Task.Run(() => PingLoop(cts.Token));
        _ = Task.Run(() => HandshakeTimeoutAsync(cts.Token));
        SendRaw(Frame.Hello(localName, nonce));
    }

    public void Disconnect(string? reason)
    {
        TcpClient? tcp;
        CancellationTokenSource? cts;
        Channel<byte[]>? outbox;
        byte[]? pairingKey;
        SecureSession? session;
        bool hadTransport;
        lock (_gate)
        {
            tcp = _tcp;
            cts = _cts;
            outbox = _outbox;
            pairingKey = _pairingKey;
            session = _session;
            hadTransport = tcp is not null;
            _tcp = null;
            _stream = null;
            _cts = null;
            _outbox = null;
            _session = null;
            _pairingKey = null;
            _clientNonce = null;
            _connected = false;
        }
        session?.Dispose();
        if (pairingKey is not null) CryptographicOperations.ZeroMemory(pairingKey);
        outbox?.Writer.TryComplete();
        cts?.Cancel();
        try { tcp?.Close(); } catch { /* ignore */ }
        cts?.Dispose();
        if (hadTransport && Interlocked.Exchange(ref _disconnectSignalled, 1) == 0)
            Disconnected?.Invoke(reason);
    }

    /// <summary>Szyfruje jedną kompletną ramkę aplikacyjną.</summary>
    public void Send(byte[] frame)
    {
        var session = _session;
        if (!_connected || session is null) return;
        var envelope = session.Seal(frame);
        if (envelope is null)
        {
            Disconnect(T("Wyczerpano licznik bezpiecznej sesji.", "The secure-session counter was exhausted."));
            return;
        }
        SendRaw(Frame.Secure(envelope));
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

    private void SendRaw(byte[] frame)
    {
        var outbox = _outbox;
        if (outbox is not null && !outbox.Writer.TryWrite(frame))
            Disconnect(T("Połączenie jest zbyt wolne; zatrzymano kolejkę wejścia.", "The connection is too slow; the input queue was stopped."));
    }

    private async Task WriterLoop(NetworkStream stream, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        using var batch = new MemoryStream();
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
                    await stream.WriteAsync(batch.GetBuffer().AsMemory(0, (int)batch.Length), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Disconnect(T("Błąd zapisu: ", "Write error: ") + ex.Message); }
    }

    private async Task ReaderLoop(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[65536];
        var inbox = new List<byte>(4096);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (count <= 0)
                {
                    Disconnect(T("Mac zamknął połączenie.", "The Mac closed the connection."));
                    return;
                }
                inbox.AddRange(new ArraySegment<byte>(buffer, 0, count));
                if (inbox.Count > ProtocolConstants.MaxWireFrame + 6)
                {
                    Disconnect(T("Przekroczono limit ramki z Maca.", "A frame from the Mac exceeded the size limit."));
                    return;
                }
                var cursor = 0;
                while (inbox.Count - cursor >= 2)
                {
                    var type = (MessageType)inbox[cursor];
                    var length = (int)inbox[cursor + 1];
                    var header = 2;
                    if (length == 0xFF)
                    {
                        if (inbox.Count - cursor < 6) break;
                        var rawLength = (uint)inbox[cursor + 2]
                            | ((uint)inbox[cursor + 3] << 8)
                            | ((uint)inbox[cursor + 4] << 16)
                            | ((uint)inbox[cursor + 5] << 24);
                        if (rawLength > ProtocolConstants.MaxWireFrame)
                        {
                            Disconnect(T("Nieprawidłowa długość ramki z Maca.", "A frame from the Mac has an invalid length."));
                            return;
                        }
                        length = (int)rawLength;
                        header = 6;
                    }
                    if (inbox.Count - cursor < header + length) break;
                    var payload = inbox.GetRange(cursor + header, length).ToArray();
                    cursor += header + length;
                    Handle(type, payload);
                    if (_tcp is null) return;
                }
                if (cursor > 0) inbox.RemoveRange(0, cursor);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Disconnect(T("Błąd odczytu: ", "Read error: ") + ex.Message); }
    }

    private void Handle(MessageType type, byte[] payload)
    {
        if (type == MessageType.Reject)
        {
            var reason = payload.Length == 0 ? "" : Encoding.UTF8.GetString(payload);
            Disconnect(reason switch
            {
                "Serwer jest zajęty lub chwilowo zablokowany" => T("Serwer jest zajęty lub chwilowo zablokowany.", "The Mac is busy or temporarily rate-limited."),
                "Wymagany jest bezpieczny protokół v2" => T("Mac wymaga bezpiecznego protokołu v2.", "The Mac requires secure protocol v2."),
                "Brak kodu parowania na Macu" => T("Na Macu brakuje kodu parowania.", "The Mac has no pairing code."),
                "Kod parowania jest nieprawidłowy" => T("Kod parowania jest nieprawidłowy.", "The pairing code is incorrect."),
                _ => string.IsNullOrWhiteSpace(reason) ? T("Mac odrzucił połączenie.", "The Mac rejected the connection.") : reason,
            });
            return;
        }

        if (!_connected)
        {
            if (type == MessageType.Challenge && _session is null) HandleChallenge(payload);
            else if (type == MessageType.Secure) HandleSecure(payload);
            else Disconnect(T("Nieprawidłowa kolejność bezpiecznego handshake.", "Invalid secure-handshake sequence."));
            return;
        }

        if (type != MessageType.Secure)
        {
            Disconnect(T("Odebrano niezabezpieczoną wiadomość po zestawieniu sesji.", "An unprotected message was received after the session was established."));
            return;
        }
        HandleSecure(payload);
    }

    private void HandleChallenge(ReadOnlySpan<byte> payload)
    {
        const int fixedBytes = 1 + ControlCrypto.NonceBytes + ControlCrypto.ProofBytes;
        var key = _pairingKey;
        var clientNonce = _clientNonce;
        if (payload.Length < fixedBytes || payload.Length > fixedBytes + 200
            || payload[0] != ProtocolConstants.Version || key is null || clientNonce is null)
        {
            Disconnect(T("Mac nie obsługuje bezpiecznego protokołu v2.", "The Mac does not support secure protocol v2."));
            return;
        }
        var serverNonce = payload.Slice(1, ControlCrypto.NonceBytes).ToArray();
        var receivedProof = payload.Slice(1 + ControlCrypto.NonceBytes, ControlCrypto.ProofBytes);
        var expectedProof = ControlCrypto.Proof(key, "server", clientNonce, serverNonce);
        var proofIsValid = CryptographicOperations.FixedTimeEquals(receivedProof, expectedProof);
        CryptographicOperations.ZeroMemory(expectedProof);
        if (!proofIsValid)
        {
            Disconnect(T("Kod parowania nie pasuje do tego Maca.", "The pairing code does not match this Mac."));
            return;
        }
        _session = new SecureSession(key, clientNonce, serverNonce, SecureSession.SessionRole.Client);
        var clientProof = ControlCrypto.Proof(key, "client", clientNonce, serverNonce);
        SendRaw(Frame.Authenticate(clientProof));
        CryptographicOperations.ZeroMemory(clientProof);
    }

    private void HandleSecure(ReadOnlySpan<byte> payload)
    {
        var session = _session;
        var clear = session?.Open(payload);
        if (clear is null || !Frame.TryParseSingle(clear, out var innerType, out var innerPayload))
        {
            Disconnect(T("Błąd integralności bezpiecznej sesji.", "Secure-session integrity check failed."));
            return;
        }
        if (!_connected)
        {
            if (innerType != MessageType.Ready || innerPayload.Length < 1 || innerPayload[0] != ProtocolConstants.Version)
            {
                Disconnect(T("Mac nie potwierdził bezpiecznej sesji.", "The Mac did not confirm the secure session."));
                return;
            }
            _connected = true;
            var name = Frame.ParseReady(innerPayload);
            Connected?.Invoke(name);
            return;
        }
        if (IsHandshake(innerType))
        {
            Disconnect(T("Nieprawidłowa wiadomość handshake w aktywnej sesji.", "Invalid handshake message in an active session."));
            return;
        }
        switch (innerType)
        {
            case MessageType.Pong:
                if (Frame.ParsePong(innerPayload) is { } sent)
                {
                    var rtt = (_clock.ElapsedTicks - (long)sent) * 1000.0 / Stopwatch.Frequency;
                    RttMeasured?.Invoke(rtt);
                }
                break;
            case MessageType.Ping:
                Send(Frame.Pong(innerPayload));
                break;
            default:
                MessageReceived?.Invoke(innerType, innerPayload);
                break;
        }
    }

    private static bool IsHandshake(MessageType type) => type is
        MessageType.Hello or MessageType.Challenge or MessageType.Authenticate or
        MessageType.Secure or MessageType.Reject or MessageType.Ready;

    private async Task PingLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (_connected) Send(Frame.Ping((ulong)_clock.ElapsedTicks));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandshakeTimeoutAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (!_connected)
                Disconnect(T("Mac nie zakończył bezpiecznego parowania w wymaganym czasie.", "The Mac did not finish secure pairing in time."));
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => Disconnect(null);
}
