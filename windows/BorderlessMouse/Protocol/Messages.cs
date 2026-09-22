using System.Buffers.Binary;
using System.Text;

namespace BorderlessMouse.Protocol;

/// <summary>Stałe protokołu – zgodne z PROTOCOL.md i aplikacją macOS.</summary>
public static class ProtocolConstants
{
    public const byte Version = 2;
    public const int DefaultControlPort = 47800;
    public const int DiscoveryPort = 47801;
    public const int DefaultAudioPort = 47802;
    public const string DiscoveryRequest = "BLM2?";
    public const string DiscoveryReply = "BLM2!";
    public const ushort AudioMagic = 0x4D42;
    public const byte AudioVersion = 2;
    public const int AudioHeaderBytes = 32;
    public const int AudioTagBytes = 16;
    /// <summary>Maksymalny rozmiar długiej ramki.</summary>
    public const int MaxLongFrame = MaxClipboardImageBytes + 1;
    public const int MaxWireFrame = MaxLongFrame + 64;
    /// <summary>Limit tekstu schowka (bajty UTF-8).</summary>
    public const int MaxClipboardBytes = 1024 * 1024;
    public const int MaxClipboardImageBytes = 32 * 1024 * 1024;
    public const long MaxClipboardImagePixels = 64 * 1024 * 1024;
}

public enum MessageType : byte
{
    Hello = 0x01,
    Challenge = 0x02,
    Authenticate = 0x03,
    Secure = 0x04,
    Reject = 0x05,
    Ready = 0x06,
    MouseMove = 0x10,
    MouseButton = 0x11,
    MouseWheel = 0x12,
    MouseAbsolute = 0x13,
    Key = 0x20,
    ReleaseAll = 0x21,
    Enter = 0x30,
    Leave = 0x31,
    WindowEnter = 0x32,
    WindowLeave = 0x33,
    WindowHandoff = 0x34,
    AudioStart = 0x40,
    AudioStop = 0x41,
    AudioFormat = 0x42,
    Ping = 0x50,
    Pong = 0x51,
    Status = 0x60,
    Clipboard = 0x70,
    DisplayStart = 0x80,
    DisplayStop = 0x81,
    DisplayReady = 0x82,
    DisplayKeyframe = 0x83,
    DisplayFocus = 0x84,
    DisplayMode = 0x85,
    DisplayWindows = 0x86,
}

/// <summary>Tryb ekranu wirtualnego: cały pulpit albo same okna Maca na pulpicie Windows.</summary>
public enum DisplayMode : byte
{
    Fullscreen = 0,
    Windows = 1,
}

/// <summary>Prostokąt na ekranie wirtualnym w jednostkach 0…65535.</summary>
public readonly record struct NormalizedRect(ushort X, ushort Y, ushort Width, ushort Height, byte Flags)
{
    public const byte MenuBar = 0x01;
}

public enum ScreenEdge : byte
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3,
}

[Flags]
public enum StatusFlags : byte
{
    None = 0,
    AccessibilityGranted = 1 << 0,
    AudioCapturing = 1 << 1,
    CursorOnMac = 1 << 2,
    /// <summary>macOS potrafi utworzyć wirtualny monitor.</summary>
    DisplaySupported = 1 << 3,
    /// <summary>Użytkownik Maca zezwala na ekran wirtualny.</summary>
    DisplayEnabled = 1 << 4,
    DisplayStreaming = 1 << 5,
    /// <summary>Mac obsługuje tryb okien.</summary>
    WindowModeSupported = 1 << 6,
}

/// <summary>Odpowiedź Maca na DISPLAY_START (lub komunikat o zatrzymaniu, gdy Status != 0).</summary>
public sealed record DisplayReadyInfo(byte Status, ushort Port, byte[] Key, byte[] Token, int Width, int Height, string Message)
{
    public bool IsOk => Status == 0;
}

