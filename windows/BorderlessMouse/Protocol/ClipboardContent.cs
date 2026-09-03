using System.Buffers.Binary;
using System.Text;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Protocol;

public enum ClipboardFormat : byte
{
    Utf8Text = 0,
    Png = 1,
}

/// <summary>Wspólny format schowka; obrazy na łączu są zawsze PNG.</summary>
public sealed class ClipboardContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public ClipboardFormat Format { get; }
    public ReadOnlyMemory<byte> Data { get; }
    public string? Text => Format == ClipboardFormat.Utf8Text ? StrictUtf8.GetString(Data.Span) : null;
    public string Summary => Text is { } text
        ? T($"{text.Length} zn.", $"{text.Length} characters")
        : T($"obraz PNG ({(Data.Length + 1023) / 1024} KiB)", $"PNG image ({(Data.Length + 1023) / 1024} KiB)");

    private ClipboardContent(ClipboardFormat format, byte[] data)
    {
        Format = format;
        Data = data;
    }

    public static ClipboardContent? FromText(string text) => Create(ClipboardFormat.Utf8Text, Encoding.UTF8.GetBytes(text));

    public static ClipboardContent? Create(ClipboardFormat format, ReadOnlySpan<byte> data)
    {
        switch (format)
        {
            case ClipboardFormat.Utf8Text:
                if (data.IsEmpty || data.Length > ProtocolConstants.MaxClipboardBytes) return null;
                try { StrictUtf8.GetCharCount(data); }
                catch (DecoderFallbackException) { return null; }
                break;
            case ClipboardFormat.Png:
                // Sprawdź wymiary przed dekodowaniem mocno skompresowanego obrazu.
                if (data.Length < 33 || data.Length > ProtocolConstants.MaxClipboardImageBytes ||
                    !data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 })) return null;
                var width = (ulong)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
                var height = (ulong)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
                if (width == 0 || height == 0 || width * height > ProtocolConstants.MaxClipboardImagePixels) return null;
                break;
            default:
                return null;
        }
        return new ClipboardContent(format, data.ToArray());
    }
}
