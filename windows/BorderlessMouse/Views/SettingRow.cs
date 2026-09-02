using Avalonia;
using Avalonia.Controls;

namespace BorderlessMouse.Views;

/// <summary>Wiersz ustawienia (tytuł + opis po lewej, kontrolka po prawej) – stylowany w Theme.axaml.</summary>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Subtitle));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }
}
