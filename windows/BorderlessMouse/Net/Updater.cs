using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BorderlessMouse.Security;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Net;

public sealed record ReleaseInfo(string Version, string Tag, string Notes, string PageUrl, string AssetUrl, string ChecksumsUrl);

/// <summary>
/// Auto-updater oparty o GitHub Releases: pobiera BorderlessMouse-Windows-x64.exe,
/// weryfikuje SHA-256 (SHA256SUMS.txt) i podmienia bieżący plik exe skryptem
/// uruchamianym po zamknięciu aplikacji.
/// </summary>
public sealed class Updater
{
    public const string Owner = "pkozubski";
    public const string Repo = "BorderlessMouse";
    public const string AssetName = "BorderlessMouse-Windows-x64.exe";
    public const string ChecksumsName = "SHA256SUMS.txt";
    private const long MaximumDownloadBytes = 300L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BorderlessMouse-Windows", CurrentVersion));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => v.TrimStart('v').Split('.', '-').Take(3)
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var a = Parts(candidate);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    private static string PublisherCertificateSha256 =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "WindowsPublisherCertificateSha256")?.Value ?? "";

    /// <summary>Zwraca najnowsze wydanie z zasobem dla Windows albo null, gdy brak wydań.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.GetProperty("html_url").GetString() ?? "";
        string? asset = null, checksums = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name == AssetName) asset = url;
            if (name == ChecksumsName) checksums = url;
        }
        if (asset is null) return null;
        if (checksums is null) throw new InvalidDataException(T("Wydanie nie zawiera wymaganego pliku SHA256SUMS.txt.", "The release does not include the required SHA256SUMS.txt file."));
        return new ReleaseInfo(tag.TrimStart('v'), tag, notes, page, asset, checksums);
    }

    /// <summary>Pobiera, weryfikuje i przygotowuje podmianę exe. Po powrocie należy zakończyć aplikację.</summary>
    public async Task DownloadAndInstallAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(T("Aktualizacja w miejscu działa tylko na Windows.", "In-place updates are available only on Windows."));
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(T("Nie można ustalić ścieżki pliku exe.", "Cannot determine the executable path."));

        var dir = Path.Combine(Path.GetTempPath(), "BorderlessMouse-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var newExe = Path.Combine(dir, "BorderlessMouse.new.exe");

        var checksumsText = await Http.GetStringAsync(release.ChecksumsUrl, ct);
        var checksumEntry = checksumsText.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == AssetName);
        if (checksumEntry is null || checksumEntry[0].Length != 64 || !checksumEntry[0].All(Uri.IsHexDigit))
            throw new InvalidDataException(T("Wydanie nie zawiera prawidłowej sumy SHA-256 dla aplikacji Windows.", "The release does not contain a valid SHA-256 checksum for the Windows app."));
        var expectedChecksum = checksumEntry[0].ToLowerInvariant();

        using (var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;
            if (total > MaximumDownloadBytes) throw new InvalidDataException(T("Plik aktualizacji przekracza limit 300 MiB.", "The update exceeds the 300 MiB limit."));
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (read > MaximumDownloadBytes) throw new InvalidDataException(T("Plik aktualizacji przekracza limit 300 MiB.", "The update exceeds the 300 MiB limit."));
                if (total > 0) progress.Report((double)read / total);
            }
        }
        progress.Report(1);

        await using (var stream = File.OpenRead(newExe))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expectedChecksum)))
                throw new InvalidOperationException(T("Suma SHA-256 pobranego pliku nie zgadza się z wydaniem.", "The downloaded file does not match the release SHA-256 checksum."));
        }
        AuthenticodeVerifier.Verify(newExe, PublisherCertificateSha256);

        var script = Path.Combine(dir, "swap.cmd");
        File.WriteAllText(script, """
            @echo off
            setlocal
            set "PID=%~1"
            set "NEW=%~2"
            set "TARGET=%~3"
            set /a tries=0
            :wait
            tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )
            set "OLD=%TARGET%.previous"
            del /q "%OLD%" >nul 2>&1
            move /y "%TARGET%" "%OLD%" >nul 2>&1
            if errorlevel 1 exit /b 1
            :install
            move /y "%NEW%" "%TARGET%" >nul 2>&1
            if errorlevel 1 (
                set /a tries+=1
                if %tries% geq 30 (
                    move /y "%OLD%" "%TARGET%" >nul 2>&1
                    exit /b 1
                )
                timeout /t 1 /nobreak >nul
                goto install
            )
            start "" "%TARGET%"
            if errorlevel 1 (
                del /q "%TARGET%" >nul 2>&1
                move /y "%OLD%" "%TARGET%" >nul 2>&1
                exit /b 1
            )
            del /q "%OLD%" >nul 2>&1
            del "%~f0"
            """);
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(newExe);
        psi.ArgumentList.Add(target);
        Process.Start(psi);
    }
}
