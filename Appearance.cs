using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DnsPanel;

internal enum ThemeChoice { System, Light, Dark }

internal static class Appearance
{
    private const string Preferences = @"Software\DnsPanelWindows";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly (string Key, int Light, int Dark)[] Colors =
    [
        ("Canvas", 0xF5F7F6, 0x111314),
        ("Surface", 0xFFFFFF, 0x1B1E1F),
        ("Ink", 0x1B3028, 0xF0F4F2),
        ("Muted", 0x50645B, 0xACBAB4),
        ("Line", 0xDCE6E0, 0x343D39),
        ("Accent", 0x9BF0CA, 0x9BF0CA),
        ("AccentInk", 0x102C22, 0x102C22),
        ("Focus", 0x328B69, 0x70D6AB),
        ("Hover", 0xEFF6F2, 0x272E2B),
        ("Selection", 0xE0F2E9, 0x223C32),
        ("SelectionLine", 0xBEE4D3, 0x467761),
        ("ScrollThumb", 0xB8CBC1, 0x586C62),
        ("Positive", 0x146B4E, 0xA0ECC8),
        ("PositiveFill", 0xE6F6EE, 0x244438),
        ("Caution", 0x815316, 0xF7D396),
        ("CautionFill", 0xFFF2D7, 0x453821),
        ("Negative", 0x9C3F3C, 0xFFB3AC),
        ("NegativeFill", 0xFAECEA, 0x492F30),
        ("Waiting", 0x52675E, 0xB8C5BE),
        ("WaitingFill", 0xEDF2EF, 0x303936),
        ("IconFill", 0xECF5F0, 0x293D34),
        ("HeroInk", 0xF1FFF9, 0xF1FFF9),
        ("HeroMuted", 0xC7E4D7, 0xC4DBD1),
        ("HeroButtonFill", 0x204C43, 0x253E36),
        ("HeroButtonLine", 0x53766B, 0x59796D),
        ("HeroTrace", 0x9CF2CC, 0x90DAB8)
    ];

    public static ThemeChoice ReadChoice()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Preferences);
        return key?.GetValue("Theme") switch
        {
            nameof(ThemeChoice.Light) => ThemeChoice.Light,
            nameof(ThemeChoice.Dark) => ThemeChoice.Dark,
            _ => ThemeChoice.System
        };
    }

    public static bool TrySave(ThemeChoice choice)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Preferences);
            key.SetValue("Theme", choice.ToString(), RegistryValueKind.String);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or SecurityException)
        {
            return false;
        }
    }

    public static bool IsDark(ThemeChoice choice)
    {
        if (choice != ThemeChoice.System)
            return choice == ThemeChoice.Dark;
        using var key = Registry.CurrentUser.OpenSubKey(Personalize);
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    public static void SetColors(ResourceDictionary resources, bool dark)
    {
        foreach (var (key, light, darkColor) in Colors)
        {
            var brush = new SolidColorBrush(Rgb(dark ? darkColor : light));
            brush.Freeze();
            resources[key] = brush;
        }
        var gradient = new LinearGradientBrush(
            Rgb(dark ? 0x162523 : 0x122E2A), Rgb(dark ? 0x234239 : 0x1B493E),
            new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        resources["HeroSurface"] = gradient;
    }

    private static Color Rgb(int value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
