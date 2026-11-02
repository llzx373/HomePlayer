using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class SettingsPage
{
    private void InitializeRefreshRateSettings()
    {
        try
        {
            var options = DisplayRefreshRate.List(WindowNative.GetWindowHandle(App.Window), out var display, out var current);
            RefreshRateBox.ItemsSource = options;
            var saved = App.Host.Settings.DisplayRefreshRate;
            RefreshRateBox.SelectedItem = options.FirstOrDefault(option => option.Hertz == saved) ?? options[0];
            RefreshRateStatus.Text = $"{display} 当前报告 {current} Hz。仅改变刷新率，不改变分辨率。";
        }
        catch (Exception ex)
        {
            RefreshRateBox.ItemsSource = new[] { new DisplayRefreshRateOption(0, "跟随系统（不修改 Windows）") };
            RefreshRateBox.SelectedIndex = 0;
            RefreshRateBox.IsEnabled = false;
            RefreshRateStatus.Text = "无法枚举当前显示器：" + ex.Message;
        }
    }

    private void RefreshRate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RefreshRateBox.SelectedItem is not DisplayRefreshRateOption selected)
            return;
        var previous = App.Host.Settings.DisplayRefreshRate;
        try
        {
            if (selected.Hertz > 0)
            {
                var actual = DisplayRefreshRate.Apply(WindowNative.GetWindowHandle(App.Window), selected.Hertz);
                RefreshRateStatus.Text = $"已应用并保存 {actual} Hz；下次启动会再次应用。";
            }
            else
            {
                RefreshRateStatus.Text = "已改为跟随系统，不会主动修改 Windows 刷新率。";
            }
            App.Host.Settings.DisplayRefreshRate = selected.Hertz;
            SaveSettings();
        }
        catch (Exception ex)
        {
            RefreshRateStatus.Text = ex.Message + " 原设置未修改。";
            _loading = true;
            try
            {
                RefreshRateBox.SelectedItem = RefreshRateBox.ItemsSource is System.Collections.IEnumerable items
                    ? items.Cast<DisplayRefreshRateOption>().FirstOrDefault(option => option.Hertz == previous)
                    : null;
            }
            finally
            {
                _loading = false;
            }
        }
    }
}
