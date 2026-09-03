using System.Text.Json;
using System.Text.Json.Serialization;
using BorderlessMouse.Protocol;

namespace BorderlessMouse.Models;

public enum MacSide
{
    Left,
    Right,
    Top,
    Bottom,
}

public enum EmergencyHotkey
{
    ScrollLock,
    Pause,
    F12,
}

public sealed class Settings
{
    public string HostAddress { get; set; } = string.Empty;
    public int ControlPort { get; set; } = ProtocolConstants.DefaultControlPort;
    public int AudioPort { get; set; } = ProtocolConstants.DefaultAudioPort;
    public string DeviceName { get; set; } = Environment.MachineName;
    public bool AutoConnect { get; set; } = true;

    public bool InputSharingEnabled { get; set; } = true;
    public MacSide MacSide { get; set; } = MacSide.Left;
    public EmergencyHotkey EmergencyHotkey { get; set; } = EmergencyHotkey.ScrollLock;
    public bool HideCursorWhileRemote { get; set; } = true;
    public double RemoteMouseSpeed { get; set; } = 1.0;

    public bool AudioEnabled { get; set; } = true;
    public string? AudioDeviceId { get; set; }
    public int JitterBufferMs { get; set; } = 20;
    public bool ExclusiveMode { get; set; }

    public bool ClipboardSyncEnabled { get; set; } = true;

    public bool AutoCheckUpdates { get; set; } = true;

    public bool StartMinimized { get; set; } = true;
    public bool HasCompletedOnboarding { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BorderlessMouse", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
            }
        }
        catch
        {
            // uszkodzony plik – wracamy do domyślnych
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // brak uprawnień do zapisu nie powinien wywracać aplikacji
        }
    }
}
