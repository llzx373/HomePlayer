using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class SettingsPage
{
    private async void CleanupOrphans_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清理孤立记录",
            Content = "将清理没有关联媒体的数据库行，以及本地磁盘上已不存在的库外历史记录。当前媒体库、仍然存在的文件和离线路径不会清理。",
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            var result = await Task.Run(() => App.Host.Repository.CleanupOrphanRecords());
            BackupStatusText.Text = $"已清理 {result.Total} 条孤立记录：数据库 {result.DatabaseRows}，观看历史 {result.PlaybackStates}，播放偏好 {result.Preferences}。";
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = "清理失败：" + ex.Message;
        }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var sensitive = DiagnosticSensitiveCheck.IsChecked == true;
        if (sensitive)
        {
            var warning = new ContentDialog
            {
                Title = "包含敏感信息",
                Content = "完整日志和设置可能包含媒体路径、本机用户名和 TMDB API Key。请只把诊断包发给你信任的人。",
                PrimaryButtonText = "继续导出",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            if (await warning.ShowAsync() != ContentDialogResult.Primary)
                return;
        }
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "HomePlayer-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
            picker.FileTypeChoices.Add("ZIP 压缩包", [".zip"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
            var file = await picker.PickSaveFileAsync();
            if (file == null)
                return;
            App.Host.SaveSettings();
            var pipeline = App.Host.Playback.Pipeline();
            var settings = sensitive ? await File.ReadAllTextAsync(AppPaths.SettingsFile) : null;
            var sensitiveValues = App.Host.Repository.ListLibraries().Select(library => library.RootPath).ToList();
            if (!string.IsNullOrWhiteSpace(App.Host.Settings.TmdbApiKey))
                sensitiveValues.Add(App.Host.Settings.TmdbApiKey);
            BackupStatusText.Text = "正在导出诊断包…";
            await Task.Run(() => DiagnosticsPackage.Export(
                file.Path, pipeline, AppPaths.Crashes, sensitive, settings, [.. sensitiveValues]));
            BackupStatusText.Text = "诊断包已导出到 " + file.Path;
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = "诊断包导出失败：" + ex.Message;
        }
    }
}
