using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CrtSharp;

public partial class MainWindow
{
    private static readonly string ExportFileFilter =
        "CrtSharp 配置 (*.crtsharpconfig)|*.crtsharpconfig|所有文件 (*.*)|*.*";

    private async void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出用户配置",
            Filter = ExportFileFilter,
            FileName = $"CrtSharp配置-{DateTime.Now:yyyyMMddHHmm}.crtsharpconfig"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var passwordDialog = new ConfigPasswordDialog(
            ConfigPasswordMode.Create,
            "为导出文件设置一个密码。文件整体加密（AES-256-GCM），包含会话、已记住的密码和私钥，请妥善保管。");
        if (!ShowOwnedDialog(passwordDialog))
        {
            return;
        }

        try
        {
            Services.ConfigTransferService.Export(
                dialog.FileName, passwordDialog.Password, _profiles, _knownHosts);
            StatusText.Text = $"配置已导出：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"导出失败：{exception.Message}", "导出配置",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await Task.CompletedTask;
    }

    private bool ShowOwnedDialog(Window dialog)
    {
        dialog.Owner = this;
        return dialog.ShowDialog() == true;
    }

    private async void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入用户配置",
            Filter = ExportFileFilter
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var passwordDialog = new ConfigPasswordDialog(
            ConfigPasswordMode.Unlock, "输入该配置文件的导出密码以解密。");        if (!ShowOwnedDialog(passwordDialog))
        {
            return;
        }

        Services.ConfigTransferService.ImportResult result;
        try
        {
            result = Services.ConfigTransferService.Import(dialog.FileName, passwordDialog.Password);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"导入失败：{exception.Message}", "导入配置",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 按会话 Id 去重合并：已存在的覆盖，新的追加。
        var importedCount = 0;
        var updatedCount = 0;
        foreach (var imported in result.Payload.Sessions)
        {
            if (result.RestoredKeyPaths.TryGetValue(
                    Path.GetFileName(imported.PrivateKeyPath), out var restoredPath)
                && !File.Exists(imported.PrivateKeyPath))
            {
                imported.PrivateKeyPath = restoredPath;
            }

            var existing = _profiles.FirstOrDefault(item => item.Id == imported.Id);
            if (existing is not null)
            {
                var index = _profiles.IndexOf(existing);
                _profiles[index] = imported;
                updatedCount++;
            }
            else
            {
                _profiles.Add(imported);
                importedCount++;
            }
        }

        Services.ConfigTransferService.ApplyPasswords(result.Payload, _profiles);
        _knownHosts.Merge(result.Payload.KnownHosts);
        _sessionStore.Save(_profiles);

        StatusText.Text = $"导入完成：新增 {importedCount} 个会话，更新 {updatedCount} 个会话。";
        await Task.CompletedTask;
    }
}
