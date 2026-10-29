using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HomePlayer.Core;
using HomePlayer.Data;
using HomePlayer.Playback;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class SettingsPage : Page
{
    private readonly ObservableCollection<CodecRow> _splitters = [];
    private readonly ObservableCollection<CodecRow> _videos = [];
    private readonly ObservableCollection<CodecRow> _audios = [];
    private readonly ObservableCollection<HotkeyRow> _hotkeys = [];
    private readonly DispatcherQueueTimer _apiKeyTimer;
    private HotkeyRow? _capturing;
    private bool _apiKeyDirty;
    private bool _loading = true;
    private bool _codecsReady;
    private int _codecScanVersion;
    private int _audioLoadVersion;
    private int _crashSummaryVersion;

    public SettingsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _apiKeyTimer = DispatcherQueue.CreateTimer();
        _apiKeyTimer.Interval = TimeSpan.FromMilliseconds(600);
        _apiKeyTimer.IsRepeating = false;
        _apiKeyTimer.Tick += (_, _) => SaveApiKey();
        var settings = App.Host.Settings;
        ApiKeyBox.Text = settings.TmdbApiKey ?? "";
        ApiKeyBox.TextChanged += ApiKey_Changed;
        TraySwitch.IsOn = settings.CloseToTray;
        HistorySwitch.IsOn = settings.RecordPlaybackHistory;
        TitleBarBox.SelectedIndex = settings.TitleBar == TitleBarMode.Overlay ? 1 : 0;
        FitWindowSwitch.IsOn = settings.FitWindow;
        AlwaysOnTopSwitch.IsOn = settings.AlwaysOnTop;
        AutoPlayNextSwitch.IsOn = settings.AutoPlayNextEpisode;
        SubtitleFontSizeBox.Value = settings.SubtitleFontSize;
        SubtitleColorBox.SelectedIndex = (int)settings.SubtitleColor;
        SubtitlePositionBox.SelectedIndex = (int)settings.SubtitlePosition;
        SubtitleOpacitySlider.Value = settings.SubtitleBackgroundOpacity * 100;
        TitleBarBox.SelectionChanged += TitleBar_Changed;
        FitWindowSwitch.Toggled += FitWindow_Toggled;
        AlwaysOnTopSwitch.Toggled += AlwaysOnTop_Toggled;
        SplitterList.ItemsSource = _splitters;
        VideoList.ItemsSource = _videos;
        AudioList.ItemsSource = _audios;
        HotkeyList.ItemsSource = _hotkeys;
        LoadHotkeysSafe(settings.Hotkeys);
        AudioDeviceBox.ItemsSource = DefaultAudioDevices();
        AudioDeviceBox.SelectedIndex = 0;
        AudioDeviceBox.IsEnabled = false;
        CodecSummary.Text = "正在扫描编解码器…";
        _ = ReloadCodecsAsync(settings);
        SettingsTabs.SelectionChanged += (_, _) => EndCapture();
        AddHandler(KeyDownEvent, new KeyEventHandler(Page_KeyDown), true);
        Unloaded += (_, _) =>
        {
            EndCapture();
            ++_audioLoadVersion;
            ++_crashSummaryVersion;
            _apiKeyTimer.Stop();
            SaveApiKey();
        };
        Loaded += (_, _) =>
        {
            EndCapture();
            _loading = true;
            try
            {
                TraySwitch.IsOn = App.Host.Settings.CloseToTray;
                HistorySwitch.IsOn = App.Host.Settings.RecordPlaybackHistory;
                FitWindowSwitch.IsOn = App.Host.Settings.FitWindow;
                AlwaysOnTopSwitch.IsOn = App.Host.Settings.AlwaysOnTop;
                AutoPlayNextSwitch.IsOn = App.Host.Settings.AutoPlayNextEpisode;
                SubtitleFontSizeBox.Value = App.Host.Settings.SubtitleFontSize;
                SubtitleColorBox.SelectedIndex = (int)App.Host.Settings.SubtitleColor;
                SubtitlePositionBox.SelectedIndex = (int)App.Host.Settings.SubtitlePosition;
                SubtitleOpacitySlider.Value = App.Host.Settings.SubtitleBackgroundOpacity * 100;
            }
            finally
            {
                _loading = false;
            }
            _ = LoadAudioDevicesAsync(App.Host.Settings.AudioOutputId);
            _ = RefreshCacheTextAsync();
            _ = RefreshCrashSummaryAsync();
        };
        _loading = false;
        CacheText.Text = "缓存占用计算中…";
        CrashSummary.Text = "正在读取崩溃日志…";
    }

    private void LoadHotkeysSafe(Dictionary<string, string> saved)
    {
        try
        {
            LoadHotkeys(saved);
        }
        catch (Exception ex)
        {
            CrashLog.Write(CrashLog.UnhandledKind, "加载快捷键失败" + Environment.NewLine + ex);
        }
    }

    private async Task LoadAudioDevicesAsync(string? savedId)
    {
        var version = ++_audioLoadVersion;
        AudioDeviceBox.IsEnabled = false;
        try
        {
            var items = await Task.Run(() => AudioOutputs.List(savedId));
            if (version != _audioLoadVersion)
                return;
            ApplyAudioDevices(items, savedId);
        }
        catch (Exception ex)
        {
            if (version != _audioLoadVersion)
                return;
            CrashLog.Write(CrashLog.UnhandledKind, "枚举播放设备失败" + Environment.NewLine + ex);
            ApplyAudioDevices(DefaultAudioDevices(), null);
        }
        finally
        {
            if (version == _audioLoadVersion)
                AudioDeviceBox.IsEnabled = true;
        }
    }

    private void ApplyAudioDevices(IReadOnlyList<AudioOutput> items, string? savedId)
    {
        _loading = true;
        try
        {
            AudioDeviceBox.ItemsSource = items;
            AudioDeviceBox.SelectedItem = items.FirstOrDefault(item => item.Id == (savedId ?? AudioOutputs.SystemDefaultId)) ?? items[0];
        }
        finally
        {
            _loading = false;
        }
    }

    private static IReadOnlyList<AudioOutput> DefaultAudioDevices() =>
        [new(AudioOutputs.SystemDefaultId, "系统默认", true)];

    private async Task RefreshCacheTextAsync()
    {
        try
        {
            var bytes = await Task.Run(() => App.Host.Libraries.CacheBytes());
            CacheText.Text = $"缓存占用 {Format(bytes)}";
        }
        catch (Exception)
        {
            CacheText.Text = "缓存占用未知";
        }
    }

    private void ApiKey_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        _apiKeyDirty = true;
        SaveStatusText.Text = "等待自动保存…";
        _apiKeyTimer.Stop();
        _apiKeyTimer.Start();
    }

    private void SaveApiKey()
    {
        _apiKeyTimer.Stop();
        if (!_apiKeyDirty)
            return;
        App.Host.Settings.TmdbApiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Text) ? null : ApiKeyBox.Text.Trim();
        _apiKeyDirty = false;
        SaveSettings();
    }

    private void SaveSettings()
    {
        App.Host.SaveSettings();
        SaveStatusText.Text = "已自动保存 " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Host.Settings.CloseToTray = TraySwitch.IsOn;
        SaveSettings();
    }

    private void History_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Host.Settings.RecordPlaybackHistory = HistorySwitch.IsOn;
        SaveSettings();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清空播放历史",
            Content = "将删除全部最近播放、续播位置和已看状态。此操作无法撤销。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await Task.Run(() => App.Host.Repository.ClearPlaybackStates());
            App.Host.Playback.ClearHistory();
            SaveStatusText.Text = "播放历史已清空。";
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "清空失败：" + ex.Message;
        }
    }

    private void TitleBar_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TitleBarBox.SelectedIndex < 0)
            return;
        App.Host.Settings.TitleBar = TitleBarBox.SelectedIndex == 1 ? TitleBarMode.Overlay : TitleBarMode.Classic;
        SaveSettings();
        App.Window.ApplyTitleBar();
    }

    private void FitWindow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Host.Settings.FitWindow = FitWindowSwitch.IsOn;
        SaveSettings();
    }

    private void AlwaysOnTop_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Host.Settings.AlwaysOnTop = AlwaysOnTopSwitch.IsOn;
        SaveSettings();
        App.Window.SetAlwaysOnTop(AlwaysOnTopSwitch.IsOn);
    }

    private void AutoPlayNext_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.Host.Settings.AutoPlayNextEpisode = AutoPlayNextSwitch.IsOn;
        App.Host.Playback.AutoAdvance = AutoPlayNextSwitch.IsOn;
        SaveSettings();
    }

    private void SubtitleFontSize_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value))
            return;
        App.Host.Settings.SubtitleFontSize = Math.Clamp(sender.Value, 16, 64);
        SaveSettings();
    }

    private void SubtitleStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SubtitleColorBox.SelectedIndex < 0 || SubtitlePositionBox.SelectedIndex < 0)
            return;
        App.Host.Settings.SubtitleColor = (SubtitleTextColor)SubtitleColorBox.SelectedIndex;
        App.Host.Settings.SubtitlePosition = (SubtitleVerticalPosition)SubtitlePositionBox.SelectedIndex;
        SaveSettings();
    }

    private void SubtitleOpacity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
            return;
        App.Host.Settings.SubtitleBackgroundOpacity = Math.Clamp(e.NewValue / 100, 0, 1);
        SaveSettings();
    }

    private async void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps?registeredAppMachine=HomePlayer"));
        }
        catch (Exception)
        {
        }
    }

    private void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        WriteAudioDevice();
        SaveSettings();
    }

    private void SplitterUp_Click(object sender, RoutedEventArgs e) => Move(_splitters, SplitterList, -1);

    private void SplitterDown_Click(object sender, RoutedEventArgs e) => Move(_splitters, SplitterList, 1);

    private void VideoUp_Click(object sender, RoutedEventArgs e) => Move(_videos, VideoList, -1);

    private void VideoDown_Click(object sender, RoutedEventArgs e) => Move(_videos, VideoList, 1);

    private void AudioUp_Click(object sender, RoutedEventArgs e) => Move(_audios, AudioList, -1);

    private void AudioDown_Click(object sender, RoutedEventArgs e) => Move(_audios, AudioList, 1);

    private async void ResetCodecs_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            await ReloadCodecsAsync(new AppSettings());
        }
        finally
        {
            _loading = false;
        }

        WriteCodecs();
        SaveSettings();
    }

    private void CodecRow_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(CodecRow.Enabled))
            return;
        WriteCodecs();
        SaveSettings();
    }

    private void WriteAudioDevice()
    {
        App.Host.Settings.AudioOutputId = AudioDeviceBox.SelectedItem is AudioOutput selected && !string.IsNullOrWhiteSpace(selected.Id)
            ? selected.Id
            : null;
    }

    private async Task ReloadCodecsAsync(AppSettings settings)
    {
        var version = ++_codecScanVersion;
        IReadOnlyList<InstalledCodec> all;
        try
        {
            all = await Task.Run(CodecCatalog.Scan);
        }
        catch (Exception ex)
        {
            if (version != _codecScanVersion)
                return;
            _codecsReady = false;
            CodecSummary.Text = "扫描编解码器失败：" + ex.Message;
            return;
        }

        if (version != _codecScanVersion)
            return;
        Fill(_splitters, CodecCatalog.Display(all, CodecKind.Splitter, settings.SplitterOrder));
        Fill(_videos, CodecCatalog.Display(all, CodecKind.VideoDecoder, settings.VideoDecoderOrder));
        Fill(_audios, CodecCatalog.Display(all, CodecKind.AudioDecoder, settings.AudioDecoderOrder));
        _codecsReady = true;
        RefreshSummary();
    }

    private void Fill(ObservableCollection<CodecRow> target, IReadOnlyList<InstalledCodec> codecs)
    {
        foreach (var row in target)
            row.PropertyChanged -= CodecRow_Changed;
        target.Clear();
        foreach (var codec in codecs)
        {
            var row = new CodecRow(codec);
            row.PropertyChanged += CodecRow_Changed;
            target.Add(row);
        }

        Renumber(target);
    }

    private void Move(ObservableCollection<CodecRow> rows, ListView list, int delta)
    {
        var index = list.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= rows.Count)
            return;
        var item = rows[index];
        rows.RemoveAt(index);
        rows.Insert(target, item);
        Renumber(rows);
        list.SelectedIndex = target;
        WriteCodecs();
        SaveSettings();
    }

    private void WriteCodecs()
    {
        if (!_codecsReady)
            return;
        App.Host.Settings.SplitterOrder = ToPreferences(_splitters);
        App.Host.Settings.VideoDecoderOrder = ToPreferences(_videos);
        App.Host.Settings.AudioDecoderOrder = ToPreferences(_audios);
        App.Host.InvalidateCodecPlan();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var process = Environment.Is64BitProcess ? "64 位" : "32 位";
        CodecSummary.Text = $"本程序是 {process} 进程。下次播放会先用：{First(_splitters, "分离器")}；{First(_videos, "视频解码")}；{First(_audios, "音频解码")}。";
    }

    private static string First(ObservableCollection<CodecRow> rows, string role)
    {
        var row = rows.FirstOrDefault(item => item.Enabled && item.Usable);
        return row == null ? $"{role}无可用项" : $"{role} {row.Codec.Name} {row.Codec.BitnessText}";
    }

    private static void Renumber(ObservableCollection<CodecRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
            rows[i].SetIndex(i + 1);
    }

    private static List<CodecPreference> ToPreferences(ObservableCollection<CodecRow> rows)
        => rows.Select(row => new CodecPreference { Id = row.Codec.Id, Enabled = row.Enabled && row.Usable }).ToList();

    private void LoadHotkeys(Dictionary<string, string> saved)
    {
        var map = HotkeyCatalog.Merge(saved);
        _hotkeys.Clear();
        foreach (var action in HotkeyCatalog.Actions)
            _hotkeys.Add(new HotkeyRow(action.Id, action.Title, map[action.Id]));
    }

    private void WriteHotkeys()
    {
        App.Host.Settings.Hotkeys = _hotkeys.ToDictionary(row => row.Id, row => row.Chord);
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        EndCapture();
        LoadHotkeys(HotkeyCatalog.Defaults());
        WriteHotkeys();
        SaveSettings();
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not HotkeyRow row)
            return;
        if (_capturing == row)
        {
            EndCapture();
            return;
        }

        EndCapture();
        _capturing = row;
        row.Capturing = true;
        Focus(FocusState.Programmatic);
    }

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not HotkeyRow row)
            return;
        if (_capturing == row)
            EndCapture();
        Assign(row, "");
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capturing == null || HotkeyChord.IsModifier(e.Key))
            return;

        var modifiers = VirtualKeyModifiers.None;
        if (Down(VirtualKey.Control))
            modifiers |= VirtualKeyModifiers.Control;
        if (Down(VirtualKey.Menu))
            modifiers |= VirtualKeyModifiers.Menu;
        if (Down(VirtualKey.Shift))
            modifiers |= VirtualKeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
            modifiers |= VirtualKeyModifiers.Windows;
        Assign(_capturing, HotkeyChord.Format(e.Key, modifiers));
        EndCapture();
        e.Handled = true;
    }

    private void Assign(HotkeyRow row, string chord)
    {
        if (!string.IsNullOrEmpty(chord))
        {
            foreach (var other in _hotkeys)
            {
                if (other != row && string.Equals(other.Chord, chord, StringComparison.OrdinalIgnoreCase))
                    other.Chord = "";
            }
        }

        row.Chord = chord;
        WriteHotkeys();
        SaveSettings();
    }

    private void EndCapture()
    {
        if (_capturing == null)
            return;
        _capturing.Capturing = false;
        _capturing = null;
    }

    private static bool Down(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async Task RefreshCrashSummaryAsync()
    {
        var version = ++_crashSummaryVersion;
        try
        {
            var items = await Task.Run(CrashLog.List);
            if (version != _crashSummaryVersion)
                return;
            CrashSummary.Text = items.Count == 0
                ? "还没有崩溃记录。"
                : $"共 {items.Count} 条，最近：{items[0].Title}";
        }
        catch (Exception)
        {
            if (version == _crashSummaryVersion)
                CrashSummary.Text = "崩溃日志读取失败。";
        }
    }

    private async void ViewCrashes_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<CrashLogItem> items;
        try
        {
            items = await Task.Run(CrashLog.List);
        }
        catch (Exception ex)
        {
            await Message("读取崩溃日志失败：" + ex.Message);
            return;
        }

        if (items.Count == 0)
        {
            await Message("还没有崩溃记录。");
            _ = RefreshCrashSummaryAsync();
            return;
        }

        var list = new ListView
        {
            ItemsSource = items,
            SelectedIndex = 0,
            MaxHeight = 180,
            DisplayMemberPath = nameof(CrashLogItem.Title)
        };
        var hint = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        var body = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 260,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12
        };
        void ShowSelected()
        {
            if (list.SelectedItem is not CrashLogItem item)
            {
                hint.Text = "";
                body.Text = "";
                return;
            }

            hint.Text = item.Detail;
            try
            {
                body.Text = CrashLog.Read(item.Path);
            }
            catch (Exception ex)
            {
                body.Text = "无法读取：" + ex.Message;
            }
        }

        list.SelectionChanged += (_, _) => ShowSelected();
        ShowSelected();
        var panel = new StackPanel { Spacing = 8, Width = 520 };
        panel.Children.Add(hint);
        panel.Children.Add(list);
        panel.Children.Add(body);
        await new ContentDialog
        {
            Title = "崩溃日志",
            Content = new ScrollViewer { Content = panel, HorizontalScrollMode = ScrollMode.Disabled },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        }.ShowAsync();
        _ = RefreshCrashSummaryAsync();
    }

    private async void OpenCrashes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.Ensure();
            await Launcher.LaunchFolderPathAsync(CrashLog.DirectoryPath);
        }
        catch (Exception ex)
        {
            await Message("无法打开日志文件夹：" + ex.Message);
        }
    }

    private async void ClearCrashes_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清除崩溃日志",
            Content = "删除本机保存的全部崩溃记录和转储文件。不影响媒体库和设置。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await Task.Run(CrashLog.Clear);
            _ = RefreshCrashSummaryAsync();
        }
        catch (Exception ex)
        {
            await Message("清除失败：" + ex.Message);
        }
    }

    private async Task Message(string text) =>
        await new ContentDialog { Title = "HomePlayer", Content = text, CloseButtonText = "确定", XamlRoot = XamlRoot }.ShowAsync();

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Host.SaveSettings();
            var picker = new FileSavePicker
            {
                SuggestedFileName = "HomePlayer-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
            };
            picker.FileTypeChoices.Add("HomePlayer 备份", [".homeplayer"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
            var file = await picker.PickSaveFileAsync();
            if (file == null)
                return;
            BackupStatusText.Text = "正在导出数据库快照…";
            await Task.Run(() => DataBackup.Export(AppPaths.Database, AppPaths.SettingsFile, file.Path));
            BackupStatusText.Text = "备份已导出到 " + file.Path;
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".homeplayer");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return;
            var dialog = new ContentDialog
            {
                Title = "导入备份",
                Content = "将用备份中的媒体库、观看进度、播放偏好和设置替换当前数据。旧数据会自动备份，恢复在下次启动时完成。",
                PrimaryButtonText = "校验并导入",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            BackupStatusText.Text = "正在校验备份…";
            await Task.Run(() => DataBackup.StageRestore(file.Path, AppPaths.Root));
            BackupStatusText.Text = "备份已通过校验。请重启 HomePlayer 完成恢复。";
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "backups");
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = "无法打开备份文件夹：" + ex.Message;
        }
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
        try
        {
            await App.Host.Libraries.ClearCacheAsync();
            await RefreshCacheTextAsync();
        }
        catch (Exception ex)
        {
            CacheText.Text = "清除缓存失败：" + ex.Message;
        }
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

