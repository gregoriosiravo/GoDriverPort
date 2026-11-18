using System.Windows;
using Microsoft.Win32;

namespace LegionGoConverter.App;

/// <summary>Follows the Windows app theme (HKCU ... Personalize\AppsUseLightTheme) by swapping the theme dictionary at
/// index 0 of the application resources. A forced theme (debug demo) stops following the system.</summary>
public static class ThemeManager
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static bool _following;

    public static bool IsDark { get; private set; } = true;
    public static event Action? Changed;

    /// <summary>True when Windows says apps should be dark. Missing value or any read failure means light (the Windows default).</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void FollowSystem()
    {
        Apply(SystemPrefersDark());
        if (_following) return;
        _following = true;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    public static void Force(bool dark)
    {
        StopFollowing();
        Apply(dark);
    }

    public static void StopFollowing()
    {
        if (!_following) return;
        _following = false;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }

    static void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() => { if (_following) Apply(SystemPrefersDark()); });
    }

    public static void Apply(bool dark)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        var theme = new ResourceDictionary { Source = new Uri($"/LegionGoDriverPort;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var merged = resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = theme; else merged.Add(theme);
        IsDark = dark;
        Changed?.Invoke();
    }
}
