using System.Windows;

namespace OverTranslate.Services;

public static class ThemeService
{
    public const string Light = "Light";
    public const string Dark  = "Dark";

    /// <summary>Light or dark as Windows' own app mode is, followed as it changes.</summary>
    public const string FollowWindows = "System";

    private static readonly Uri LightUri = new("Themes/LightTheme.xaml", UriKind.Relative);
    private static readonly Uri DarkUri  = new("Themes/DarkTheme.xaml",  UriKind.Relative);

    public static string Current => SettingsService.Instance.Current.Theme;

    /// <summary>
    /// Raised once the new dictionary is in place.
    /// </summary>
    /// <remarks>
    /// For the colours a DynamicResource cannot reach — the shell window's outer edge is drawn by
    /// the desktop compositor from a value handed over once, so nothing repaints it on its own.
    /// </remarks>
    public static event EventHandler? Changed;

    public static void Apply(string theme)
    {
        // Once, for the life of the process; it only acts while the setting says to follow.
        if (theme == FollowWindows && !_watching)
        {
            _watching = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && Current == FollowWindows)
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Apply(FollowWindows));
            };
        }

        if (theme == FollowWindows) theme = WindowsUsesLightApps() ? Light : Dark;

        var dicts = System.Windows.Application.Current.Resources.MergedDictionaries;

        var old = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (old != null) dicts.Remove(old);

        dicts.Insert(0, new ResourceDictionary
        {
            Source = theme == Dark ? DarkUri : LightUri
        });

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool _watching;

    /// <summary>Settings → Personalisation → Colours → app mode; light where it cannot be read.</summary>
    private static bool WindowsUsesLightApps()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
