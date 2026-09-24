namespace BorderlessMouse;

/// <summary>Zapis awarii do %LOCALAPPDATA%\BorderlessMouse\crash.log (odczytywany przy następnym starcie).</summary>
internal static class CrashLog
{
    private static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BorderlessMouse");
    private static string PathName => Path.Combine(Directory, "crash.log");

    public static void Write(Exception? exception)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.ProcessPath}\n{exception}\n\n");
        }
        catch
        {
            // zapis awarii nie może spowodować kolejnej
        }
    }

    /// <summary>Treść awarii z poprzedniego uruchomienia (plik jest potem archiwizowany) albo null.</summary>
    public static string? TakePrevious()
    {
        try
        {
            if (!File.Exists(PathName)) return null;
            var text = File.ReadAllText(PathName).Trim();
            File.Move(PathName, Path.Combine(Directory, "crash-previous.log"), overwrite: true);
            return text.Length == 0 ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
