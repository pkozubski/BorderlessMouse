using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using BorderlessMouse.Input;
using static BorderlessMouse.Localization.L10n;

namespace BorderlessMouse.Display;

/// <summary>
/// Wirtualny monitor Windows ze sterownika Virtual Display Driver (MIT, podpisany,
/// github.com/VirtualDrivers/Virtual-Display-Driver). Okna przeciągnięte na ten monitor
/// pokazuje Mac. Sterownik instaluje się raz (z uprawnieniami administratora); potem
/// aplikacja tylko podłącza monitor do pulpitu na czas sesji i odłącza go po niej –
/// to nie wymaga uprawnień administratora.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VirtualDisplayDriver
{
    public const string ConfigDirectory = @"C:\VirtualDisplayDriver";
    private const string PipeName = "MTTVirtualDisplayPipe";
    private const string DriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip";
    private const string DriverSha256 = "E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A";
    private const string NefconUrl = "https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip";
    private const string NefconSha256 = "A15557DA24A9EFCA203158DE3B43B0EAF982DB231F0194031F1ED428BC13E669";

    /// <summary>Rozmiary ekranów MacBooków w punktach (domyślne i popularne skalowania) oraz monitory zewnętrzne.</summary>
    private static readonly (int Width, int Height)[] DefaultModes =
    [
        (1280, 800), (1440, 900), (1680, 1050), (1280, 832), (1470, 956), (1710, 1112), (1440, 932), (1710, 1107),
        (1352, 878), (1512, 982), (1800, 1169), (1496, 967), (1728, 1117), (2056, 1329), (1920, 1200),
        (1920, 1080), (2560, 1440), (2560, 1600),
    ];

    /// <summary>Monitor sterownika: nazwa GDI (\\.\DISPLAYn), czy jest na pulpicie i gdzie.</summary>
    public readonly record struct Monitor(string DeviceName, bool Attached, NativeMethods.RECT Bounds);

    /// <summary>Monitor sterownika albo null, gdy sterownik nie jest zainstalowany.</summary>
    public static Monitor? Find()
    {
        var device = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
        for (uint index = 0; EnumDisplayDevicesW(null, index, ref device, 0); index++)
        {
            var name = device.DeviceName;
            var isDriver = device.DeviceString.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase)
                           || device.DeviceID.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
                           || MonitorName(name).Contains("VDD by MTT", StringComparison.OrdinalIgnoreCase);
            var attached = (device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            device = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!isDriver) continue;
            var bounds = default(NativeMethods.RECT);
            var mode = NewMode();
            if (attached && EnumDisplaySettingsW(name, ENUM_CURRENT_SETTINGS, ref mode))
            {
                bounds = new NativeMethods.RECT
                {
                    Left = mode.dmPositionX, Top = mode.dmPositionY,
                    Right = mode.dmPositionX + (int)mode.dmPelsWidth, Bottom = mode.dmPositionY + (int)mode.dmPelsHeight,
                };
            }
            return new Monitor(name, attached && bounds.Width > 0, bounds);
        }
        return null;
    }

    public static bool IsInstalled => Find() is not null;

    private static string MonitorName(string adapter)
    {
        var monitor = new DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICEW>() };
        return EnumDisplayDevicesW(adapter, 0, ref monitor, 0) ? monitor.DeviceString : "";
    }

    /// <summary>Rozdzielczości oferowane przez monitor sterownika (60 Hz lub dowolne).</summary>
    public static List<(int Width, int Height)> Modes(string deviceName)
    {
        var modes = new HashSet<(int, int)>();
        var mode = NewMode();
        for (var index = 0; EnumDisplaySettingsW(deviceName, index, ref mode); index++)
        {
            modes.Add(((int)mode.dmPelsWidth, (int)mode.dmPelsHeight));
            mode = NewMode();
        }
        return modes.ToList();
    }

    /// <summary>
    /// Podłącza monitor do pulpitu z rozdzielczością <paramref name="width"/>×<paramref name="height"/>
    /// w punkcie (<paramref name="x"/>, <paramref name="y"/>) wirtualnego pulpitu.
    /// </summary>
    public static void Attach(string deviceName, int width, int height, int x, int y)
    {
        var mode = NewMode();
        mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        mode.dmPositionX = x;
        mode.dmPositionY = y;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        Apply(deviceName, ref mode);
    }

    /// <summary>Odłącza monitor od pulpitu (okna z niego wracają na pozostałe monitory).</summary>
    public static void Detach(string deviceName)
    {
        var mode = NewMode();
        mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        Apply(deviceName, ref mode);
    }

    private static void Apply(string deviceName, ref DEVMODEW mode)
    {
        var result = ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (result != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException(T($"Windows odrzucił ustawienia monitora wirtualnego (kod {result}).",
                $"Windows rejected the virtual monitor settings (code {result})."));
        result = ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (result != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException(T($"Nie udało się zastosować układu monitorów (kod {result}).",
                $"Could not apply the monitor layout (code {result})."));
    }

    /// <summary>
    /// Dopisuje rozdzielczość do konfiguracji sterownika i przeładowuje go (bez uprawnień
    /// administratora – instalator nadaje bieżącemu użytkownikowi prawo zapisu do katalogu).
    /// </summary>
    public static bool TryAddMode(int width, int height)
    {
        var path = Path.Combine(ConfigDirectory, "vdd_settings.xml");
        try
        {
            var xml = File.ReadAllText(path);
            var index = xml.IndexOf("</resolutions>", StringComparison.Ordinal);
            if (index < 0) return false;
            if (!Regex.IsMatch(xml, $@"<width>\s*{width}\s*</width>\s*<height>\s*{height}\s*</height>"))
            {
                xml = xml.Insert(index, Resolution(width, height) + "    ");
                File.WriteAllText(path, xml);
            }
            return Send("RELOAD_DRIVER");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Wysyła polecenie do potoku sterownika (UTF-16, jak w VDD Control).</summary>
    private static bool Send(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(1500);
            var bytes = Encoding.Unicode.GetBytes(command);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------- instalacja ----------------

    /// <summary>
    /// Pobiera sterownik i narzędzie nefcon (sumy SHA-256 przypięte w kodzie), zapisuje
    /// konfigurację i instaluje sterownik. Uruchamia PowerShell jako administrator (UAC).
    /// </summary>
    public static async Task InstallAsync()
    {
        var log = Path.Combine(Path.GetTempPath(), "BorderlessMouse-vdd-install.log");
        try { File.Delete(log); } catch (IOException) { }
        var user = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var script = InstallScript(log, user);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var info = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(T("Instalację anulowano w oknie Kontroli konta użytkownika.",
                "The installation was cancelled in the User Account Control prompt."));
        }
        if (process is null) throw new InvalidOperationException(T("Nie udało się uruchomić instalatora.", "Could not start the installer."));
        using (process)
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var details = File.Exists(log) ? File.ReadAllText(log).Trim() : "";
                throw new InvalidOperationException(T("Instalacja sterownika nie powiodła się", "Driver installation failed")
                    + (details.Length > 0 ? ": " + details : $" (kod {process.ExitCode})."));
            }
        }
        // Sterownik pojawia się jako nowy monitor dopiero po chwili.
        for (var i = 0; i < 40 && !IsInstalled; i++) await Task.Delay(250);
        if (!IsInstalled)
            throw new InvalidOperationException(T("Sterownik zainstalowano, ale monitor wirtualny się nie pojawił. Uruchom ponownie komputer.",
                "The driver was installed but the virtual monitor did not appear. Restart the computer."));
    }

    private static string InstallScript(string logPath, string userSid)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var resolutions = string.Concat(DefaultModes.Select(m => Resolution(m.Width, m.Height)));
        var settings = $"""
            <?xml version='1.0' encoding='utf-8'?>
            <vdd_settings>
                <monitors>
                    <count>1</count>
                </monitors>
                <gpu>
                    <friendlyname>default</friendlyname>
                </gpu>
                <global>
                    <g_refresh_rate>60</g_refresh_rate>
                </global>
                <resolutions>
            {resolutions}    </resolutions>
                <options>
                    <CustomEdid>false</CustomEdid>
                    <PreventSpoof>false</PreventSpoof>
                    <EdidCeaOverride>false</EdidCeaOverride>
                    <HardwareCursor>true</HardwareCursor>
                    <SDR10bit>false</SDR10bit>
                    <HDRPlus>false</HDRPlus>
                    <logging>false</logging>
                    <debuglogging>false</debuglogging>
                </options>
            </vdd_settings>
            """;
        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $log = {{Quote(logPath)}}
            $work = Join-Path $env:TEMP ('blm-vdd-' + [guid]::NewGuid().ToString('N'))
            try {
                New-Item -ItemType Directory -Path $work | Out-Null
                function Get-Verified([string]$url, [string]$sha, [string]$name) {
                    $file = Join-Path $work $name
                    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $file
                    $hash = (Get-FileHash -Algorithm SHA256 -Path $file).Hash
                    if ($hash -ne $sha) { throw "Nieprawidłowa suma kontrolna pliku $name" }
                    Expand-Archive -Path $file -DestinationPath $work -Force
                }
                Get-Verified {{Quote(DriverUrl)}} {{Quote(DriverSha256)}} 'driver.zip'
                Get-Verified {{Quote(NefconUrl)}} {{Quote(NefconSha256)}} 'nefcon.zip'

                $conf = {{Quote(ConfigDirectory)}}
                New-Item -ItemType Directory -Force -Path $conf | Out-Null
                $settingsPath = Join-Path $conf 'vdd_settings.xml'
                if (Test-Path $settingsPath) { Copy-Item $settingsPath ($settingsPath + '.bak') -Force }
                [IO.File]::WriteAllText($settingsPath, @'
            {{settings}}
            '@)
                if ({{Quote(userSid)}} -ne '') { & icacls.exe $conf /grant ('*' + {{Quote(userSid)}} + ':(OI)(CI)M') | Out-Null }

                $certificates = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
                $certificates.Import([IO.File]::ReadAllBytes((Join-Path $work 'VirtualDisplayDriver\mttvdd.cat')))
                $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPublisher', 'LocalMachine')
                $store.Open('ReadWrite')
                foreach ($certificate in $certificates) { $store.Add($certificate) }
                $store.Close()

                $nefcon = Join-Path $work 'x64\nefconw.exe'
                $inf = Join-Path $work 'VirtualDisplayDriver\MttVDD.inf'
                $process = Start-Process -FilePath $nefcon -ArgumentList @('install', ('"' + $inf + '"'), 'Root\MttVDD') -Wait -PassThru
                if ($process.ExitCode -ne 0) { throw "nefcon zakończył się kodem $($process.ExitCode)" }
                exit 0
            } catch {
                try { Set-Content -Path $log -Value $_.Exception.Message -Encoding UTF8 } catch { }
                exit 1
            } finally {
                Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
            }
            """;
    }

    private static string Resolution(int width, int height) => $"""
                <resolution>
                    <width>{width}</width>
                    <height>{height}</height>
                    <refresh_rate>60</refresh_rate>
                </resolution>

        """;

    // ---------------- Win32 ----------------

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_POSITION = 0x00000020;
    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_NORESET = 0x10000000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    private static DEVMODEW NewMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>(), dmDeviceName = "", dmFormName = "" };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>DEVMODEW w wariancie dla monitorów (220 bajtów).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICEW displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODEW devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref DEVMODEW devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr param);
}
