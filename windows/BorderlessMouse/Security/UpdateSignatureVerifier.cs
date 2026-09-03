using System.Security.Cryptography;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Security;

/// <summary>
/// Verifies a detached ECDSA P-256 signature made by the project's offline update key.
/// This authenticates beta artifacts independently of Authenticode trust.
/// </summary>
internal static class UpdateSignatureVerifier
{
    private const string PublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6zFwLcIIcCm9BIXcE0VpRy60bKUb5Hc7sgfHofCWP4sSHKr5O/2C97+DmmIW8RTyrFtGHDuxZWsacBOcAaq/Cw==";

    public static void VerifyHash(ReadOnlySpan<byte> sha256, ReadOnlySpan<byte> signature)
    {
        if (sha256.Length != 32 || signature.Length is < 64 or > 80 || !IsValid(sha256, signature))
            throw new InvalidDataException(T(
                "Podpis kryptograficzny pliku aktualizacji jest nieprawidłowy. Dotychczasowa aplikacja pozostaje bez zmian.",
                "The update artifact signature is invalid. The installed app was not changed."));
    }

    internal static bool VerifyData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        var hash = SHA256.HashData(data);
        return signature.Length is >= 64 and <= 80 && IsValid(hash, signature);
    }

    private static bool IsValid(ReadOnlySpan<byte> sha256, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeySpkiBase64), out var read);
            if (read == 0) return false;
            return verifier.VerifyHash(sha256, signature, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
