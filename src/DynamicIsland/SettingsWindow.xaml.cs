using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DynamicIsland;

/// <summary>
/// Every option in one place. Switches apply at once: each one's Tag names the
/// <see cref="AppSettings"/> property it flips, and <see cref="Changed"/> tells the island which.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<string> _calendarStatus;
    private bool _loading = true;

    internal SettingsWindow(AppSettings settings, Func<string> calendarStatus)
    {
        _settings = settings;
        _calendarStatus = calendarStatus;
        InitializeComponent();

        foreach (var box in Switches(this))
        {
            if (Property(box) is not { } property) continue;
            box.IsChecked = (bool)property.GetValue(_settings)!;
            box.Checked += Switch_Changed;
            box.Unchecked += Switch_Changed;
        }
        StyleIsland.IsChecked = _settings.Style == IslandStyle.Island;
        StyleNotch.IsChecked = _settings.Style == IslandStyle.Notch;
        CityBox.Text = _settings.WeatherCity;
        StartupSwitch.IsChecked = AppSettings.StartWithWindows;
        RefreshCalendar();
        UpdateDependents();
        _loading = false;
        Closing += (_, _) => ApplyCity(); // typed a city and closed without pressing Enter
    }

    /// <summary>Raised after a setting was changed and saved, with the property's name.</summary>
    public event Action<string>? Changed;

    /// <summary>Raised when "set up calendar" is clicked.</summary>
    public event Action? CalendarRequested;

    public void RefreshCalendar() => CalendarStatus.Text = _calendarStatus();

    private void Switch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox box || Property(box) is not { } property) return;
        property.SetValue(_settings, box.IsChecked == true);
        Save(property.Name);
        UpdateDependents();
    }

    private void Style_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Style = StyleNotch.IsChecked == true ? IslandStyle.Notch : IslandStyle.Island;
        Save(nameof(AppSettings.Style));
    }

    private void CityBox_TextChanged(object sender, TextChangedEventArgs e) =>
        CityPlaceholder.Visibility = CityBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void CityBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyCity();
    }

    private void CityBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyCity();

    private void ApplyCity()
    {
        string city = CityBox.Text.Trim();
        if (city == _settings.WeatherCity) return;
        _settings.WeatherCity = city;
        Save(nameof(AppSettings.WeatherCity));
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            AppSettings.StartWithWindows = StartupSwitch.IsChecked == true;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void Calendar_Click(object sender, RoutedEventArgs e) => CalendarRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Save(string name)
    {
        _settings.Save();
        Changed?.Invoke(name);
    }

    /// <summary>Options that only matter when another one is on are greyed out otherwise.</summary>
    private void UpdateDependents()
    {
        foreach (var box in Switches(this))
        {
            box.IsEnabled = (box.Tag as string) switch
            {
                nameof(AppSettings.CompactLyrics) => _settings.ShowLyrics,
                nameof(AppSettings.ShowNotificationContent) or nameof(AppSettings.HideWindowsBanners) => _settings.ShowAppNotifications,
                _ => true,
            };
        }
        CityBox.IsEnabled = _settings.ShowWeather;
    }

    private static PropertyInfo? Property(CheckBox box) =>
        box.Tag is string name && typeof(AppSettings).GetProperty(name) is { PropertyType: var type } property && type == typeof(bool) ? property : null;

    // The logical tree: it exists right after InitializeComponent, before any template is applied.
    private static IEnumerable<CheckBox> Switches(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is CheckBox box) yield return box;
            foreach (var nested in Switches(child)) yield return nested;
        }
    }
}
