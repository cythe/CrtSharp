using System.Windows;

namespace CrtSharp;

public enum ConfigPasswordMode
{
    /// <summary>导出：输入并确认密码。</summary>
    Create,
    /// <summary>导入：输入密码解密。</summary>
    Unlock
}

public partial class ConfigPasswordDialog : Window
{
    private readonly ConfigPasswordMode _mode;

    public ConfigPasswordDialog(ConfigPasswordMode mode, string prompt)
    {
        InitializeComponent();
        _mode = mode;
        PromptText.Text = prompt;
        if (mode == ConfigPasswordMode.Unlock)
        {
            ConfirmLabel.Visibility = Visibility.Collapsed;
            ConfirmBox.Visibility = Visibility.Collapsed;
        }
        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string Password => PasswordBox.Password;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            ErrorText.Text = "密码不能为空。";
            return;
        }
        if (_mode == ConfigPasswordMode.Create
            && !string.Equals(PasswordBox.Password, ConfirmBox.Password, StringComparison.Ordinal))
        {
            ErrorText.Text = "两次输入的密码不一致。";
            return;
        }
        DialogResult = true;
    }
}
