using System.Buffers.Binary;
using System.Text;

namespace BorderlessMouse.Protocol;

/// <summary>Stałe protokołu – zgodne z PROTOCOL.md i aplikacją macOS.</summary>
public static class ProtocolConstants
{
    public const byte Version = 1;
    public const int DefaultControlPort = 47800;
    public const int DiscoveryPort = 47801;
    public const int DefaultAudioPort = 47802;
    public const string DiscoveryRequest = "BLM1?";
    public const string DiscoveryReply = "BLM1!";
    public const ushort AudioMagic = 0x4D42;
    /// <summary>Maksymalny rozmiar długiej ramki.</summary>
    public const int MaxLongFrame = MaxClipboardImageBytes + 1;
    /// <summary>Limit tekstu schowka (bajty UTF-8).</summary>
    public const int MaxClipboardBytes = 1024 * 1024;
    public const int MaxClipboardImageBytes = 32 * 1024 * 1024;
    public const long MaxClipboardImagePixels = 64 * 1024 * 1024;
}

public enum MessageType : byte
{
    Hello = 0x01,
    Welcome = 0x02,
    MouseMove = 0x10,
    MouseButton = 0x11,
    MouseWheel = 0x12,
    Key = 0x20,
    ReleaseAll = 0x21,
    Enter = 0x30,
    Leave = 0x31,
    AudioStart = 0x40,
    AudioStop = 0x41,
    AudioFormat = 0x42,
    Ping = 0x50,
    Pong = 0x51,
    Status = 0x60,
    Clipboard = 0x70,
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
    private static byte[] Make(MessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ProtocolConstants.MaxLongFrame) throw new ArgumentException("payload too large");
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

    public static byte[] Hello(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 200) nameBytes = nameBytes[..200];
        Span<byte> p = stackalloc byte[1 + nameBytes.Length];
        p[0] = ProtocolConstants.Version;
        nameBytes.CopyTo(p[1..]);
        return Make(MessageType.Hello, p);
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

    public static byte[] Ping(ulong ts)
    {
        Span<byte> p = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(p, ts);
        return Make(MessageType.Ping, p);
    }

    // --- parsowanie ---

    public static (ScreenEdge edge, float ratio)? ParseLeave(ReadOnlySpan<byte> p)
    {
        if (p.Length < 5) return null;
        return ((ScreenEdge)p[0], BinaryPrimitives.ReadSingleLittleEndian(p[1..]));
    }

    public static AudioFormatInfo? ParseAudioFormat(ReadOnlySpan<byte> p)
    {
        if (p.Length < 7) return null;
        var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(p);
        var msg = p.Length > 7 ? Encoding.UTF8.GetString(p[7..]) : string.Empty;
        return new AudioFormatInfo(rate, p[4], p[5], p[6], msg);
    }

    public static string ParseWelcome(ReadOnlySpan<byte> p)
        => p.Length > 1 ? Encoding.UTF8.GetString(p[1..]) : string.Empty;

    public static ulong? ParsePong(ReadOnlySpan<byte> p)
        => p.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(p) : null;
}
