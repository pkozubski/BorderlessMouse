using System.Globalization;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using BorderlessMouse.Views;
using FluentAvalonia.UI.Controls;

namespace BorderlessMouse.Localization;

/// <summary>
/// Lekka lokalizacja pierwszej bety. Polski pozostaje językiem źródłowym,
/// a każdy inny język systemu otrzymuje angielski. Dynamiczne komunikaty
/// powinny używać <see cref="T"/> bezpośrednio.
/// </summary>
public static class L10n
{
    public static bool IsPolish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pl", StringComparison.OrdinalIgnoreCase);
    public static string T(string polish, string english) => IsPolish ? polish : english;
    public static string ShowWindow => T("Pokaż okno", "Show window");
    public static string Quit => T("Zakończ", "Quit");
    public static string TranslateText(string value) => !IsPolish && English.TryGetValue(value, out var translated) ? translated : value;

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["Połączenie"] = "Connection",
        ["Połączenie z Makiem"] = "Connect to Mac",
        ["Sterowanie"] = "Control",
        ["Ustawienia"] = "Settings",
        ["Dziennik"] = "Diagnostics",
        ["Wyczyść"] = "Clear",
        ["Pokaż okno"] = "Show window",
        ["Zakończ"] = "Quit",
        ["Połącz swoje biurko w trzech krokach"] = "Connect your desk in three steps",
        ["1. Uruchom aplikację na Macu.  2. Wpisz kod parowania poniżej.  3. Wybierz znalezionego Maca i połącz."] = "1. Open the app on your Mac.  2. Enter the pairing code below.  3. Select the discovered Mac and connect.",
        ["Rozumiem"] = "Got it",
        ["BEZPIECZNE PAROWANIE"] = "SECURE PAIRING",
        ["Przepisz kod widoczny w sekcji Połączenie na Macu. Kod nie jest zapisywany w zwykłych ustawieniach."] = "Enter the code shown on the Mac Connection page. The code is not stored in ordinary settings.",
        ["Zapisz kod"] = "Save code",
        ["Ten komputer jest sparowany"] = "This PC is paired",
        ["Chronione"] = "Protected",
        ["Usuń zaufanie"] = "Forget",
        ["MAKI W SIECI LOKALNEJ"] = "MACS ON YOUR LOCAL NETWORK",
        ["Na Macu musi działać BorderlessMouse. Kliknij, aby wybrać adres."] = "BorderlessMouse must be running on the Mac. Select it below.",
        ["Nie znaleziono jeszcze żadnego Maca – wpisz adres ręcznie poniżej."] = "No Mac found yet — enter its address manually below.",
        ["Adres IP Maca, np. 192.168.1.20"] = "Mac IP address, e.g. 192.168.1.20",
        ["Łącz automatycznie"] = "Connect automatically",
        ["Przy starcie i po utracie połączenia próbuj ponownie."] = "Reconnect at startup and after a connection loss.",
        ["STAN"] = "STATUS",
        ["Kursor"] = "Pointer",
        ["KLAWIATURA I MYSZ"] = "KEYBOARD AND MOUSE",
        ["Udostępniaj klawiaturę i mysz"] = "Share keyboard and mouse",
        ["Krawędź przełącza na Maca. Pełny ekran i przechwycona mysz blokują automatyczne przejście."] = "Cross the selected edge to control the Mac. Full-screen apps and captured pointer input block automatic switching.",
        ["Mac stoi względem tego ekranu"] = "Mac position relative to this display",
        ["Krawędź, przez którą kursor przechodzi na Maca."] = "The edge used to move the pointer to the Mac.",
        ["Awaryjny skrót klawiszowy"] = "Emergency shortcut",
        ["Czułość myszy na Macu"] = "Pointer speed on Mac",
        ["Ukrywaj kursor podczas sterowania Makiem"] = "Hide the Windows pointer while controlling Mac",
        ["Kursor zostaje przy krawędzi, przez którą wyszedł, i znika pod niewidocznym oknem."] = "The Windows pointer stays at the exit edge and is hidden.",
        ["DŹWIĘK"] = "AUDIO",
        ["Odbieraj dźwięk z Maca"] = "Play audio from Mac",
        ["Cały dźwięk systemowy Maca gra na wybranym urządzeniu przez szyfrowany strumień LAN."] = "Play all Mac system audio on the selected output using encrypted LAN streaming.",
        ["Urządzenie wyjściowe"] = "Output device",
        ["Słuchawki lub głośniki podłączone do Windowsa."] = "Headphones or speakers connected to Windows.",
        ["Odśwież listę urządzeń"] = "Refresh devices",
        ["Bufor sieciowy"] = "Network buffer",
        ["Tryb WASAPI exclusive"] = "WASAPI exclusive mode",
        ["Najniższe opóźnienie, ale blokuje kartę dla innych aplikacji. Przy braku wsparcia wraca do shared."] = "Lowest latency, but reserves the device. Falls back to shared mode when unavailable.",
        ["STAN STRUMIENIA"] = "STREAM STATUS",
        ["SCHOWEK"] = "CLIPBOARD",
        ["Synchronizuj schowek (tekst i zdjęcia)"] = "Sync clipboard text and images",
        ["Kopiuj i wklejaj tekst, zdjęcia oraz zrzuty ekranu w obie strony. Obrazy do 32 MiB."] = "Copy and paste text, images and screenshots in either direction. Images up to 32 MiB.",
        ["Ostatnia synchronizacja"] = "Last synchronization",
        ["OGÓLNE"] = "GENERAL",
        ["Nazwa tego komputera"] = "This PC name",
        ["Widoczna w aplikacji na Macu."] = "Shown in the Mac app.",
        ["Port TCP Maca"] = "Mac TCP port",
        ["Domyślnie 47800 – musi zgadzać się z ustawieniem na Macu."] = "Default 47800 — must match the Mac setting.",
        ["Port UDP audio (ten komputer)"] = "Audio UDP port on this PC",
        ["Domyślnie 47802. Zapora Windows musi go przepuszczać."] = "Default 47802. Windows Firewall must allow it.",
        ["URUCHAMIANIE"] = "STARTUP",
        ["Uruchamiaj przy logowaniu"] = "Launch at sign-in",
        ["Startuj zminimalizowany do zasobnika"] = "Start minimized to tray",
        ["Przy autostarcie okno pozostaje ukryte; kliknij ikonę w zasobniku, aby je otworzyć."] = "Keep the window hidden at startup; use the tray icon to open it.",
        ["AKTUALIZACJE"] = "UPDATES",
        ["Sprawdź teraz"] = "Check now",
        ["Zainstaluj"] = "Install",
        ["Zainstaluj i uruchom ponownie"] = "Install and restart",
        ["Sprawdzaj automatycznie"] = "Check automatically",
        ["Po starcie i co 6 godzin, przez GitHub Releases."] = "At startup and every six hours through GitHub Releases.",
        ["Dostępna aktualizacja"] = "Update available",
        ["Mac nie ma uprawnienia Dostępność"] = "Mac accessibility permission is missing",
        ["Sterowanie nie zadziała, dopóki na Macu nie nadasz BorderlessMouse uprawnienia w Ustawieniach systemowych (Prywatność i ochrona, sekcja Dostępność)."] = "Control will not work until BorderlessMouse has Accessibility permission in Mac System Settings (Privacy & Security, Accessibility).",
    };

    public static void Apply(Control root)
    {
        if (IsPolish) return;
        Translate(root);
        foreach (var control in root.GetLogicalDescendants().OfType<Control>()) Translate(control);
    }

    private static void Translate(Control control)
    {
        if (control is TextBlock text && text.Text is { } value && English.TryGetValue(value, out var translatedText))
            text.Text = translatedText;
        if (control is SettingRow row)
        {
            if (English.TryGetValue(row.Title ?? "", out var title)) row.Title = title;
            if (English.TryGetValue(row.Subtitle ?? "", out var subtitle)) row.Subtitle = subtitle;
        }
        if (control is ContentControl content && content.Content is string label && English.TryGetValue(label, out var translatedContent))
            content.Content = translatedContent;
        if (control is TextBox box && box.Watermark is string watermark && English.TryGetValue(watermark, out var translatedWatermark))
            box.Watermark = translatedWatermark;
        if (control is InfoBar info)
        {
            if (English.TryGetValue(info.Title ?? "", out var title)) info.Title = title;
            if (English.TryGetValue(info.Message ?? "", out var message)) info.Message = message;
        }
        if (ToolTip.GetTip(control) is string tip && English.TryGetValue(tip, out var translatedTip))
            ToolTip.SetTip(control, translatedTip);
    }
}
