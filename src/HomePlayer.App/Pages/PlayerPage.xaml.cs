using System.Globalization;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;
using HomePlayer.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class PlayerPage : Page
{
    private const string GlyphPlay = "\uE768";
    private const string GlyphPause = "\uE769";
    private const string GlyphMute = "\uE74F";
    private const string GlyphVolume0 = "\uE992";
    private const string GlyphVolume1 = "\uE993";
    private const string GlyphVolume2 = "\uE994";
    private const string GlyphVolume3 = "\uE995";
    private const string GlyphFullscreen = "\uE740";
    private const string GlyphExitFullscreen = "\uE73F";
    private const string GlyphRepeat = "\uE8EE";
    private const string GlyphRepeatOne = "\uE8ED";
    // 无边框播放时标题栏浮在内容之上，播放列表面板顶部要让出这条高度，
    // 否则面板标题行的「收起」会被标题栏盖住，点下去还会点到窗口按钮。
    private const double OverlayTitleBarHeight = 40;

    private readonly VideoPresenter _presenter;
    private SubtitleSession _subtitles = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherQueueTimer _saveSettingsTimer;
    private int _pipelineTicks;
    private bool _scrubbing;
    private bool _pointerDown;
    private bool _layingProgress;
    private double _scrubSeconds;
    private bool _haveAnchor;
    private bool _anchorPlaying;
    private TimeSpan _anchor;
    private long _anchorStamp;
    private string? _progressMedia;
    private bool _progressOpen;
    private bool _updatingVolume;
    private bool _alwaysOnTopSync;
    private bool _overlayChrome;
    private bool _fullscreenChrome;
    private bool _controlsVisible = true;
    private DateTime _controlsHoldUntil;
    private double _controlsHeight = 110;
    private bool _playlistBeforeFullscreen;
    private string? _statusHold;
    private DateTime _statusHoldUntil;
    private int _snapshotArmed;
    private string? _subtitlePath;
    private bool _subtitleOpen;
    private int _subtitleScan;
    private int _subtitleEmbed;
    private int _subtitleLoad;
    private string? _shownSubtitle;
    private (string? Path, string Engine)? _audioTracksKey;
    private bool _hasAudioTracks;
    private bool _windowEvents;

    public PlayerPage()
    {
        InitializeComponent();
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _presenter = new VideoPresenter(VideoSurface)
        {
            CanRender = () => App.Window.IsInteractive
        };
        VideoHost.SizeChanged += (_, _) =>
        {
            _presenter.Refresh();
            LayoutSubtitle();
        };
        var playback = App.Host.Playback;
        playback.FrameArrived += OnVideoFrame;
        playback.Changed += () => DispatcherQueue.TryEnqueue(UpdateChrome);
        playback.VideoSizeChanged += () => DispatcherQueue.TryEnqueue(() => FitWindow(false));
        playback.Stopped += () => DispatcherQueue.TryEnqueue(() =>
        {
            _presenter.Clear();
            SyncSnapshotButton();
        });
        PlaylistView.ItemsSource = playback.Playlist.Items;
        UpdateRepeatButton();
        SetShuffle(playback.Playlist.Shuffle);
        _updatingVolume = true;
        VolumeSlider.Value = playback.Volume * 100;
        _updatingVolume = false;
        PositionBar.SizeChanged += (_, _) => LayoutProgress(_scrubbing ? _scrubSeconds : ShownSeconds());
        ControlsBar.SizeChanged += (_, _) => RememberControlsHeight();
        _saveSettingsTimer = DispatcherQueue.CreateTimer();
        _saveSettingsTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveSettingsTimer.IsRepeating = false;
        _saveSettingsTimer.Tick += (_, _) =>
        {
            _saveSettingsTimer.Stop();
            App.Host.SaveSettings();
        };
        _timer.Tick += (_, _) =>
        {
            if (App.Host.Exiting)
                return;
            LayoutProgress(_scrubbing || _pointerDown ? _scrubSeconds : ShownSeconds());
            SyncSnapshotButton();
            UpdateSubtitleOverlay();
            if (_statusHold != null)
                ApplyStatusText();
            if (++_pipelineTicks % 50 == 0 && PipelinePanel.Visibility == Visibility.Visible)
                _ = RefreshPipelineAsync();
            AdjustTimerInterval();
        };
        Loaded += (_, _) =>
        {
            AttachWindowEvents();
            ApplyHotkeys();
            SetPlaylist(PlaylistButton.IsChecked == true);
            ApplyChromeLayout();
            if (_overlayChrome)
            {
                _controlsHoldUntil = DateTime.UtcNow.AddSeconds(2.5);
                SetControlsOpen(true);
            }
            UpdateChrome();
            SyncFullscreenButton();
            SyncAlwaysOnTopButton();
            SyncAspectMenu();
            LayoutProgress(ShownSeconds());
            _presenter.Resume();
            FitWindow(false);
            if (!_timer.IsEnabled)
                _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _presenter.Suspend();
            DetachWindowEvents();
        };
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        _presenter.Suspend();
        DetachWindowEvents();
        base.OnNavigatedFrom(e);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AttachWindowEvents();
        _presenter.Resume();
    }

    private void AttachWindowEvents()
    {
        if (_windowEvents)
            return;
        App.Window.FullscreenChanged += OnWindowFullscreenChanged;
        App.Window.OverlayTick += UpdateControlsOverlay;
        App.Window.SuspendRendering += _presenter.Suspend;
        App.Window.ResumeRendering += OnWindowResumeRendering;
        _windowEvents = true;
    }

    private void DetachWindowEvents()
    {
        if (!_windowEvents)
            return;
        App.Window.FullscreenChanged -= OnWindowFullscreenChanged;
        App.Window.OverlayTick -= UpdateControlsOverlay;
        App.Window.SuspendRendering -= _presenter.Suspend;
        App.Window.ResumeRendering -= OnWindowResumeRendering;
        _windowEvents = false;
    }

    private void OnWindowFullscreenChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyChromeLayout();
            SyncFullscreenButton();
            SyncAlwaysOnTopButton();
        });
    }

    private void OnWindowResumeRendering()
    {
        if (IsLoaded)
            _presenter.Resume();
    }

    private PlaybackController Playback => App.Host.Playback;

    private bool WantsOverlayChrome() =>
        App.Window.IsFullscreen || App.Host.Settings.TitleBar == TitleBarMode.Overlay;

    private void ApplyChromeLayout()
    {
        var overlay = WantsOverlayChrome();
        var fullscreen = App.Window.IsFullscreen;
        var overlayChanged = overlay != _overlayChrome;
        var fullscreenChanged = fullscreen != _fullscreenChrome;
        if (!overlayChanged && !fullscreenChanged)
            return;

        if (overlayChanged)
        {
            _overlayChrome = overlay;
            if (overlay)
            {
                Grid.SetRowSpan(Stage, 2);
                Grid.SetRow(ControlsBar, 0);
                Grid.SetRowSpan(ControlsBar, 2);
                ControlsBar.VerticalAlignment = VerticalAlignment.Bottom;
                ControlsRow.Height = new GridLength(0);
                ControlsBar.RequestedTheme = ElementTheme.Dark;
                ControlsBar.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A));
                PlaylistPane.Padding = new Thickness(0, OverlayTitleBarHeight, 0, 0);
                _controlsHoldUntil = DateTime.UtcNow.AddSeconds(2.5);
                _controlsVisible = false;
                SetControlsOpen(true);
            }
            else
            {
                Grid.SetRowSpan(Stage, 1);
                Grid.SetRow(ControlsBar, 1);
                Grid.SetRowSpan(ControlsBar, 1);
                ControlsBar.VerticalAlignment = VerticalAlignment.Stretch;
                ControlsRow.Height = GridLength.Auto;
                ControlsBar.RequestedTheme = ElementTheme.Default;
                ControlsBar.ClearValue(Panel.BackgroundProperty);
                PlaylistPane.Padding = new Thickness(0);
                _controlsHoldUntil = DateTime.MinValue;
                _controlsVisible = false;
                SetControlsOpen(true);
            }
        }

        if (fullscreen && !_fullscreenChrome)
        {
            _playlistBeforeFullscreen = PlaylistPane.Visibility == Visibility.Visible;
            SetPlaylist(false);
            PipelinePanel.Visibility = Visibility.Collapsed;
            PipelineButton.IsChecked = false;
        }
        else if (!fullscreen && _fullscreenChrome)
        {
            SetPlaylist(_playlistBeforeFullscreen);
        }

        _fullscreenChrome = fullscreen;
        ButtonsRow.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        ControlsBar.Padding = fullscreen ? new Thickness(16, 10, 16, 14) : new Thickness(12, 8, 12, 12);
        ControlsBar.RowSpacing = fullscreen ? 0 : 4;
        if (fullscreenChanged)
        {
            _controlsHoldUntil = DateTime.UtcNow.AddSeconds(2.5);
            SetControlsOpen(true);
        }

        ApplyChromeInsets();
        ControlsBar.UpdateLayout();
        RememberControlsHeight();
        Stage.UpdateLayout();
        _presenter.Refresh();
        FitWindow(false);
    }

    // 浮起的控件栏只盖视频区，不压住右侧播放列表，
    // 否则鼠标一碰列表底部就会把控件栏叫出来，还挡住列表自己的控件。
    private void ApplyChromeInsets()
    {
        var inset = 0.0;
        if (_overlayChrome && PlaylistPane.Visibility == Visibility.Visible)
            inset = PlaylistPane.ActualWidth > 1 ? PlaylistPane.ActualWidth : PlaylistPane.Width;
        ControlsBar.Margin = new Thickness(0, 0, Math.Max(0, inset), 0);
    }

    private void UpdateControlsOverlay()
    {
        ApplyChromeLayout();
        if (!_overlayChrome || !IsLoaded || Visibility != Visibility.Visible)
            return;
        if (_scrubbing || _pointerDown)
        {
            SetControlsOpen(true);
            return;
        }

        if (!App.Window.TryGetClientCursor(out var x, out var y, out var width, out var height, out _))
            return;

        RememberControlsHeight();
        var band = Math.Max(_controlsHeight, 1);
        var videoRight = width;
        if (PlaylistPane.Visibility == Visibility.Visible)
            videoRight -= PlaylistPane.ActualWidth > 1 ? PlaylistPane.ActualWidth : PlaylistPane.Width;
        // 热区只算视频区那一段：播放列表铺满右侧整列，鼠标停在列表上时不该唤出控件栏。
        var alongBottom = x >= -16
            && x <= videoRight + 1
            && y >= height - band
            && y <= height + 24;
        SetControlsOpen(alongBottom || DateTime.UtcNow < _controlsHoldUntil);
    }

    private void SetControlsOpen(bool open)
    {
        if (!_overlayChrome)
            open = true;
        if (open)
            RememberControlsHeight();
        if (_controlsVisible == open)
            return;
        if (_controlsVisible)
            RememberControlsHeight();
        _controlsVisible = open;
        ControlsBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            ControlsBar.UpdateLayout();
            RememberControlsHeight();
            LayoutProgress(_scrubbing || _pointerDown ? _scrubSeconds : ShownSeconds());
        }
    }

    private void RememberControlsHeight()
    {
        if (ControlsBar.ActualHeight > 1)
            _controlsHeight = ControlsBar.ActualHeight;
    }

    private void UpdateChrome()
    {
        PlayIcon.Glyph = Playback.IsPlaying ? GlyphPause : GlyphPlay;
        ApplyStatusText();
        if (PlaylistView.SelectedIndex != Playback.Playlist.CurrentIndex)
        {
            PlaylistView.SelectedIndex = Playback.Playlist.CurrentIndex;
            if (PlaylistView.SelectedItem != null)
                PlaylistView.ScrollIntoView(PlaylistView.SelectedItem);
        }
        MuteButton.IsChecked = Playback.Muted;
        UpdateVolumeIcon();
        UpdateSpeedChrome();
        UpdateAudioChrome();
        SyncSubtitleSource();
        UpdateSubtitleChrome();
        UpdateSubtitleOverlay();
        FitWindowButton.IsChecked = App.Host.Settings.FitWindow;
        SyncAlwaysOnTopButton();
        SyncSnapshotButton();
        PipelineButton.IsChecked = PipelinePanel.Visibility == Visibility.Visible;
        FfmpegItem.IsEnabled = Playback.Playlist.Current != null;
        StepFrameButton.IsEnabled = Playback.IsOpen;
    }

    private void ApplyStatusText()
    {
        if (_statusHold != null && DateTime.UtcNow < _statusHoldUntil)
        {
            StatusText.Text = _statusHold;
            return;
        }

        _statusHold = null;
        var name = Playback.Playlist.Current?.DisplayName;
        StatusText.Text = string.IsNullOrEmpty(name)
            ? $"{Playback.EngineLabel} · {Playback.Status}"
            : $"{name} · {Playback.EngineLabel} · {Playback.Status}";
    }

    private void ShowStatusHold(string message)
    {
        _statusHold = message;
        _statusHoldUntil = DateTime.UtcNow.AddSeconds(4);
        StatusText.Text = message;
    }

    private void SyncSnapshotButton()
    {
        if (SnapshotButton == null)
            return;
        SnapshotButton.IsEnabled = _presenter.HasLastFrame;
    }

    private void OnVideoFrame(VideoFrame frame)
    {
        _presenter.Submit(frame);
        if (!_presenter.HasLastFrame || Interlocked.Exchange(ref _snapshotArmed, 1) == 1)
            return;
        DispatcherQueue.TryEnqueue(SyncSnapshotButton);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Playback.TogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => Playback.Stop();
    private async void Next_Click(object sender, RoutedEventArgs e) => await Playback.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await Playback.PreviousAsync();
    private void SeekBack_Click(object sender, RoutedEventArgs e) =>
        Playback.Seek(Playback.Position - TimeSpan.FromSeconds(5));
    private void SeekForward_Click(object sender, RoutedEventArgs e) =>
        Playback.Seek(Playback.Position + TimeSpan.FromSeconds(5));
    private void StepFrame_Click(object sender, RoutedEventArgs e) => Playback.StepFrame();

    private void Position_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (Playback.Duration <= TimeSpan.Zero)
            return;
        var point = e.GetCurrentPoint(PositionBar);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        _pointerDown = true;
        _scrubbing = true;
        PositionBar.CapturePointer(e.Pointer);
        MoveScrub(point.Position.X);
        e.Handled = true;
    }

    private void Position_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown)
            return;
        MoveScrub(e.GetCurrentPoint(PositionBar).Position.X);
        e.Handled = true;
    }

    private void Position_Released(object sender, PointerRoutedEventArgs e)
    {
        FinishScrub();
        e.Handled = true;
    }

    private void MoveScrub(double x)
    {
        var width = PositionBar.ActualWidth;
        if (width <= 0 || Playback.Duration <= TimeSpan.Zero)
            return;
        var ratio = Math.Clamp(x / width, 0, 1);
        _scrubSeconds = ratio * Playback.Duration.TotalSeconds;
        LayoutProgress(_scrubSeconds);
    }

    private void FinishScrub()
    {
        if (!_scrubbing && !_pointerDown)
            return;
        var seconds = _scrubSeconds;
        _scrubbing = false;
        _pointerDown = false;
        PositionBar.ReleasePointerCaptures();
        Anchor(TimeSpan.FromSeconds(Math.Max(0, seconds)), Playback.IsPlaying);
        if (Playback.Duration <= TimeSpan.Zero)
            return;
        Playback.Seek(TimeSpan.FromSeconds(seconds));
        LayoutProgress(seconds);
    }

    private double ShownSeconds()
    {
        SyncProgressMedia();
        var duration = Playback.Duration;
        var engine = Playback.Position;
        var playing = Playback.IsPlaying;
        if (!_haveAnchor)
        {
            var initial = engine;
            if (duration > TimeSpan.FromSeconds(10) && engine >= duration - TimeSpan.FromSeconds(0.75))
                initial = TimeSpan.Zero;
            Anchor(initial, playing);
        }
        else if (playing != _anchorPlaying)
        {
            Anchor(engine, playing);
        }
        else
        {
            var predicted = Predicted();
            var stuckAtEnd = duration > TimeSpan.FromSeconds(10)
                && engine >= duration - TimeSpan.FromSeconds(0.75)
                && predicted < duration - TimeSpan.FromSeconds(3);
            if (!stuckAtEnd && (engine - predicted).Duration() < TimeSpan.FromSeconds(1.5))
                Anchor(engine, playing);
        }

        return Predicted().TotalSeconds;
    }

    private void SyncProgressMedia()
    {
        var path = Playback.Playlist.Current?.Path;
        var open = Playback.IsOpen;
        if (!string.Equals(path, _progressMedia, StringComparison.OrdinalIgnoreCase))
        {
            _progressMedia = path;
            _haveAnchor = false;
        }

        if (open == _progressOpen)
            return;
        _progressOpen = open;
        if (open)
            _haveAnchor = false;
    }

    private void Anchor(TimeSpan position, bool playing)
    {
        var duration = Playback.Duration;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;
        _anchor = position;
        _anchorPlaying = playing;
        _anchorStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _haveAnchor = true;
    }

    private TimeSpan Predicted()
    {
        var position = _anchor;
        if (_anchorPlaying)
        {
            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _anchorStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            position += TimeSpan.FromSeconds(Math.Max(0, elapsed));
        }

        var duration = Playback.Duration;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;
        return position;
    }

    private void LayoutProgress(double seconds)
    {
        if (_layingProgress)
            return;
        _layingProgress = true;
        try
        {
            var duration = Playback.Duration;
            var maximum = duration.TotalSeconds;
            var ratio = maximum > 0 ? Math.Clamp(seconds / maximum, 0, 1) : 0;
            var width = PositionBar.ActualWidth;
            var fill = width > 0 ? ratio * width : 0;
            PlayedColumn.Width = new GridLength(Math.Max(0, fill));
            var thumb = PositionThumb.Width > 0 ? PositionThumb.Width : 14;
            var left = width > 0 ? Math.Clamp(fill - thumb / 2, 0, Math.Max(0, width - thumb)) : 0;
            PositionThumb.Margin = new Thickness(left, 0, 0, 0);
            var shown = TimeSpan.FromSeconds(Math.Max(0, maximum > 0 ? seconds : 0));
            CurrentTimeText.Text = ClockText.Format(shown);
            DurationTimeText.Text = ClockText.Format(duration);
        }
        finally
        {
            _layingProgress = false;
        }
    }

    private void Volume_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVolumeIcon();
        if (_updatingVolume)
            return;
        Playback.SetVolume(VolumeSlider.Value / 100);
        _saveSettingsTimer.Stop();
        _saveSettingsTimer.Start();
    }

    private void UpdateVolumeIcon()
    {
        MuteIcon.Glyph = VolumeGlyph(Playback.Muted, VolumeSlider.Value);
    }

    private static string VolumeGlyph(bool muted, double volume)
    {
        if (muted)
            return GlyphMute;
        if (volume <= 0)
            return GlyphVolume0;
        if (volume < 34)
            return GlyphVolume1;
        if (volume < 67)
            return GlyphVolume2;
        return GlyphVolume3;
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        Playback.SetMuted(MuteButton.IsChecked == true);
    }

    private void Pipeline_Click(object sender, RoutedEventArgs e) => TogglePipeline();

    private async void Ffmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (Playback.Playlist.Current == null)
            return;
        await Playback.PlayPathsAsync(Playback.Playlist.Items.Select(item => item.Path).ToList(), Playback.Playlist.CurrentIndex, true);
    }

    private void OpenFlyout_Opening(object sender, object e) =>
        FfmpegItem.IsEnabled = Playback.Playlist.Current != null;

    private void Fullscreen_Click(object sender, RoutedEventArgs e) =>
        App.Window.SetFullscreen(!App.Window.IsFullscreen);

    private void SyncFullscreenButton()
    {
        var exit = App.Window.IsFullscreen;
        FullscreenIcon.Glyph = exit ? GlyphExitFullscreen : GlyphFullscreen;
        ApplyTip(FullscreenButton, exit ? "退出全屏" : "全屏", HotkeyCatalog.Fullscreen);
    }

    private void FitWindow_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.FitWindow = FitWindowButton.IsChecked == true;
        App.Host.SaveSettings();
        if (FitWindowButton.IsChecked == true)
            FitWindow(true);
    }

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e) =>
        ApplyAlwaysOnTop(AlwaysOnTopButton.IsChecked == true);

    private void Snapshot_Click(object sender, RoutedEventArgs e) => SaveSnapshot();

    private void SaveSnapshot()
    {
        if (!_presenter.TryCopyLastFrame(out var width, out var height, out var stride, out var bottomUp, out var data))
            return;
        _ = WriteSnapshotAsync(width, height, stride, bottomUp, data);
    }

    private async Task WriteSnapshotAsync(int width, int height, int stride, bool bottomUp, byte[] data)
    {
        try
        {
            var pixels = PackTopDownBgra(width, height, stride, bottomUp, data);
            var folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HomePlayer");
            Directory.CreateDirectory(folderPath);
            var title = SanitizeFileName(Playback.Playlist.Current?.DisplayName);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var position = ClockText.Format(Playback.Position).Replace(':', '-');
            var fileName = $"{title}_{stamp}_{position}.png";
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)width,
                (uint)height,
                96,
                96,
                pixels);
            await encoder.FlushAsync();
            var saved = file.Path;
            DispatcherQueue.TryEnqueue(() => ShowStatusHold(saved));
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatusHold("截图失败：" + ex.Message));
        }
    }

    private static byte[] PackTopDownBgra(int width, int height, int stride, bool bottomUp, byte[] data)
    {
        var rowBytes = width * 4;
        if (width < 1 || height < 1 || stride < rowBytes)
            throw new InvalidOperationException("没有可截取的画面");
        var packed = new byte[checked(rowBytes * height)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = bottomUp ? height - 1 - y : y;
            var source = sourceY * stride;
            if (source < 0 || source + rowBytes > data.Length)
                throw new InvalidOperationException("画面数据不完整");
            var dest = y * rowBytes;
            Buffer.BlockCopy(data, source, packed, dest, rowBytes);
            for (var i = dest + 3; i < dest + rowBytes; i += 4)
                packed[i] = 255;
        }

        return packed;
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "screenshot";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "screenshot" : cleaned;
    }

    private void AudioFlyout_Opening(object sender, object e) => RebuildAudioMenu();

    private void RebuildAudioMenu()
    {
        if (AudioFlyout == null)
            return;
        AudioFlyout.Items.Clear();
        var tracks = Playback.GetAudioTracks();
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var item = new RadioMenuFlyoutItem
            {
                Text = AudioTrackText(track, i + 1),
                GroupName = "AudioTrack",
                Tag = track.Index,
                IsChecked = track.Selected
            };
            item.Click += AudioItem_Click;
            AudioFlyout.Items.Add(item);
        }
    }

    private void AudioItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index })
            Playback.SetAudioTrack(index);
    }

    private void CycleAudioTrack()
    {
        var tracks = Playback.GetAudioTracks();
        if (tracks.Count == 0)
            return;
        var current = Playback.AudioTrackIndex;
        var position = -1;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Index == current)
            {
                position = i;
                break;
            }
        }

        var next = tracks[(position + 1) % tracks.Count];
        Playback.SetAudioTrack(next.Index);
    }

    private void UpdateAudioChrome()
    {
        if (AudioButton == null)
            return;
        // GetAudioTracks 在 DirectShow 引擎上要走 STA 线程枚举整张滤镜图。
        // Changed 在拖动音量时会连续触发，只在换片/换引擎时问一次就够了。
        var key = (Playback.Playlist.Current?.Path, Playback.EngineLabel);
        if (key != _audioTracksKey)
        {
            _audioTracksKey = key;
            _hasAudioTracks = Playback.GetAudioTracks().Count > 0;
        }

        AudioButton.IsEnabled = _hasAudioTracks;
        ApplyTip(AudioButton, _hasAudioTracks ? "音轨" : "没有音轨", HotkeyCatalog.CycleAudioTrack);
    }

    private static string AudioTrackText(MediaTrack track, int number)
    {
        var title = string.IsNullOrWhiteSpace(track.Title) ? $"音轨 {number}" : track.Title;
        return string.IsNullOrWhiteSpace(track.Language) ? title : $"{track.Language} · {title}";
    }

    private void SubtitleFlyout_Opening(object sender, object e) => RebuildSubtitleMenu();

    private void RebuildSubtitleMenu()
    {
        if (SubtitleFlyout == null)
            return;
        SubtitleFlyout.Items.Clear();
        SubtitleFlyout.Items.Add(SubtitleRadio("关闭", "off", _subtitles.OffChecked));
        SubtitleFlyout.Items.Add(SubtitleRadio("自动", "auto", _subtitles.AutoChecked));
        foreach (var path in _subtitles.MenuSidecars)
        {
            var name = Path.GetFileName(path);
            if (_subtitles.Unreadable(path))
                name += "（无法读取）";
            SubtitleFlyout.Items.Add(SubtitleRadio(name, "sidecar|" + path, _subtitles.SidecarChecked(path)));
        }

        for (var i = 0; i < _subtitles.Embedded.Count; i++)
        {
            var track = _subtitles.Embedded[i];
            var item = SubtitleRadio(SubtitleTrackText(track, i + 1), "embed|" + track.Index, _subtitles.EmbeddedChecked(track.Index));
            item.IsEnabled = track.IsText;
            SubtitleFlyout.Items.Add(item);
        }

        SubtitleFlyout.Items.Add(new MenuFlyoutSeparator());
        var open = new MenuFlyoutItem { Text = "打开字幕文件…" };
        open.Click += OpenSubtitleFile_Click;
        SubtitleFlyout.Items.Add(open);
    }

    private RadioMenuFlyoutItem SubtitleRadio(string text, string tag, bool selected)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "Subtitle",
            Tag = tag,
            IsChecked = selected
        };
        item.Click += SubtitleItem_Click;
        return item;
    }

    private void SubtitleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
            return;
        if (tag == "off")
            _subtitles.Off();
        else if (tag == "auto")
            _subtitles.Auto();
        else if (tag.StartsWith("sidecar|", StringComparison.Ordinal))
            _subtitles.UseSidecar(tag["sidecar|".Length..]);
        else if (tag.StartsWith("embed|", StringComparison.Ordinal)
            && int.TryParse(tag["embed|".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            if (!_subtitles.UseEmbedded(index))
                return;
        }
        else
            return;
        FinishSubtitleChoice();
    }

    private async void OpenSubtitleFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in SubtitleFiles.Extensions)
                picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return;
            _subtitles.UseSidecar(file.Path);
            FinishSubtitleChoice();
        }
        catch (Exception ex)
        {
            ShowStatusHold("无法打开字幕文件：" + ex.Message);
        }
    }

    private void CycleSubtitle()
    {
        _subtitles.Cycle();
        FinishSubtitleChoice();
    }

    private void ToggleSubtitle()
    {
        _subtitles.Toggle();
        FinishSubtitleChoice();
    }

    private void FinishSubtitleChoice()
    {
        if (!string.IsNullOrEmpty(_subtitles.Error) && !_subtitles.IsOff)
            ShowStatusHold(_subtitles.Error);
        UpdateSubtitleChrome();
        UpdateSubtitleOverlay();
        if (_subtitles.Mode == SubtitleSession.Choice.Embedded || _subtitles.AutoEmbedded != null)
            _ = LoadEmbeddedCues(_subtitles.EmbeddedIndex >= 0 ? _subtitles.EmbeddedIndex : _subtitles.AutoEmbedded!.Index);
    }

    private void SyncSubtitleSource()
    {
        var path = Playback.Playlist.Current?.Path;
        var open = Playback.IsOpen;
        if (path != _subtitlePath || open && !_subtitleOpen)
        {
            _subtitlePath = path;
            _ = LoadSubtitlesAsync(path, ++_subtitleLoad);
        }

        _subtitleOpen = open;
    }

    /// <summary>
    /// 解析外挂字幕要读文件、跑正则，放在 UI 线程上会让切换视频明显顿一下。
    /// </summary>
    private async Task LoadSubtitlesAsync(string? path, int token)
    {
        SubtitleSession session;
        try
        {
            session = await Task.Run(() =>
            {
                var next = new SubtitleSession();
                next.Load(path);
                return next;
            });
        }
        catch (Exception)
        {
            return;
        }

        if (token != _subtitleLoad || App.Host.Exiting)
            return;
        _subtitles = session;
        UpdateSubtitleChrome();
        UpdateSubtitleOverlay();
        if (session.VideoPath != null)
            _ = ScanEmbedded();
    }

    private async Task ScanEmbedded()
    {
        var token = ++_subtitleScan;
        var path = _subtitles.VideoPath;
        IReadOnlyList<FfmpegSubtitleStream> tracks = [];
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                tracks = await Task.Run(() => FfmpegSubtitles.List(path));
            }
            catch
            {
                tracks = [];
            }
        }

        if (token != _subtitleScan)
            return;
        _subtitles.SetEmbeddedList(tracks);
        if (_subtitles.AutoEmbedded != null)
            await LoadEmbeddedCues(_subtitles.AutoEmbedded.Index, token);
        UpdateSubtitleChrome();
        UpdateSubtitleOverlay();
    }

    private async Task LoadEmbeddedCues(int index, int? scanToken = null)
    {
        var path = _subtitles.VideoPath;
        if (string.IsNullOrEmpty(path))
            return;
        var embed = ++_subtitleEmbed;
        var scan = scanToken ?? _subtitleScan;
        try
        {
            var cues = await Task.Run(() => FfmpegSubtitles.ReadCues(path, index));
            if (embed != _subtitleEmbed || scan != _subtitleScan)
                return;
            if (_subtitles.EmbeddedIndex != index && _subtitles.AutoEmbedded?.Index != index)
                return;
            _subtitles.SetCues(cues, cues.Count == 0 ? "没有可显示的字幕" : null);
            if (_subtitles.Error != null)
                ShowStatusHold(_subtitles.Error);
        }
        catch (Exception ex)
        {
            if (embed != _subtitleEmbed || scan != _subtitleScan)
                return;
            _subtitles.SetCues([], "无法读取字幕：" + ex.Message);
            ShowStatusHold(_subtitles.Error ?? ex.Message);
        }

        UpdateSubtitleOverlay();
    }

    private void UpdateSubtitleChrome()
    {
        if (SubtitleButton == null)
            return;
        if (SubtitleIcon != null)
            SubtitleIcon.Opacity = _subtitles.IsOff ? 0.45 : 1;
        ApplyTip(SubtitleButton, "字幕", HotkeyCatalog.CycleSubtitle);
    }

    private void LayoutSubtitle()
    {
        if (SubtitlePill == null || SubtitleText == null || VideoHost == null)
            return;
        var height = VideoHost.ActualHeight;
        var width = VideoHost.ActualWidth;
        if (height < 1 || width < 1)
            return;
        SubtitleText.FontSize = Math.Clamp(28 * (height / 720.0), 18, 44);
        SubtitlePill.MaxWidth = Math.Max(160, width * 0.8);
        SubtitlePill.Margin = new Thickness(0, 0, 0, Math.Clamp(height * 0.06, 24, 80));
    }

    private void UpdateSubtitleOverlay()
    {
        if (SubtitleLayer == null || SubtitleText == null)
            return;
        LayoutSubtitle();
        string? text = null;
        if (Playback.IsOpen && !_subtitles.IsOff)
            text = _subtitles.TextAt(Playback.Position);
        if (string.IsNullOrEmpty(text))
        {
            if (SubtitleLayer.Visibility != Visibility.Collapsed)
                SubtitleLayer.Visibility = Visibility.Collapsed;
            if (SubtitleText.Text.Length > 0)
                SubtitleText.Text = "";
            _shownSubtitle = null;
            return;
        }

        if (text != _shownSubtitle)
        {
            _shownSubtitle = text;
            SubtitleText.Text = text;
        }

        if (SubtitleLayer.Visibility != Visibility.Visible)
            SubtitleLayer.Visibility = Visibility.Visible;
    }

    private static string SubtitleTrackText(FfmpegSubtitleStream track, int number)
    {
        var title = string.IsNullOrWhiteSpace(track.Title) ? $"字幕 {number}" : track.Title;
        if (!track.IsText)
            title += "（位图）";
        return string.IsNullOrWhiteSpace(track.Language) ? title : $"{track.Language} · {title}";
    }

    private void SpeedFlyout_Opening(object sender, object e) => SyncSpeedMenu();

    private void SpeedItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }
            && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            ApplyPlaybackRate(rate);
    }

    private void ApplyPlaybackRate(double rate)
    {
        Playback.SetRate(rate);
        UpdateSpeedChrome();
        if (Playback.Status == "当前滤镜不支持倍速")
            ShowStatusHold("当前滤镜不支持倍速");
    }

    private void UpdateSpeedChrome()
    {
        if (SpeedText == null || SpeedButton == null)
            return;
        SpeedText.Text = PlaybackRates.Label(Playback.Rate);
        ApplyTip(SpeedButton, "倍速", HotkeyCatalog.CycleSpeed);
        SyncSpeedMenu();
    }

    private void SyncSpeedMenu()
    {
        if (SpeedFlyout == null)
            return;
        var rate = Playback.Rate;
        foreach (var item in SpeedFlyout.Items.OfType<RadioMenuFlyoutItem>())
        {
            if (item.Tag is string tag
                && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                item.IsChecked = Math.Abs(value - rate) < 0.001;
        }
    }

    private void AspectFlyout_Opening(object sender, object e) => SyncAspectMenu();

    private void AspectItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;
        SetAspectMode(AspectFromTag(element.Tag));
    }

    private void SetAspectMode(AspectMode mode)
    {
        _presenter.AspectMode = mode;
        SyncAspectMenu();
    }

    private void SyncAspectMenu()
    {
        if (AspectFitItem == null)
            return;
        var mode = _presenter.AspectMode;
        AspectFitItem.IsChecked = mode == AspectMode.Fit;
        AspectFillItem.IsChecked = mode == AspectMode.Fill;
        AspectStretchItem.IsChecked = mode == AspectMode.Stretch;
        Aspect16x9Item.IsChecked = mode == AspectMode.Ratio16x9;
        Aspect4x3Item.IsChecked = mode == AspectMode.Ratio4x3;
    }

    private static AspectMode AspectFromTag(object? tag) => tag switch
    {
        "Fill" => AspectMode.Fill,
        "Stretch" => AspectMode.Stretch,
        "Ratio16x9" => AspectMode.Ratio16x9,
        "Ratio4x3" => AspectMode.Ratio4x3,
        _ => AspectMode.Fit
    };

    private void ApplyAlwaysOnTop(bool on)
    {
        if (_alwaysOnTopSync)
            return;
        App.Host.Settings.AlwaysOnTop = on;
        App.Host.SaveSettings();
        App.Window.SetAlwaysOnTop(on);
        SyncAlwaysOnTopButton();
    }

    private void SyncAlwaysOnTopButton()
    {
        if (AlwaysOnTopButton == null)
            return;
        var on = App.Host.Settings.AlwaysOnTop;
        _alwaysOnTopSync = true;
        AlwaysOnTopButton.IsChecked = on;
        _alwaysOnTopSync = false;
        if (AlwaysOnTopIcon != null)
            AlwaysOnTopIcon.Opacity = on ? 1 : 0.45;
    }

    private void FitWindow(bool force)
    {
        if (!force && !App.Host.Settings.FitWindow)
            return;
        if (!IsLoaded || Visibility != Visibility.Visible || !App.Window.IsInteractive)
            return;
        App.Window.FitToVideo(Playback.VideoWidth, Playback.VideoHeight, VideoHost);
    }

    private void PlaylistButton_Click(object sender, RoutedEventArgs e) =>
        SetPlaylist(PlaylistButton.IsChecked == true);

    private void CollapsePlaylist_Click(object sender, RoutedEventArgs e) => SetPlaylist(false);

    private void SetPlaylist(bool open)
    {
        PlaylistPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PlaylistButton.IsChecked = open;
        Stage.UpdateLayout();
        ApplyChromeInsets();
        ControlsBar.UpdateLayout();
        _presenter.Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Playback.Stop();
        Playback.Playlist.Clear();
    }

    private void Repeat_Click(object sender, RoutedEventArgs e) =>
        SetRepeat(NextRepeat(Playback.Playlist.Repeat));

    private void SetRepeat(RepeatMode mode)
    {
        Playback.Playlist.Repeat = mode;
        UpdateRepeatButton();
    }

    private static RepeatMode NextRepeat(RepeatMode mode) => mode switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off
    };

    private void UpdateRepeatButton()
    {
        if (RepeatIcon == null || RepeatButton == null)
            return;
        var mode = Playback.Playlist.Repeat;
        var glyph = mode == RepeatMode.One ? GlyphRepeatOne : GlyphRepeat;
        var opacity = mode == RepeatMode.Off ? 0.45 : 1;
        RepeatIcon.Glyph = glyph;
        RepeatIcon.Opacity = opacity;
        ApplyTip(RepeatButton, RepeatTitle(mode), HotkeyCatalog.CycleRepeat);
        if (PlaylistRepeatIcon != null)
        {
            PlaylistRepeatIcon.Glyph = glyph;
            PlaylistRepeatIcon.Opacity = opacity;
        }
        if (PlaylistRepeatText != null)
            PlaylistRepeatText.Text = RepeatTitle(mode);
    }

    private static string RepeatTitle(RepeatMode mode) => mode switch
    {
        RepeatMode.All => "循环全部",
        RepeatMode.One => "循环单条",
        _ => "播完停止"
    };

    private void Shuffle_Click(object sender, RoutedEventArgs e) =>
        SetShuffle(sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle && toggle.IsChecked == true);

    private void SetShuffle(bool on)
    {
        Playback.Playlist.Shuffle = on;
        if (ShuffleButton != null)
        {
            ShuffleButton.IsChecked = on;
            ShuffleIcon.Opacity = on ? 1 : 0.45;
        }
        if (PlaylistShuffleButton != null)
        {
            PlaylistShuffleButton.IsChecked = on;
            PlaylistShuffleIcon.Opacity = on ? 1 : 0.45;
        }
    }

    private async void Playlist_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PlaylistItem item)
            return;
        var index = Playback.Playlist.Items.IndexOf(item);
        if (index < 0)
            return;
        await Playback.PlayIndexAsync(index);
    }

    private void Playlist_Reordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (Playback.Playlist.Current != null)
            Playback.Playlist.CurrentIndex = Playback.Playlist.Items.IndexOf(Playback.Playlist.Current);
        Playback.Playlist.RefreshShuffleOrder();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in VideoFiles.Extensions)
                picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0)
                return;
            await Playback.PlayPathsAsync(files.Select(file => file.Path).ToList(), 0);
        }
        catch (Exception)
        {
        }
    }

    private void VideoHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        App.Window.SetFullscreen(!App.Window.IsFullscreen);
        e.Handled = true;
    }

    private void ApplyHotkeys()
    {
        KeyboardAccelerators.Clear();
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        foreach (var (id, text) in App.Host.Settings.Hotkeys)
        {
            if (!HotkeyChord.TryParse(text, out var key, out var modifiers))
                continue;
            var action = id;
            var accel = new KeyboardAccelerator
            {
                Key = key,
                Modifiers = modifiers
            };
            accel.Invoked += (_, args) =>
            {
                RunHotkey(action);
                args.Handled = true;
            };
            KeyboardAccelerators.Add(accel);
        }

        ApplyControlTips();
    }

    private void ApplyControlTips()
    {
        ApplyTip(OpenButton, "打开", HotkeyCatalog.OpenFiles);
        ApplyTip(OpenFilesItem, "打开文件", HotkeyCatalog.OpenFiles);
        ApplyTip(FfmpegItem, "用 FFmpeg 打开当前项", HotkeyCatalog.PlayWithFfmpeg);
        ApplyTip(StopButton, "停止", HotkeyCatalog.Stop);
        ApplyTip(PreviousButton, "上一个", HotkeyCatalog.Previous);
        ApplyTip(SeekBackButton, "后退 5 秒", HotkeyCatalog.SeekBack);
        ApplyTip(PlayButton, "播放/暂停", HotkeyCatalog.PlayPause);
        ApplyTip(SeekForwardButton, "前进 5 秒", HotkeyCatalog.SeekForward);
        ApplyTip(NextButton, "下一个", HotkeyCatalog.Next);
        ApplyTip(StepFrameButton, "下一帧", HotkeyCatalog.StepFrame);
        ApplyTip(MuteButton, "静音", HotkeyCatalog.Mute);
        UpdateSpeedChrome();
        UpdateAudioChrome();
        UpdateSubtitleChrome();
        UpdateRepeatButton();
        ApplyTip(ShuffleButton, "随机", HotkeyCatalog.ToggleShuffle);
        ApplyTip(SnapshotButton, "截图", HotkeyCatalog.Snapshot);
        ApplyTip(FitWindowButton, "自适应窗口", HotkeyCatalog.FitWindow);
        ApplyTip(AlwaysOnTopButton, "置顶", HotkeyCatalog.ToggleAlwaysOnTop);
        ApplyTip(AspectButton, "画面比例", HotkeyCatalog.CycleAspect);
        ApplyTip(PlaylistButton, "播放列表", HotkeyCatalog.TogglePlaylist);
        ApplyTip(PipelineButton, "管线信息", HotkeyCatalog.TogglePipeline);
        SyncFullscreenButton();
    }

    private static void ApplyTip(FrameworkElement element, string title, string id)
    {
        AutomationProperties.SetName(element, title);
        ToolTipService.SetToolTip(element, HotkeyTip(title, id));
    }

    private static string HotkeyTip(string title, string id)
    {
        if (!App.Host.Settings.Hotkeys.TryGetValue(id, out var chord) || string.IsNullOrWhiteSpace(chord))
            return title;
        return $"{title} ({chord})";
    }

    private void RunHotkey(string action)
    {
        switch (action)
        {
            case HotkeyCatalog.PlayPause:
                Playback.TogglePlay();
                break;
            case HotkeyCatalog.Stop:
                Playback.Stop();
                break;
            case HotkeyCatalog.Previous:
                _ = Playback.PreviousAsync();
                break;
            case HotkeyCatalog.Next:
                _ = Playback.NextAsync();
                break;
            case HotkeyCatalog.SeekBack:
                Playback.Seek(Playback.Position - TimeSpan.FromSeconds(5));
                break;
            case HotkeyCatalog.SeekForward:
                Playback.Seek(Playback.Position + TimeSpan.FromSeconds(5));
                break;
            case HotkeyCatalog.StepFrame:
                Playback.StepFrame();
                break;
            case HotkeyCatalog.VolumeUp:
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                break;
            case HotkeyCatalog.VolumeDown:
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                break;
            case HotkeyCatalog.Mute:
                Playback.SetMuted(!Playback.Muted);
                MuteButton.IsChecked = Playback.Muted;
                break;
            case HotkeyCatalog.Fullscreen:
                App.Window.SetFullscreen(!App.Window.IsFullscreen);
                break;
            case HotkeyCatalog.Escape:
                if (App.Window.IsFullscreen)
                    App.Window.SetFullscreen(false);
                else if (PipelinePanel.Visibility == Visibility.Visible)
                    TogglePipeline();
                break;
            case HotkeyCatalog.TogglePlaylist:
                SetPlaylist(PlaylistPane.Visibility != Visibility.Visible);
                break;
            case HotkeyCatalog.TogglePipeline:
                TogglePipeline();
                break;
            case HotkeyCatalog.OpenFiles:
                Open_Click(this, new RoutedEventArgs());
                break;
            case HotkeyCatalog.PlayWithFfmpeg:
                Ffmpeg_Click(this, new RoutedEventArgs());
                break;
            case HotkeyCatalog.ToggleShuffle:
                SetShuffle(!Playback.Playlist.Shuffle);
                break;
            case HotkeyCatalog.CycleRepeat:
                SetRepeat(NextRepeat(Playback.Playlist.Repeat));
                break;
            case HotkeyCatalog.CycleSpeed:
                ApplyPlaybackRate(PlaybackRates.Next(Playback.Rate));
                break;
            case HotkeyCatalog.CycleAudioTrack:
                CycleAudioTrack();
                break;
            case HotkeyCatalog.CycleSubtitle:
                CycleSubtitle();
                break;
            case HotkeyCatalog.ToggleSubtitle:
                ToggleSubtitle();
                break;
            case HotkeyCatalog.FitWindow:
                FitWindow(true);
                break;
            case HotkeyCatalog.CycleAspect:
                SetAspectMode(VideoAspect.Next(_presenter.AspectMode));
                break;
            case HotkeyCatalog.ToggleAlwaysOnTop:
                ApplyAlwaysOnTop(!App.Host.Settings.AlwaysOnTop);
                break;
            case HotkeyCatalog.Snapshot:
                SaveSnapshot();
                break;
        }
    }

    private void TogglePipeline()
    {
        var open = PipelinePanel.Visibility != Visibility.Visible;
        PipelinePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PipelineButton.IsChecked = open;
        if (open)
            _ = RefreshPipelineAsync();
    }

    /// <summary>
    /// 枚举 DirectShow 滤镜图必须在 STA 线程上做，会阻塞调用线程。
    /// 放到线程池里调用，UI 线程就不会为了一次读管线信息卡住。
    /// </summary>
    private async Task RefreshPipelineAsync()
    {
        try
        {
            var text = await Task.Run(() => Playback.Pipeline()?.Format() ?? "当前没有播放。");
            if (!App.Host.Exiting)
                PipelineText.Text = text;
        }
        catch (Exception ex)
        {
            if (!App.Host.Exiting)
                PipelineText.Text = "读取管线信息失败：" + ex.Message;
        }
    }

    private void AdjustTimerInterval()
    {
        var interval = Playback.IsPlaying ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(250);
        if (_timer.Interval != interval)
            _timer.Interval = interval;
    }
}
