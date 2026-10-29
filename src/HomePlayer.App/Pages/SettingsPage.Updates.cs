using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed partial class SettingsPage
{
    private UpdateInfo? _availableUpdate;
    private bool _updateEvents;

    private void InitializeUpdateSettings()
    {
        UpdateChannelBox.SelectedIndex = App.Host.Settings.UpdateChannel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (_updateEvents)
            return;
        _updateEvents = true;
        UpdateChannelBox.SelectionChanged += (_, _) =>
        {
            if (_loading)
                return;
            App.Host.Settings.UpdateChannel = UpdateChannelBox.SelectedIndex == 1 ? "beta" : "stable";
            _availableUpdate = null;
            UpdateDownloadButton.Visibility = Visibility.Collapsed;
            App.Host.SaveSettings();
        };
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateDownloadButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var channel = UpdateChannelBox.SelectedIndex == 1 ? "beta" : "stable";
            var result = await UpdateChecker.CheckAsync(UpdateChecker.CurrentVersion(), channel);
            _availableUpdate = result.HasUpdate ? result.Latest : null;
            UpdateStatusText.Text = result.Message;
            UpdateDownloadButton.Visibility = _availableUpdate == null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查更新失败：" + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate != null && Uri.TryCreate(_availableUpdate.Url, UriKind.Absolute, out var uri))
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
    }
}
