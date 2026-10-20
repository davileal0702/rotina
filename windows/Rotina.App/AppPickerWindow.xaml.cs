using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;

namespace Rotina.App;

public sealed class PickRow(WinApp app)
{
    public WinApp App { get; } = app;
    public string Name => App.Name;
    public bool Checked { get; set; }
}

public partial class AppPickerWindow : Window
{
    readonly HashSet<string> already;
    List<PickRow> rows = [];
    public List<WinApp> Picked { get; } = [];

    public AppPickerWindow(IEnumerable<string> alreadyAllowed)
    {
        InitializeComponent();
        already = new(alreadyAllowed, StringComparer.OrdinalIgnoreCase);
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            rows = [.. (await AppCatalog.All()).Where(a => !already.Contains(a.Path)).Select(a => new PickRow(a))];
            AppList.ItemsSource = rows;
            CollectionViewSource.GetDefaultView(rows).Filter = o => Matches((PickRow)o);
            UpdateEmpty();
        };
    }

    bool Matches(PickRow r)
    {
        var q = SearchBox.Text.Trim();
        return q.Length == 0
            || r.App.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.App.Path.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (AppList.ItemsSource is null) return;
        CollectionViewSource.GetDefaultView(rows).Refresh();
        UpdateEmpty();
    }

    void UpdateEmpty()
    {
        var any = rows.Any(Matches);
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = rows.Count == 0 && SearchBox.Text.Length == 0
            ? "Todos os apps encontrados já estão liberados. Use \"Procurar .exe...\" para escolher outro arquivo."
            : $"Nenhum app com \"{SearchBox.Text.Trim()}\". Tente outra parte do nome, ou use \"Procurar .exe...\" para escolher o arquivo.";
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolher programa",
            Filter = "Programas (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        Picked.AddRange(dialog.FileNames.Where(f => !already.Contains(f)).Select(AppCatalog.Describe));
        Add_Click(sender, e);
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Picked.AddRange(rows.Where(r => r.Checked).Select(r => r.App));
        DialogResult = true;
    }
}
