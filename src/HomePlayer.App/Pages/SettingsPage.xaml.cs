using HomePlayer.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        var settings = App.Host.Settings;
        ApiKeyBox.Text = settings.TmdbApiKey ?? "";
        TraySwitch.IsOn = settings.CloseToTray;
        var lav = LavLocator.Detect();
        LavText.Text = lav == null
            ? "未检测到 64 位 LAV Filters。播放时会提示，并可以改用 FFmpeg。"
            : $"已检测到 LAV Video：{lav.Version ?? "未知版本"}（{lav.VideoDecoderPath}）";
        CacheText.Text = $"缓存占用 {Format(App.Host.Libraries.CacheBytes())}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.TmdbApiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Text) ? null : ApiKeyBox.Text.Trim();
        App.Host.Settings.CloseToTray = TraySwitch.IsOn;
        App.Host.SaveSettings();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清除缓存",
            Content = "删除已生成的缩略图和海报，并重新处理媒体库。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        App.Host.Libraries.ClearCache();
        CacheText.Text = $"缓存占用 {Format(App.Host.Libraries.CacheBytes())}";
    }

    private static string Format(long bytes)
    {
        double size = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
