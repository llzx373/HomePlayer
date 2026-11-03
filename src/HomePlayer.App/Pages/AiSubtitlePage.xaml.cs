using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HomePlayer.AI;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class AiSubtitlePage : Page
{
    private readonly string _mediaPath;
    private readonly IReadOnlyList<MediaTrack> _audioTracks;
    private readonly int _selectedAudioTrack;
    private readonly ObservableCollection<AiCueRow> _rows = [];
    private readonly List<AiSourceItem> _sources = [];
    private CancellationTokenSource? _task;
    private AiSubtitleResult? _result;

    public event Action<string>? SubtitleSelected;

    public AiSubtitlePage(
        string mediaPath,
        IReadOnlyList<MediaTrack> audioTracks,
        int selectedAudioTrack,
        IReadOnlyList<string> sidecars,
        IReadOnlyList<FfmpegSubtitleStream> embedded)
    {
        _mediaPath = mediaPath;
        _audioTracks = audioTracks;
        _selectedAudioTrack = selectedAudioTrack;
        InitializeComponent();
        MediaText.Text = Path.GetFileName(mediaPath);
        EnableSwitch.IsOn = App.Host.Settings.AiSubtitlesEnabled;
        MirrorBox.Text = App.Host.Settings.AiModelMirror;
        ProxyBox.Text = App.Host.Settings.AiDownloadProxy;
        BilingualCheck.IsChecked = App.Host.Settings.AiBilingualSubtitles;
        CueList.ItemsSource = _rows;
        foreach (var path in sidecars)
            _sources.Add(AiSourceItem.Sidecar(path));
        foreach (var track in embedded.Where(track => track.IsText))
            _sources.Add(AiSourceItem.Embedded(track));
        _sources.Add(AiSourceItem.Audio());
        SourceBox.ItemsSource = _sources;
        SourceBox.SelectedIndex = 0;
        var sourceLanguages = new List<AiLanguage> { new("", "自动检测") };
        sourceLanguages.AddRange(AiSubtitleCatalog.Languages);
        SourceLanguageBox.ItemsSource = sourceLanguages;
        SourceLanguageBox.SelectedIndex = 0;
        TargetLanguageBox.ItemsSource = AiSubtitleCatalog.Languages;
        TargetLanguageBox.SelectedIndex = Math.Max(0, AiSubtitleCatalog.Languages.ToList().FindIndex(
            language => language.Code == App.Host.Settings.AiTargetLanguage));
        RecognitionModelBox.ItemsSource = AiSubtitleCatalog.Models.Where(model => model.Kind == AiModelKind.Recognition).ToArray();
        RecognitionModelBox.SelectedItem = AiSubtitleCatalog.Models.FirstOrDefault(model => model.Id == App.Host.Settings.AiRecognitionModel)
            ?? AiSubtitleCatalog.Models.First(model => model.Kind == AiModelKind.Recognition);
        TranslationModelBox.ItemsSource = AiSubtitleCatalog.Models.Where(model => model.Kind == AiModelKind.Translation).ToArray();
        TranslationModelBox.SelectedItem = AiSubtitleCatalog.Models.FirstOrDefault(model => model.Id == App.Host.Settings.AiTranslationModel)
            ?? AiSubtitleCatalog.Models.First(model => model.Kind == AiModelKind.Translation);
        DeviceBox.ItemsSource = AiDeviceOption.Items;
        DeviceBox.SelectedItem = AiDeviceOption.Items.FirstOrDefault(item => item.Device == AiComputeDeviceExtensions.Parse(App.Host.Settings.AiComputeDevice))
            ?? AiDeviceOption.Items[0];
        EngineBox.ItemsSource = AiEngineOption.Items;
        EngineBox.SelectedItem = App.Host.Settings.AiRecognitionEngine == "whisper" ? AiEngineOption.Items[1] : AiEngineOption.Items[0];
        LiveSwitch.IsOn = App.Host.Settings.AiLiveSubtitles;
        var tracks = audioTracks.Select((track, index) => new AiAudioTrackItem(track, index)).ToArray();
        AudioTrackBox.ItemsSource = tracks;
        AudioTrackBox.SelectedItem = tracks.FirstOrDefault(item => item.Track.Index == selectedAudioTrack) ?? tracks.FirstOrDefault();
        TranslateAfterSwitch.IsOn = false;
        UpdateSourceLayout();
    }

    private AiSubtitleService Service => App.Host.AiSubtitles;
    private AiComputeDevice SelectedDevice => (DeviceBox.SelectedItem as AiDeviceOption)?.Device ?? AiComputeDevice.Auto;
    private bool UseSherpa => (EngineBox.SelectedItem as AiEngineOption)?.UseSherpa != false;

    private void Source_Changed(object sender, SelectionChangedEventArgs e) => UpdateSourceLayout();
    private void Engine_Changed(object sender, SelectionChangedEventArgs e) => UpdateSourceLayout();

    private void TranslateAfter_Toggled(object sender, RoutedEventArgs e) => UpdateSourceLayout();

    public void CancelTask() => _task?.Cancel();

    private void UpdateSourceLayout()
    {
        var audio = SelectedSource?.Kind == AiSourceKind.Audio;
        RecognitionModelBox.Visibility = audio && !UseSherpa ? Visibility.Visible : Visibility.Collapsed;
        EngineBox.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        LiveSwitch.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        AudioTrackBox.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        TranslateAfterSwitch.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        TranslationModelBox.Visibility = !audio || TranslateAfterSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        TargetLanguageBox.Visibility = TranslationModelBox.Visibility;
        BilingualCheck.Visibility = TranslationModelBox.Visibility;
    }

    private AiSourceItem? SelectedSource => SourceBox.SelectedItem as AiSourceItem;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_task != null)
            return;
        SavePreferences();
        if (!EnableSwitch.IsOn)
        {
            StatusText.Text = "请先启用右上角“本地 AI 字幕”。";
            return;
        }
        if (SelectedSource == null)
            return;
        var source = SelectedSource;
        if (source.Kind == AiSourceKind.Audio && AudioTrackBox.SelectedItem is not AiAudioTrackItem)
        {
            StatusText.Text = "没有可用的识别音轨。已有文本字幕可直接翻译，不需要音轨。";
            return;
        }
        _task = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        ConfigurationPanel.IsEnabled = false;
        EnableSwitch.IsEnabled = false;
        CancelButton.IsEnabled = true;
        UseButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        TaskProgress.Value = 0;
        try
        {
            var progress = new Progress<AiSubtitleProgress>(value =>
            {
                StatusText.Text = value.Stage;
                TaskProgress.Value = value.Value * 1000;
            });
            IReadOnlyList<SubtitleCue> cues;
            string description;
            if (source.Kind == AiSourceKind.Audio)
            {
                var audio = (AiAudioTrackItem)AudioTrackBox.SelectedItem;
                var model = (AiModelInfo)RecognitionModelBox.SelectedItem;
                cues = UseSherpa
                    ? await Service.RecognizeWithSherpaAsync(_mediaPath, audio.Track.Index, SelectedDevice, 0, progress, null, _task.Token)
                    : await Service.RecognizeAsync(_mediaPath, audio.Track.Index,
                        ((AiLanguage)SourceLanguageBox.SelectedItem).Code, model.Id, SelectedDevice, progress, _task.Token);
                description = UseSherpa ? "Sherpa 识别" : "Whisper 识别";
                if (TranslateAfterSwitch.IsOn)
                    cues = await Translate(cues, progress);
            }
            else
            {
                StatusText.Text = "读取已有文本字幕";
                cues = await Task.Run(() => source.Kind == AiSourceKind.Sidecar
                    ? SubtitleText.ParseFile(source.Path!)
                    : FfmpegSubtitles.ReadCues(_mediaPath, source.StreamIndex), _task.Token);
                cues = await Translate(cues, progress);
                description = "AI 翻译";
            }

            var target = TranslateAfterSwitch.IsOn || source.Kind != AiSourceKind.Audio
                ? ((AiLanguage)TargetLanguageBox.SelectedItem).DisplayName
                : "原文";
            description += $" · {target}";
            _result = Service.Save(_mediaPath, cues, description);
            ShowResult(cues);
            TaskProgress.Value = 1000;
            StatusText.Text = $"完成：{description}，{cues.Count} 条字幕。可校对、导出或在当前影片中使用。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "任务已取消，原字幕和播放不受影响。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "生成失败：" + ex.Message;
            CrashLog.Write("AI 字幕任务失败", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _task.Dispose();
            _task = null;
            StartButton.IsEnabled = true;
            ConfigurationPanel.IsEnabled = true;
            EnableSwitch.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private Task<IReadOnlyList<SubtitleCue>> Translate(IReadOnlyList<SubtitleCue> cues, IProgress<AiSubtitleProgress> progress)
    {
        var model = (AiModelInfo)TranslationModelBox.SelectedItem;
        return Service.TranslateAsync(cues,
            ((AiLanguage)SourceLanguageBox.SelectedItem).Code,
            ((AiLanguage)TargetLanguageBox.SelectedItem).Code,
            BilingualCheck.IsChecked == true, model.Id, SelectedDevice, progress, _task!.Token);
    }

    private void ShowResult(IReadOnlyList<SubtitleCue> cues)
    {
        _rows.Clear();
        foreach (var cue in cues)
            _rows.Add(new AiCueRow(cue));
        EmptyResultText.Visibility = Visibility.Collapsed;
        CueList.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        UseButton.IsEnabled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _task?.Cancel();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SaveEdits())
            StatusText.Text = "修改已保存。";
    }

    private bool SaveEdits()
    {
        if (_result == null)
            return false;
        try
        {
            Service.SaveEdits(_result, _rows.Select(row => row.Cue).ToArray());
            _result = _result with { Cues = _rows.Select(row => row.Cue).ToArray() };
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
            return false;
        }
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || !SaveEdits())
            return;
        SubtitleSelected?.Invoke(_result.Path);
        StatusText.Text = "已在当前影片中启用，可随时从字幕菜单切回原字幕。";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || !SaveEdits())
            return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(_mediaPath) + ".ai" };
            picker.FileTypeChoices.Add("SRT 字幕", [".srt"]);
            picker.FileTypeChoices.Add("WebVTT 字幕", [".vtt"]);
            picker.FileTypeChoices.Add("纯文本", [".txt"]);
            picker.FileTypeChoices.Add("JSON 数据", [".json"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
            var file = await picker.PickSaveFileAsync();
            if (file == null)
                return;
            var cues = _rows.Select(row => row.Cue).ToArray();
            switch (Path.GetExtension(file.Path).ToLowerInvariant())
            {
                case ".vtt": SubtitleFileWriter.WriteVtt(file.Path, cues); break;
                case ".txt": SubtitleFileWriter.WriteText(file.Path, cues); break;
                case ".json":
                    var json = JsonSerializer.Serialize(cues.Select(cue => new
                    {
                        start = cue.Start.ToString(),
                        end = cue.End.ToString(),
                        text = cue.Text
                    }), new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(file.Path, json);
                    break;
                default: SubtitleFileWriter.WriteSrt(file.Path, cues); break;
            }
            StatusText.Text = "已导出到 " + file.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private void SavePreferences()
    {
        App.Host.Settings.AiSubtitlesEnabled = EnableSwitch.IsOn;
        App.Host.Settings.AiModelMirror = MirrorBox.Text.Trim();
        App.Host.Settings.AiDownloadProxy = ProxyBox.Text.Trim();
        App.Host.Settings.AiComputeDevice = SelectedDevice.ToString().ToLowerInvariant();
        App.Host.Settings.AiRecognitionEngine = UseSherpa ? "sherpa" : "whisper";
        App.Host.Settings.AiLiveSubtitles = LiveSwitch.IsOn;
        App.Host.Settings.AiRecognitionModel = (RecognitionModelBox.SelectedItem as AiModelInfo)?.Id ?? "whisper-small";
        App.Host.Settings.AiTranslationModel = (TranslationModelBox.SelectedItem as AiModelInfo)?.Id ?? "qwen3-4b-q4";
        App.Host.Settings.AiTargetLanguage = (TargetLanguageBox.SelectedItem as AiLanguage)?.Code ?? "zh";
        App.Host.Settings.AiBilingualSubtitles = BilingualCheck.IsChecked == true;
        App.Host.SaveSettings();
    }
}

public sealed class AiCueRow : INotifyPropertyChanged
{
    private string _text;

    public AiCueRow(SubtitleCue cue)
    {
        Start = cue.Start;
        End = cue.End;
        _text = cue.Text;
    }

    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public string TimeRange => $"{Start:hh\\:mm\\:ss\\.fff} → {End:hh\\:mm\\:ss\\.fff}";
    public SubtitleCue Cue => new(Start, End, _text);

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum AiSourceKind
{
    Sidecar,
    Embedded,
    Audio
}

public sealed record AiSourceItem(AiSourceKind Kind, string DisplayName, string? Path, int StreamIndex)
{
    public static AiSourceItem Sidecar(string path) => new(AiSourceKind.Sidecar, "翻译外挂字幕：" + System.IO.Path.GetFileName(path), path, -1);
    public static AiSourceItem Embedded(FfmpegSubtitleStream track) => new(AiSourceKind.Embedded,
        $"翻译内嵌字幕：{(string.IsNullOrWhiteSpace(track.Title) ? track.Language ?? $"轨道 {track.Index}" : track.Title)}", null, track.Index);
    public static AiSourceItem Audio() => new(AiSourceKind.Audio, "从音频重新识别（无文本字幕时使用）", null, -1);
}

public sealed record AiAudioTrackItem(MediaTrack Track, int Number)
{
    public string DisplayName
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Track.Title) ? $"音轨 {Number + 1}" : Track.Title;
            return string.IsNullOrWhiteSpace(Track.Language) ? title : Track.Language + " · " + title;
        }
    }
}

public sealed record AiDeviceOption(AiComputeDevice Device, string DisplayName)
{
    public static IReadOnlyList<AiDeviceOption> Items { get; } =
    [
        new(AiComputeDevice.Auto, "自动（优先 NVIDIA CUDA）"),
        new(AiComputeDevice.Cuda, "NVIDIA CUDA GPU"),
        new(AiComputeDevice.Cpu, "CPU（兼容回退）")
    ];
}

public sealed record AiEngineOption(bool UseSherpa, string DisplayName)
{
    public static IReadOnlyList<AiEngineOption> Items { get; } =
    [
        new(true, "Sherpa-ONNX SenseVoice（英/日/韩，低延迟）"),
        new(false, "Whisper（多语言）")
    ];
}