public readonly record struct AudioFormatInfo(int SampleRate, int Channels, byte Format, byte Status, string Message)
{
    public bool IsOk => Status == 0;
}

/// <summary>
/// Budowanie ramek: [type u8][len u8][payload]. Payloady ≥ 255 bajtów idą jako
/// długie ramki: [type u8][0xFF][length u32][payload].
/// </summary>
public static class Frame
{
    public static byte[] Make(MessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ProtocolConstants.MaxWireFrame) throw new ArgumentException("payload too large");
        if (payload.Length < 255)
        {
            var buf = new byte[payload.Length + 2];
            buf[0] = (byte)type;
            buf[1] = (byte)payload.Length;
            payload.CopyTo(buf.AsSpan(2));
            return buf;
        }
        var big = new byte[payload.Length + 6];
        big[0] = (byte)type;
        big[1] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(big.AsSpan(6));
        return big;
    }

    public static byte[] Clipboard(ClipboardContent content)
    {
        var bytes = content.Data;
        var payload = new byte[bytes.Length + 1];
        payload[0] = (byte)content.Format;
        bytes.Span.CopyTo(payload.AsSpan(1));
        return Make(MessageType.Clipboard, payload);
    }

    public static ClipboardContent? ParseClipboard(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return null;
        return ClipboardContent.Create((ClipboardFormat)p[0], p[1..]);
    }

    public static byte[] Hello(string name, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != BorderlessMouse.Security.ControlCrypto.NonceBytes) throw new ArgumentException("invalid nonce");
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 200) nameBytes = nameBytes[..200];
        Span<byte> p = stackalloc byte[1 + BorderlessMouse.Security.ControlCrypto.NonceBytes + nameBytes.Length];
        p[0] = ProtocolConstants.Version;
        nonce.CopyTo(p[1..]);
        nameBytes.CopyTo(p[(1 + BorderlessMouse.Security.ControlCrypto.NonceBytes)..]);
        return Make(MessageType.Hello, p);
    }

    public static byte[] Challenge(string name, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> proof)
    {
        if (nonce.Length != BorderlessMouse.Security.ControlCrypto.NonceBytes || proof.Length != BorderlessMouse.Security.ControlCrypto.ProofBytes)
            throw new ArgumentException("invalid challenge");
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 200) nameBytes = nameBytes[..200];
        var p = new byte[1 + BorderlessMouse.Security.ControlCrypto.NonceBytes + BorderlessMouse.Security.ControlCrypto.ProofBytes + nameBytes.Length];
        p[0] = ProtocolConstants.Version;
        nonce.CopyTo(p.AsSpan(1));
        proof.CopyTo(p.AsSpan(1 + BorderlessMouse.Security.ControlCrypto.NonceBytes));
        nameBytes.CopyTo(p.AsSpan(1 + BorderlessMouse.Security.ControlCrypto.NonceBytes + BorderlessMouse.Security.ControlCrypto.ProofBytes));
        return Make(MessageType.Challenge, p);
    }

    public static byte[] Authenticate(ReadOnlySpan<byte> proof)
    {
        if (proof.Length != BorderlessMouse.Security.ControlCrypto.ProofBytes) throw new ArgumentException("invalid proof");
        return Make(MessageType.Authenticate, proof);
    }

    public static byte[] Secure(ReadOnlySpan<byte> envelope) => Make(MessageType.Secure, envelope);

    public static byte[] Ready(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 200) nameBytes = nameBytes[..200];
        var payload = new byte[1 + nameBytes.Length];
        payload[0] = ProtocolConstants.Version;
        nameBytes.CopyTo(payload.AsSpan(1));
        return Make(MessageType.Ready, payload);
    }

    public static byte[] Reject(string reason)
    {
        var bytes = Encoding.UTF8.GetBytes(reason);
        if (bytes.Length > 200) bytes = bytes[..200];
        return Make(MessageType.Reject, bytes);
    }

    public static byte[] MouseMove(int dx, int dy)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(p, (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(p[2..], (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        return Make(MessageType.MouseMove, p);
    }

    public static byte[] MouseButton(int button, bool down)
    {
        Span<byte> p = stackalloc byte[2];
        p[0] = (byte)button;
        p[1] = down ? (byte)1 : (byte)0;
        return Make(MessageType.MouseButton, p);
    }

    public static byte[] MouseWheel(int dx, int dy)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(p, (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(p[2..], (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        return Make(MessageType.MouseWheel, p);
    }

    public static byte[] Key(ushort scancode, ushort vk, bool extended, bool down, bool repeat)
    {
        Span<byte> p = stackalloc byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(p, scancode);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], vk);
        byte flags = 0;
        if (extended) flags |= 0x01;
        if (down) flags |= 0x02;
        if (repeat) flags |= 0x04;
        p[4] = flags;
        return Make(MessageType.Key, p);
    }

    public static byte[] ReleaseAll() => Make(MessageType.ReleaseAll, ReadOnlySpan<byte>.Empty);

    public static byte[] Enter(ScreenEdge edge, float ratio)
    {
        Span<byte> p = stackalloc byte[5];
        p[0] = (byte)edge;
        BinaryPrimitives.WriteSingleLittleEndian(p[1..], ratio);
        return Make(MessageType.Enter, p);
    }

    public static byte[] AudioStart(ushort udpPort)
    {
        Span<byte> p = stackalloc byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(p, udpPort);
        p[2] = 0; // s16le
        return Make(MessageType.AudioStart, p);
    }

    public static byte[] AudioStop() => Make(MessageType.AudioStop, ReadOnlySpan<byte>.Empty);

    /// <summary>Prośba o wirtualny monitor Maca o rozmiarze monitora Windows (piksele fizyczne).</summary>
    public static byte[] DisplayStart(int width, int height, int scalePercent, ScreenEdge macEdgeFacingWindows,
        uint maxBitrateKbps = 0, Protocol.DisplayMode mode = Protocol.DisplayMode.Fullscreen)
    {
        Span<byte> p = stackalloc byte[13];
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)Math.Clamp(width, 320, 8192));
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], (ushort)Math.Clamp(height, 240, 8192));
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], (ushort)Math.Clamp(scalePercent, 50, 400));
        p[6] = (byte)macEdgeFacingWindows;
        p[7] = 0; // H.264
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], maxBitrateKbps);
        p[12] = (byte)mode;
        return Make(MessageType.DisplayStart, p);
    }

    public static byte[] SetDisplayMode(Protocol.DisplayMode mode) => Make(MessageType.DisplayMode, [(byte)mode]);

    private static byte[] Point(MessageType type, ushort x, ushort y)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(p, x);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], y);
        return Make(type, p);
    }

    /// <summary>Kursor Windows wszedł nad okno Maca (punkt 0…65535 na ekranie wirtualnym).</summary>
    public static byte[] WindowEnter(ushort x, ushort y) => Point(MessageType.WindowEnter, x, y);
    public static byte[] MouseAbsolute(ushort x, ushort y) => Point(MessageType.MouseAbsolute, x, y);
    /// <summary>Kursor zszedł z okna Maca; <paramref name="keepKeyboard"/> – klawiatura dalej pisze w tym oknie.</summary>
    public static byte[] WindowLeave(bool keepKeyboard) => Make(MessageType.WindowLeave, [keepKeyboard ? (byte)1 : (byte)0]);

    public static byte[] DisplayStop() => Make(MessageType.DisplayStop, ReadOnlySpan<byte>.Empty);
    public static byte[] DisplayKeyframe() => Make(MessageType.DisplayKeyframe, ReadOnlySpan<byte>.Empty);

    public static byte[] Ping(ulong ts)
    {
        Span<byte> p = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(p, ts);
        return Make(MessageType.Ping, p);
    }

    public static byte[] Pong(ReadOnlySpan<byte> payload) => Make(MessageType.Pong, payload);

    // --- parsowanie ---

    public static (ScreenEdge edge, float ratio)? ParseLeave(ReadOnlySpan<byte> p)
    {
        if (p.Length < 5) return null;
        return ((ScreenEdge)p[0], BinaryPrimitives.ReadSingleLittleEndian(p[1..]));
    }

    /// <summary>Opcjonalny szósty bajt LEAVE: bit 0 = kursor wyszedł z ekranu wirtualnego.</summary>
    public static bool LeaveFromVirtualDisplay(ReadOnlySpan<byte> p) => p.Length >= 6 && (p[5] & 0x01) != 0;

    public static DisplayReadyInfo? ParseDisplayReady(ReadOnlySpan<byte> p)
    {
        const int fixedBytes = 1 + 2 + VideoStream.KeyBytes + VideoStream.TokenBytes + 2 + 2;
        if (p.Length < fixedBytes || p.Length > fixedBytes + 200) return null;
        var offset = 3;
        var key = p.Slice(offset, VideoStream.KeyBytes).ToArray();
        offset += VideoStream.KeyBytes;
        var token = p.Slice(offset, VideoStream.TokenBytes).ToArray();
        offset += VideoStream.TokenBytes;
        var width = BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(p[(offset + 2)..]);
        var message = Encoding.UTF8.GetString(p[fixedBytes..]);
        return new DisplayReadyInfo(p[0], BinaryPrimitives.ReadUInt16LittleEndian(p[1..]), key, token, width, height, message);
    }

    public static bool? ParseDisplayFocus(ReadOnlySpan<byte> p) => p.Length >= 1 ? p[0] != 0 : null;

    public static List<NormalizedRect>? ParseDisplayWindows(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1 || p.Length != 1 + p[0] * 9) return null;
        var rects = new List<NormalizedRect>(p[0]);
        for (var i = 0; i < p[0]; i++)
        {
            var r = p.Slice(1 + i * 9, 9);
            rects.Add(new NormalizedRect(BinaryPrimitives.ReadUInt16LittleEndian(r), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), BinaryPrimitives.ReadUInt16LittleEndian(r[6..]), r[8]));
        }
        return rects;
    }

    public static (ushort x, ushort y)? ParseWindowHandoff(ReadOnlySpan<byte> p)
        => p.Length >= 4 ? (BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..])) : null;

    public static AudioFormatInfo? ParseAudioFormat(ReadOnlySpan<byte> p)
    {
        if (p.Length < 7) return null;
        var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(p);
        var msg = p.Length > 7 ? Encoding.UTF8.GetString(p[7..]) : string.Empty;
        return new AudioFormatInfo(rate, p[4], p[5], p[6], msg);
    }

    public static string ParseReady(ReadOnlySpan<byte> p)
        => p.Length > 1 ? Encoding.UTF8.GetString(p[1..]) : string.Empty;

    public static ulong? ParsePong(ReadOnlySpan<byte> p)
        => p.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(p) : null;

    public static bool TryParseSingle(ReadOnlySpan<byte> data, out MessageType type, out byte[] payload)
    {
        type = default;
        payload = Array.Empty<byte>();
        if (data.Length < 2 || !Enum.IsDefined(typeof(MessageType), data[0])) return false;
        type = (MessageType)data[0];
        var length = (int)data[1];
        var header = 2;
        if (length == 0xFF)
        {
            if (data.Length < 6) return false;
            var unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
            if (unsignedLength > ProtocolConstants.MaxLongFrame) return false;
            length = (int)unsignedLength;
            header = 6;
        }
        if (length > ProtocolConstants.MaxLongFrame || data.Length != header + length) return false;
        payload = data.Slice(header, length).ToArray();
        return true;
    }
}
