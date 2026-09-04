using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BorderlessMouse.Audio;
using BorderlessMouse.Protocol;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Net;

/// <summary>Odbiera wyłącznie uwierzytelnione pakiety audio z aktywnej sesji Maca.</summary>
public sealed class AudioReceiver : IDisposable
{
    private UdpClient? _udp;
    private Thread? _thread;
    private volatile bool _running;
    private ushort _lastSeq;
    private bool _haveSeq;
    private float _level;
    private IPAddress? _expectedAddress;
    private byte[]? _key;
    private ulong _sessionId;
    private ulong _highestCounter;
    private ulong _counterWindow;
    private bool _haveCounter;
    private volatile JitterBufferProvider? _provider;

    public JitterBufferProvider? Provider { get => _provider; set => _provider = value; }
    public int Port { get; private set; }
    public long PacketsReceived;
    public long PacketsLost;
    public long PacketsRejected;
    public long BytesReceived;
    public float Level => _level;

    /// <summary>Otwiera port UDP i wiąże odbiór z adresem oraz kluczem aktywnej sesji.</summary>
    public int Start(int port, string expectedAddress, ReadOnlySpan<byte> key, ulong sessionId)
    {
        Stop();
        if (!IPAddress.TryParse(expectedAddress, out var address))
            throw new ArgumentException(T("Nie można ustalić adresu IPv4 Maca.", "Cannot determine the Mac IPv4 address."), nameof(expectedAddress));
        // Gniazdo sterowania jest dual-stack, więc Mac w IPv4 potrafi przyjść
        // jako adres zmapowany (::ffff:192.168.x.y) – to nadal ten sam host.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException(
                T($"Sesja zestawiona po IPv6 ({expectedAddress}); dźwięk wymaga adresu IPv4 Maca.",
                  $"The session uses IPv6 ({expectedAddress}); audio requires the Mac IPv4 address."),
                nameof(expectedAddress));
        if (key.Length != 32) throw new ArgumentException(T("Nieprawidłowy klucz sesji audio.", "Invalid audio-session key."), nameof(key));

        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        udp.Client.ReceiveBufferSize = 1 << 20;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        SocketHelpers.DisableUdpConnReset(udp.Client);
        Port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        _expectedAddress = address;
        _key = key.ToArray();
        _sessionId = sessionId;
        _udp = udp;
        _running = true;
        _haveSeq = false;
        _haveCounter = false;
        _counterWindow = 0;
        PacketsReceived = PacketsLost = PacketsRejected = BytesReceived = 0;
        _thread = new Thread(() => Loop(udp))
        {
            IsBackground = true,
            Name = "blm-audio-rx",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        return Port;
    }

