using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Security;

internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static void Verify(string path, string expectedCertificateSha256)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var expected = Normalize(expectedCertificateSha256);
        if (expected.Length != 64)
            throw new InvalidOperationException(T("Wydanie aplikacji nie ma skonfigurowanego odcisku certyfikatu Windows.", "This app build has no configured Windows publisher certificate fingerprint."));

        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,       // WTD_UI_NONE
                UnionChoice = 1,    // WTD_CHOICE_FILE
                FileInfo = fileInfoPointer,
                StateAction = 0,    // WTD_STATEACTION_IGNORE
                UiContext = 0,
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            if (result != 0)
                throw new InvalidOperationException(T($"Podpis Authenticode aktualizacji jest nieprawidłowy (0x{result:X8}).", $"The update Authenticode signature is invalid (0x{result:X8})."));

            using var signer = X509Certificate.CreateFromSignedFile(path);
            var actual = Convert.ToHexString(SHA256.HashData(signer.GetRawCertData()));
            var expectedBytes = Convert.FromHexString(expected);
            var actualBytes = Convert.FromHexString(actual);
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
                throw new InvalidOperationException(T("Aktualizacja została podpisana przez innego wydawcę.", "The update was signed by a different publisher."));
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    private static string Normalize(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }
}