public sealed class HotkeyRow : INotifyPropertyChanged
{
    private string _chord;
    private bool _capturing;

    public HotkeyRow(string id, string title, string chord)
    {
        Id = id;
        Title = title;
        _chord = chord;
    }

    public string Id { get; }
    public string Title { get; }

    public string Chord
    {
        get => _chord;
        set
        {
            if (_chord == value)
                return;
            _chord = value;
            OnChanged();
            OnChanged(nameof(Display));
        }
    }

    public bool Capturing
    {
        get => _capturing;
        set
        {
            if (_capturing == value)
                return;
            _capturing = value;
            OnChanged();
            OnChanged(nameof(Display));
        }
    }

    public string Display => _capturing ? "按下快捷键…" : HotkeyChord.Display(_chord);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CodecRow : INotifyPropertyChanged
{
    private bool _enabled;
    private int _index = 1;

    public CodecRow(InstalledCodec codec)
    {
        Codec = codec;
        _enabled = codec.Enabled && codec.Usable;
    }

    public InstalledCodec Codec { get; }
    public bool Usable => Codec.Usable;

    public string Title => $"{_index}. {Codec.Name}";

    public string Detail
    {
        get
        {
            var state = Codec.Usable ? "可以加载" : "不能加载，进程位数不同";
            var version = Codec.Version ?? "未知版本";
            return $"{Codec.BitnessText} · {version} · {state}\n{Codec.Path ?? "没有注册文件"}";
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            var next = value && Usable;
            if (_enabled == next)
                return;
            _enabled = next;
            OnChanged();
        }
    }

    public void SetIndex(int index)
    {
        if (_index == index)
            return;
        _index = index;
        OnChanged(nameof(Title));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
