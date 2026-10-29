using System.Windows;

namespace CrtSharp;

public partial class MainWindow
{
    private async void FindTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSession() is { } active)
        {
            await active.Terminal.ShowSearchAsync();
        }
        else
        {
            StatusText.Text = "请先打开或选择一个终端标签";
        }
    }
}
