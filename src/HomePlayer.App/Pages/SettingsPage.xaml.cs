using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HomePlayer.Core;
using HomePlayer.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed partial class SettingsPage : Page
{
    private readonly ObservableCollection<CodecRow> _splitters = [];
    private readonly ObservableCollection<CodecRow> _videos = [];
    private readonly ObservableCollection<CodecRow> _audios = [];
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var settings = App.Host.Settings;
        ApiKeyBox.Text = settings.TmdbApiKey ?? "";
        TraySwitch.IsOn = settings.CloseToTray;
        TitleBarBox.SelectedIndex = settings.TitleBar == TitleBarMode.Overlay ? 1 : 0;
        TitleBarBox.SelectionChanged += TitleBar_Changed;
        SplitterList.ItemsSource = _splitters;
        VideoList.ItemsSource = _videos;
        AudioList.ItemsSource = _audios;
        ReloadCodecs(settings);
        _loading = false;
        CacheText.Text = $"缓存占用 {Format(App.Host.Libraries.CacheBytes())}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.TmdbApiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Text) ? null : ApiKeyBox.Text.Trim();
        App.Host.Settings.CloseToTray = TraySwitch.IsOn;
        App.Host.Settings.TitleBar = TitleBarBox.SelectedIndex == 1 ? TitleBarMode.Overlay : TitleBarMode.Classic;
        WriteCodecs();
        App.Host.SaveSettings();
        App.Window.ApplyTitleBar();
    }

    private void TitleBar_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TitleBarBox.SelectedIndex < 0)
            return;
        App.Host.Settings.TitleBar = TitleBarBox.SelectedIndex == 1 ? TitleBarMode.Overlay : TitleBarMode.Classic;
        App.Host.SaveSettings();
        App.Window.ApplyTitleBar();
    }

    private void SplitterUp_Click(object sender, RoutedEventArgs e) => Move(_splitters, SplitterList, -1);

    private void SplitterDown_Click(object sender, RoutedEventArgs e) => Move(_splitters, SplitterList, 1);

    private void VideoUp_Click(object sender, RoutedEventArgs e) => Move(_videos, VideoList, -1);

    private void VideoDown_Click(object sender, RoutedEventArgs e) => Move(_videos, VideoList, 1);

    private void AudioUp_Click(object sender, RoutedEventArgs e) => Move(_audios, AudioList, -1);

    private void AudioDown_Click(object sender, RoutedEventArgs e) => Move(_audios, AudioList, 1);

    private void ResetCodecs_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        ReloadCodecs(new AppSettings());
        _loading = false;
        WriteCodecs();
        App.Host.SaveSettings();
    }

    private void CodecRow_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(CodecRow.Enabled))
            return;
        WriteCodecs();
        App.Host.SaveSettings();
    }

    private void ReloadCodecs(AppSettings settings)
    {
        var all = CodecCatalog.Scan();
        Fill(_splitters, CodecCatalog.Display(all, CodecKind.Splitter, settings.SplitterOrder));
        Fill(_videos, CodecCatalog.Display(all, CodecKind.VideoDecoder, settings.VideoDecoderOrder));
        Fill(_audios, CodecCatalog.Display(all, CodecKind.AudioDecoder, settings.AudioDecoderOrder));
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
        App.Host.SaveSettings();
    }

    private void WriteCodecs()
    {
        App.Host.Settings.SplitterOrder = ToPreferences(_splitters);
        App.Host.Settings.VideoDecoderOrder = ToPreferences(_videos);
        App.Host.Settings.AudioDecoderOrder = ToPreferences(_audios);
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
