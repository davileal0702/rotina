using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rotina.Core;

namespace Rotina.App;

public enum ChipKind { Text, Site, App }

public sealed record Chip(string Value, string Label, ImageSource? Icon);

/// <summary>Editable list shown as removable chips: apps (picked from the catalog), sites or plain text.</summary>
public partial class ChipEditor : UserControl
{
    public ObservableCollection<Chip> Items { get; } = [];
    public ChipKind Kind { get; set; }
    public string InputLabel { get; set; } = "Novo item";

    public ChipEditor()
    {
        InitializeComponent();
        ChipItems.ItemsSource = Items;
        Items.CollectionChanged += (_, _) => EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            AppButton.Visibility = Kind == ChipKind.App ? Visibility.Visible : Visibility.Collapsed;
            InputRow.Visibility = Kind == ChipKind.App ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(InputBox, InputLabel);
        };
    }

    public List<string> Values => [.. Items.Select(c => c.Value)];

    public void SetValues(IEnumerable<string> values)
    {
        Items.Clear();
        foreach (var v in values)
        {
            if (Kind != ChipKind.App) { Items.Add(new(v, v, null)); continue; }
            var app = AppCatalog.Describe(v);
            Items.Add(new(v, app.Name, app.Icon));
        }
        ErrorText.Visibility = Visibility.Collapsed;
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Chip c) Items.Remove(c);
    }

    void PickApps_Click(object sender, RoutedEventArgs e)
    {
        var picker = new AppPickerWindow(Values) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true) return;
        foreach (var app in picker.Picked)
            if (!Values.Contains(app.Path, StringComparer.OrdinalIgnoreCase)) Items.Add(new(app.Path, app.Name, app.Icon));
    }

    void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Add_Click(sender, e);
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var value = Kind == ChipKind.Site ? Sites.Normalize(InputBox.Text) : InputBox.Text.Trim();
        if (string.IsNullOrEmpty(value))
        {
            ErrorText.Text = Kind == ChipKind.Site ? "Digite um domínio, por exemplo youtube.com." : "Digite um valor.";
            ErrorText.Visibility = Visibility.Visible;
            InputBox.Focus();
            return;
        }
        if (!Values.Contains(value, StringComparer.OrdinalIgnoreCase)) Items.Add(new(value, value, null));
        InputBox.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
        InputBox.Focus();
    }
}
