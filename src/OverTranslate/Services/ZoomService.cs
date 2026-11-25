using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using OverTranslate.Views.QuickLookup;
using OverTranslate.Views.Shell;

namespace OverTranslate.Services;

/// <summary>
/// Interface zoom and the size of the text in the translation boxes, swapped as application
/// resources the way <see cref="ThemeService"/> swaps colours: <c>UiZoom</c> is the LayoutTransform
/// the main window's body and Quick Lookup carry, <c>TextBoxFontSize</c> what the translation boxes
/// are set in. Anything using them through DynamicResource follows a change at once.
/// </summary>
/// <remarks>
/// Windows that scale themselves to the monitor they sit on — the capture toolbar and drawing panel,
/// the realtime capsule and its panels — multiply that scale by <see cref="Factor"/> instead. The
/// translation overlays and realtime subtitles are not zoomed: they are sized to the text they cover,
/// and have text settings of their own.
/// </remarks>
public static class ZoomService
{
    public static readonly double[] Zooms = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];
    public static readonly double[] TextSizes = [12, 14, 16, 18, 20, 24, 28, 32];

    /// <summary>The text size the translation boxes were designed at; Quick Lookup scales by the ratio to it.</summary>
    public const double DesignTextSize = 14;

    /// <summary>Raised after a change, for the settings page to show the new values.</summary>
    public static event EventHandler? Changed;

    /// <summary>The interface zoom in effect, for windows that apply it in code.</summary>
    public static double Factor => Zoom(SettingsService.Instance.Current.UiZoom);

    public static double TextScale => TextSize(SettingsService.Instance.Current.TextBoxFontSize) / DesignTextSize;

    // The settings file is hand-editable; a zoom of 0 or a 500 pt font is not one anyone can recover from in the UI.
    private static double Zoom(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.5, 3) : 1;
    private static double TextSize(double value) => double.IsFinite(value) ? Math.Clamp(value, 8, 72) : DesignTextSize;

    public static void Apply()
    {
        var settings = SettingsService.Instance.Current;
        var zoom = Zoom(settings.UiZoom);
        var scale = new ScaleTransform(zoom, zoom);
        scale.Freeze();   // one instance shared by every window that uses it

        var resources = System.Windows.Application.Current.Resources;
        resources["UiZoom"] = scale;
        resources["TextBoxFontSize"] = TextSize(settings.TextBoxFontSize);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Set(double? zoom = null, double? textSize = null)
    {
        var settings = SettingsService.Instance.Current;
        if (zoom is { } z) settings.UiZoom = z;
        if (textSize is { } t) settings.TextBoxFontSize = t;
        SettingsService.Instance.Save();
        Apply();
    }

    /// <summary>The next of <paramref name="steps"/> from the one nearest <paramref name="current"/>.</summary>
    public static double Step(double[] steps, double current, int direction)
    {
        var nearest = 0;
        for (var i = 1; i < steps.Length; i++)
            if (Math.Abs(steps[i] - current) < Math.Abs(steps[nearest] - current)) nearest = i;
        return steps[Math.Clamp(nearest + direction, 0, steps.Length - 1)];
    }

    /// <summary>
    /// Ctrl + Plus / Minus / 0 and Ctrl + wheel zoom the main window and Quick Lookup; with Shift
    /// added, the keys change the text size instead. Called once at startup.
    /// </summary>
    public static void RegisterShortcuts()
    {
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
    }

    /// <summary>Set while Settings records a shortcut, which may well be one of these keys.</summary>
    public static bool Suspended { get; set; }

    private static bool Zoomable(object sender) => !Suspended && sender is ShellWindow or QuickLookupWindow;

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!Zoomable(sender) || (Keyboard.Modifiers & ~ModifierKeys.Shift) != ModifierKeys.Control) return;

        int? direction = e.Key switch
        {
            Key.OemPlus or Key.Add => 1,
            Key.OemMinus or Key.Subtract => -1,
            Key.D0 or Key.NumPad0 => 0,
            _ => null,
        };
        if (direction is not { } d) return;

        var settings = SettingsService.Instance.Current;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            Set(textSize: d == 0 ? DesignTextSize : Step(TextSizes, settings.TextBoxFontSize, d));
        else
            Set(zoom: d == 0 ? 1 : Step(Zooms, settings.UiZoom, d));
        e.Handled = true;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Zoomable(sender) || Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0) return;
        Set(zoom: Step(Zooms, SettingsService.Instance.Current.UiZoom, Math.Sign(e.Delta)));
        e.Handled = true;
    }
}
