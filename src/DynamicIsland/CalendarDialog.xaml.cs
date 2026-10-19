using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>Small dark window to paste (or remove) the iCal link behind the agenda.</summary>
public partial class CalendarDialog : Window
{
    private readonly bool _hadSource;

    internal CalendarDialog(string source)
    {
        InitializeComponent();
        SourceBox.Text = source;
        _hadSource = source.Length > 0;
        RemoveButton.Visibility = _hadSource ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            SourceBox.Focus();
            SourceBox.SelectAll();
        };
    }

    /// <summary>The new link or file path; empty when the calendar was removed.</summary>
    public event Action<string>? Saved;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string source = SourceBox.Text.Trim();
        if (source.Length == 0)
        {
            if (_hadSource) Saved?.Invoke("");
            Close();
            return;
        }
        if (!CalendarService.IsValidSource(source))
        {
            ErrorText.Text = "Itu bukan link kalender. Link iCal diawali https:// atau webcal:// (atau pilih file .ics yang ada di PC ini).";
            ErrorText.Visibility = Visibility.Visible;
            SourceBox.Focus();
            return;
        }
        Saved?.Invoke(source);
        Close();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        Saved?.Invoke("");
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SourceBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Placeholder.Visibility = SourceBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void SourceBox_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        InputChrome.BorderBrush = SourceBox.IsKeyboardFocused ? (Brush)FindResource("CalendarBrush") : Brushes.Transparent;

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
