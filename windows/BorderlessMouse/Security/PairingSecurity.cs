using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BorderlessMouse.Security;

public static class PairingCodeCodec
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        uint accumulator = 0;
        var bits = 0;
        foreach (var value in data)
        {
            accumulator = (accumulator << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(Alphabet[(int)((accumulator >> bits) & 0x1F)]);
            }
        }
        if (bits > 0) output.Append(Alphabet[(int)((accumulator << (5 - bits)) & 0x1F)]);
        var raw = output.ToString();
        return string.Join('-', Enumerable.Range(0, (raw.Length + 4) / 5)
            .Select(i => raw.Substring(i * 5, Math.Min(5, raw.Length - i * 5))));
    }

    public static bool TryDecode(string? code, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(code)) return false;
        var normalized = new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalized.Length != 26) return false;
        var lastValue = Alphabet.IndexOf(normalized[^1]);
        if (lastValue < 0 || (lastValue & 0x03) != 0) return false;
        var output = new List<byte>();
        uint accumulator = 0;
        var bits = 0;
        foreach (var ch in normalized)
        {
            var value = Alphabet.IndexOf(ch);
            if (value < 0) return false;
            accumulator = (accumulator << 5) | (uint)value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((accumulator >> bits) & 0xFF));
            }
        }
        if (output.Count != 16) return false;
        key = output.ToArray();
        return true;
    }
}

/// <summary>Przechowuje sekret parowania zaszyfrowany dla bieżącego konta Windows (DPAPI).</summary>
public static class PairingKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BorderlessMouse/v2/pairing-key");
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BorderlessMouse", "pairing.key");

    public static byte[]? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(FilePath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(FilePath);
            var key = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            if (key.Length == 16) return key;
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
        catch { return null; }
    }

    public static bool SaveCode(string code)
    {
        if (!OperatingSystem.IsWindows() || !PairingCodeCodec.TryDecode(code, out var key)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        byte[]? protectedBytes = null;
        string? temporary = null;
        try
        {
            protectedBytes = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
            temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, FilePath, true);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

public static class ControlCrypto
{
    public const int NonceBytes = 16;
    public const int ProofBytes = 32;
    public const int TagBytes = 16;
    public const int CounterBytes = 8;

    public static byte[] RandomNonce() => RandomNumberGenerator.GetBytes(NonceBytes);

    public static byte[] Proof(ReadOnlySpan<byte> secret, string role, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        var prefix = Encoding.UTF8.GetBytes($"BorderlessMouse/v2/{role}");
        var message = new byte[prefix.Length + clientNonce.Length + serverNonce.Length];
        prefix.CopyTo(message, 0);
        clientNonce.CopyTo(message.AsSpan(prefix.Length));
        serverNonce.CopyTo(message.AsSpan(prefix.Length + clientNonce.Length));
        return HMACSHA256.HashData(secret, message);
    }

    public static byte[] Derive(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> salt, string info, int length = 32)
    {
        var prk = HMACSHA256.HashData(salt, secret);
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;
        var infoBytes = Encoding.UTF8.GetBytes(info);
        while (offset < length)
        {
            var input = new byte[previous.Length + infoBytes.Length + 1];
            previous.CopyTo(input, 0);
            infoBytes.CopyTo(input, previous.Length);
            input[^1] = counter++;
            previous = HMACSHA256.HashData(prk, input);
            var count = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, count).CopyTo(output.AsSpan(offset));
            offset += count;
        }
        CryptographicOperations.ZeroMemory(prk);
        return output;
    }
}

/// <summary>Dwukierunkowa sesja AES-256-GCM dla kompletnych ramek protokołu.</summary>
public sealed class SecureSession : IDisposable
{
    public enum SessionRole { Client, Server }

    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly object _sendGate = new();
    private readonly object _receiveGate = new();
    private ulong _sendCounter;
    private ulong? _receiveCounter;
    private bool _disposed;

    public byte[] AudioKey { get; }
    public ulong AudioSessionId { get; }

    public SecureSession(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, SessionRole role)
    {
        var salt = new byte[clientNonce.Length + serverNonce.Length];
        clientNonce.CopyTo(salt);
        serverNonce.CopyTo(salt.AsSpan(clientNonce.Length));
        var c2s = ControlCrypto.Derive(secret, salt, "BorderlessMouse/v2/control/client-to-server");
        var s2c = ControlCrypto.Derive(secret, salt, "BorderlessMouse/v2/control/server-to-client");
        (_sendKey, _receiveKey) = role == SessionRole.Client ? (c2s, s2c) : (s2c, c2s);
        AudioKey = ControlCrypto.Derive(secret, salt, "BorderlessMouse/v2/audio/server-to-client");
        var session = ControlCrypto.Derive(secret, salt, "BorderlessMouse/v2/audio/session-id", 8);
        AudioSessionId = BinaryPrimitives.ReadUInt64LittleEndian(session);
        CryptographicOperations.ZeroMemory(session);
    }

    public byte[]? Seal(ReadOnlySpan<byte> plaintext)
    {
        lock (_sendGate)
        {
            if (_disposed) return null;
            if (_sendCounter == ulong.MaxValue) return null;
            var counter = _sendCounter++;
            var output = new byte[ControlCrypto.CounterBytes + plaintext.Length + ControlCrypto.TagBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(output, counter);
            var nonce = Nonce(counter);
            var aad = Aad(output.AsSpan(0, ControlCrypto.CounterBytes));
            using var aes = new AesGcm(_sendKey, ControlCrypto.TagBytes);
            aes.Encrypt(nonce, plaintext,
                output.AsSpan(ControlCrypto.CounterBytes, plaintext.Length),
                output.AsSpan(ControlCrypto.CounterBytes + plaintext.Length, ControlCrypto.TagBytes), aad);
            return output;
        }
    }

    public byte[]? Open(ReadOnlySpan<byte> envelope)
    {
        lock (_receiveGate)
        {
            if (_disposed) return null;
            if (envelope.Length < ControlCrypto.CounterBytes + ControlCrypto.TagBytes) return null;
            var counter = BinaryPrimitives.ReadUInt64LittleEndian(envelope);
            if (_receiveCounter is { } previous && counter <= previous) return null;
            var clearLength = envelope.Length - ControlCrypto.CounterBytes - ControlCrypto.TagBytes;
            var plaintext = new byte[clearLength];
            try
            {
                using var aes = new AesGcm(_receiveKey, ControlCrypto.TagBytes);
                aes.Decrypt(Nonce(counter),
                    envelope.Slice(ControlCrypto.CounterBytes, clearLength),
                    envelope.Slice(ControlCrypto.CounterBytes + clearLength, ControlCrypto.TagBytes),
                    plaintext, Aad(envelope[..ControlCrypto.CounterBytes]));
                _receiveCounter = counter;
                return plaintext;
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                return null;
            }
        }
    }

    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12] { 0x42, 0x4C, 0x4D, 0x32, 0, 0, 0, 0, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    private static byte[] Aad(ReadOnlySpan<byte> counter)
    {
        var aad = new byte[4 + ControlCrypto.CounterBytes];
        "BLM2"u8.CopyTo(aad);
        counter.CopyTo(aad.AsSpan(4));
        return aad;
    }

    public void Dispose()
    {
        lock (_sendGate)
        lock (_receiveGate)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_sendKey);
            CryptographicOperations.ZeroMemory(_receiveKey);
            CryptographicOperations.ZeroMemory(AudioKey);
        }
    }
}
