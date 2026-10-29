using System.Windows;

namespace CrtSharp;

public partial class RenameTabDialog : Window
{
    public RenameTabDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string TabName { get; private set; } = string.Empty;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var value = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            ValidationText.Text = "标签名称不能为空。";
            return;
        }

        TabName = value;
        DialogResult = true;
    }
}
