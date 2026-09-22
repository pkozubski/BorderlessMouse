using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BorderlessMouse.Protocol;

/// <summary>
/// Zaszyfrowany kanał wideo ekranu wirtualnego (osobne połączenie TCP do Maca).
/// Rekord: <c>u32 length</c> + <c>u64 counter</c> + szyfrogram + 16 B tagu AES-256-GCM.
/// Nonce i AAD to <c>"BLMV" || counter</c>. Klucz i token są losowe dla każdego
/// strumienia i przychodzą wyłącznie przez zaszyfrowany kanał sterowania.
/// </summary>
public static class VideoStream
{
    public const int KeyBytes = 32;
    public const int TokenBytes = 16;
    public const int CounterBytes = 8;
    public const int TagBytes = 16;
    public const int LengthBytes = 4;
    public const int FrameHeaderBytes = 14;
    /// <summary>Klatka okna: dodatkowo <c>u32 streamID</c> i <c>u16 cornerRadius</c>.</summary>
    public const int WindowFrameHeaderBytes = 20;
    public const int MaxRecordBytes = 16 * 1024 * 1024;

    public const byte KindH264AccessUnit = 1;
    public const byte KindWindowAccessUnit = 2;
    /// <summary>Strumień paska menu ekranu wirtualnego (tryb okien).</summary>
    public const uint MenuBarStreamId = 0xFFFF_FFFE;
    public const byte FlagKeyframe = 0x01;

    /// <summary>
    /// Jedna klatka H.264 (Annex B). <see cref="StreamId"/> 0 = cały ekran wirtualny,
    /// inaczej okno Maca (tryb okien) albo <see cref="MenuBarStreamId"/>.
    /// </summary>
    public readonly record struct VideoFrame(byte Kind, byte Flags, int Width, int Height, ulong CaptureMicros, byte[] Payload,
        uint StreamId = 0, ushort CornerRadius = 0)
    {
        public bool IsKeyframe => (Flags & FlagKeyframe) != 0;
    }

    public static bool TryParseFrame(ReadOnlySpan<byte> clear, out VideoFrame frame)
    {
        frame = default;
        if (clear.Length == 0 || (clear[0] != KindH264AccessUnit && clear[0] != KindWindowAccessUnit)) return false;
        var header = clear[0] == KindWindowAccessUnit ? WindowFrameHeaderBytes : FrameHeaderBytes;
        if (clear.Length <= header) return false;
        var width = BinaryPrimitives.ReadUInt16LittleEndian(clear[2..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(clear[4..]);
        if (width == 0 || height == 0) return false;
        uint stream = 0;
        ushort radius = 0;
        if (clear[0] == KindWindowAccessUnit)
        {
            stream = BinaryPrimitives.ReadUInt32LittleEndian(clear[14..]);
            radius = BinaryPrimitives.ReadUInt16LittleEndian(clear[18..]);
            if (stream == 0) return false;
        }
        frame = new VideoFrame(clear[0], clear[1], width, height,
            BinaryPrimitives.ReadUInt64LittleEndian(clear[6..]), clear[header..].ToArray(), stream, radius);
        return true;
    }

    public static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        "BLMV"u8.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    /// <summary>Tworzy rekord z prefiksem długości (używane w testach i przez stronę wysyłającą).</summary>
    public static byte[] Seal(ReadOnlySpan<byte> key, ulong counter, ReadOnlySpan<byte> plaintext)
    {
        var body = CounterBytes + plaintext.Length + TagBytes;
        var record = new byte[LengthBytes + body];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)body);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(LengthBytes), counter);
        var nonce = Nonce(counter);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext,
            record.AsSpan(LengthBytes + CounterBytes, plaintext.Length),
            record.AsSpan(LengthBytes + CounterBytes + plaintext.Length, TagBytes), nonce);
        return record;
    }

    /// <summary>Odszyfrowuje kolejne rekordy. Licznik musi rosnąć dokładnie o jeden (TCP).</summary>
    public sealed class Opener : IDisposable
    {
        private readonly AesGcm _aes;
        private ulong _expected;

        public Opener(ReadOnlySpan<byte> key)
        {
            if (key.Length != KeyBytes) throw new ArgumentException("invalid video key", nameof(key));
            _aes = new AesGcm(key, TagBytes);
        }

        /// <summary><paramref name="body"/> = rekord bez prefiksu długości.</summary>
        public byte[]? Open(ReadOnlySpan<byte> body)
        {
            if (body.Length < CounterBytes + TagBytes) return null;
            var counter = BinaryPrimitives.ReadUInt64LittleEndian(body);
            if (counter != _expected) return null;
            var cipherLength = body.Length - CounterBytes - TagBytes;
            var clear = new byte[cipherLength];
            var nonce = Nonce(counter);
            try
            {
                _aes.Decrypt(nonce, body.Slice(CounterBytes, cipherLength), body[(CounterBytes + cipherLength)..], clear, nonce);
            }
            catch (CryptographicException)
            {
                return null;
            }
            _expected++;
            return clear;
        }

        public void Dispose() => _aes.Dispose();
    }
}
