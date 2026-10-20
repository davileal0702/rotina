using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Rotina.App;

/// <summary>Corner notice shown when a blocked app is closed. Never takes focus; hides itself after a few seconds.</summary>
public partial class BlockToast : Window
{
    static BlockToast? current;
    readonly DispatcherTimer hide = new() { Interval = TimeSpan.FromSeconds(6) };

    BlockToast()
    {
        InitializeComponent();
        hide.Tick += (_, _) => { hide.Stop(); Close(); };
        Closed += (_, _) => current = null;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
        };
    }

    public static void Notify(string title, string body, Brush accent)
    {
        current ??= new BlockToast();
        current.TitleText.Text = title;
        current.BodyText.Text = body;
        current.Accent.Background = accent;
        current.hide.Stop();
        current.hide.Start();
        if (!current.IsVisible) current.Show();
    }
}