    public void Stop()
    {
        _running = false;
        var udp = _udp;
        var thread = _thread;
        try { udp?.Close(); } catch { /* ignore */ }
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(1));
        _udp = null;
        _thread = null;
        _level = 0;
        if (_key is { } key) CryptographicOperations.ZeroMemory(key);
        _key = null;
        _expectedAddress = null;
        _haveCounter = false;
    }

    private void Loop(UdpClient udp)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            byte[] packet;
            try { packet = udp.Receive(ref remote); }
            catch (SocketException) { if (!_running) break; continue; }
            catch (ObjectDisposedException) { break; }

            var source = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            if (_expectedAddress is null || !source.Equals(_expectedAddress))
            {
                Interlocked.Increment(ref PacketsRejected);
                continue;
            }
            var clear = Authenticate(packet, out var sequence, out var frames, out var channels, out var format);
            if (clear is null)
            {
                Interlocked.Increment(ref PacketsRejected);
                continue;
            }
            var provider = Provider;
            if (provider is null) continue;

            if (_haveSeq)
            {
                // Bufor odtwarzania jest FIFO, więc stare/przestawione pakiety
                // muszą zostać odrzucone zamiast zanieczyszczać kolejność próbek.
                var distance = (ushort)(sequence - _lastSeq);
                if (distance == 0 || distance >= 0x8000)
                {
                    Interlocked.Increment(ref PacketsRejected);
                    CryptographicOperations.ZeroMemory(clear);
                    continue;
                }
                var gap = distance - 1;
                if (gap is > 0 and < 100)
                {
                    Interlocked.Add(ref PacketsLost, gap);
                    var silenceBytes = checked(gap * frames * channels * 2);
                    if (silenceBytes <= 2 * 1024 * 1024) provider.WriteSilence(silenceBytes);
                }
            }
            _lastSeq = sequence;
            _haveSeq = true;

            Interlocked.Increment(ref PacketsReceived);
            Interlocked.Add(ref BytesReceived, clear.Length);
            if (format == 0)
            {
                var peak = 0;
                for (var index = 0; index + 1 < clear.Length; index += 2)
                {
                    var value = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(clear.AsSpan(index)));
                    if (value > peak) peak = value;
                }
                var level = peak / 32768f;
                _level = level > _level ? level : _level * 0.85f;
                provider.Write(clear);
            }
            else
            {
                var count = clear.Length / 4;
                var converted = new byte[count * 2];
                var peak = 0f;
                for (var index = 0; index < count; index++)
                {
                    var sample = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(clear.AsSpan(index * 4)), -1f, 1f);
                    if (Math.Abs(sample) > peak) peak = Math.Abs(sample);
                    BinaryPrimitives.WriteInt16LittleEndian(converted.AsSpan(index * 2), (short)(sample * 32767));
                }
                _level = peak > _level ? peak : _level * 0.85f;
                provider.Write(converted);
            }
        }
    }

    private byte[]? Authenticate(byte[] packet, out ushort sequence, out int frames, out int channels, out byte format)
    {
        sequence = 0;
        frames = channels = 0;
        format = 0;
        if (packet.Length < ProtocolConstants.AudioHeaderBytes + ProtocolConstants.AudioTagBytes || _key is null) return null;
        var span = packet.AsSpan();
        if (BinaryPrimitives.ReadUInt16LittleEndian(span) != ProtocolConstants.AudioMagic ||
            span[2] != ProtocolConstants.AudioVersion || span[3] != 0 ||
            BinaryPrimitives.ReadUInt64LittleEndian(span[4..]) != _sessionId ||
            BinaryPrimitives.ReadUInt16LittleEndian(span[26..]) != 0) return null;

        var counter = BinaryPrimitives.ReadUInt64LittleEndian(span[12..]);
        sequence = BinaryPrimitives.ReadUInt16LittleEndian(span[20..]);
        frames = BinaryPrimitives.ReadUInt16LittleEndian(span[22..]);
        channels = span[24];
        format = span[25];
        if (frames is <= 0 or > 1024 || channels is <= 0 or > 8 || format > 1) return null;
        var bytesPerSample = format == 0 ? 2 : 4;
        var expectedBytes = checked(frames * channels * bytesPerSample);
        var cipherBytes = packet.Length - ProtocolConstants.AudioHeaderBytes - ProtocolConstants.AudioTagBytes;
        if (cipherBytes != expectedBytes) return null;

        var clear = new byte[cipherBytes];
        try
        {
            using var aes = new AesGcm(_key, ProtocolConstants.AudioTagBytes);
            aes.Decrypt(Nonce(counter),
                span.Slice(ProtocolConstants.AudioHeaderBytes, cipherBytes),
                span.Slice(ProtocolConstants.AudioHeaderBytes + cipherBytes, ProtocolConstants.AudioTagBytes),
                clear, span[..ProtocolConstants.AudioHeaderBytes]);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(clear);
            return null;
        }
        if (!AcceptCounter(counter))
        {
            CryptographicOperations.ZeroMemory(clear);
            return null;
        }
        return clear;
    }

    /// <summary>64-pakietowe okno anty-replay tolerujące niewielką zmianę kolejności UDP.</summary>
    private bool AcceptCounter(ulong counter)
    {
        if (!_haveCounter)
        {
            _haveCounter = true;
            _highestCounter = counter;
            _counterWindow = 1;
            return true;
        }
        if (counter > _highestCounter)
        {
            var shift = counter - _highestCounter;
            _counterWindow = shift >= 64 ? 1 : (_counterWindow << (int)shift) | 1;
            _highestCounter = counter;
            return true;
        }
        var distance = _highestCounter - counter;
        if (distance >= 64) return false;
        var bit = 1UL << (int)distance;
        if ((_counterWindow & bit) != 0) return false;
        _counterWindow |= bit;
        return true;
    }

    private byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, _sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public void Dispose() => Stop();
}
