using HomePlayer.Core;
using HomePlayer.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class PlayerPage : Page
{
    private readonly VideoPresenter _presenter;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
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
    private bool _updatingVolume;
    private bool _playlistSync;
    private bool _overlayChrome;
    private bool _fullscreenChrome;
    private bool _controlsVisible = true;
    private DateTime _controlsHoldUntil;
    private double _controlsHeight = 110;
    private bool _playlistBeforeFullscreen;

    public PlayerPage()
    {
        InitializeComponent();
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _presenter = new VideoPresenter(VideoSurface);
        VideoHost.SizeChanged += (_, _) => _presenter.Refresh();
        var playback = App.Host.Playback;
        playback.FrameArrived += _presenter.Submit;
        playback.Changed += () => DispatcherQueue.TryEnqueue(UpdateChrome);
        App.Window.FullscreenChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyChromeLayout();
            SyncFullscreenButton();
        });
        App.Window.OverlayTick += UpdateControlsOverlay;
        PlaylistView.ItemsSource = playback.Playlist.Items;
        RepeatBox.SelectedIndex = (int)playback.Playlist.Repeat;
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
            LayoutProgress(_scrubbing || _pointerDown ? _scrubSeconds : ShownSeconds());
            if (++_pipelineTicks % 5 == 0 && PipelinePanel.Visibility == Visibility.Visible)
                UpdatePipelineText();
        };
        Loaded += (_, _) =>
        {
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
            LayoutProgress(ShownSeconds());
            _presenter.Refresh();
            if (!_timer.IsEnabled)
                _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
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

        ControlsBar.UpdateLayout();
        RememberControlsHeight();
        Stage.UpdateLayout();
        _presenter.Refresh();
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
        var alongBottom = x >= -16 && x <= width + 16
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
        PlayButton.Content = Playback.IsPlaying ? "暂停" : "播放";
        var name = Playback.Playlist.Current?.DisplayName;
        StatusText.Text = string.IsNullOrEmpty(name)
            ? $"{Playback.EngineLabel} · {Playback.Status}"
            : $"{name} · {Playback.EngineLabel} · {Playback.Status}";
        if (PlaylistView.SelectedIndex != Playback.Playlist.CurrentIndex)
        {
            PlaylistView.SelectedIndex = Playback.Playlist.CurrentIndex;
            if (PlaylistView.SelectedItem != null)
                PlaylistView.ScrollIntoView(PlaylistView.SelectedItem);
        }
        MuteButton.IsChecked = Playback.Muted;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Playback.TogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => Playback.Stop();
    private async void Next_Click(object sender, RoutedEventArgs e) => await Playback.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await Playback.PreviousAsync();

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
            Anchor(Predicted(), playing);
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
            TimeText.Text = $"{ClockText.Format(shown)} / {ClockText.Format(duration)}";
        }
        finally
        {
            _layingProgress = false;
        }
    }

    private void Volume_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolume)
            return;
        Playback.SetVolume(VolumeSlider.Value / 100);
        _saveSettingsTimer.Stop();
        _saveSettingsTimer.Start();
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

    private void Fullscreen_Click(object sender, RoutedEventArgs e) =>
        App.Window.SetFullscreen(!App.Window.IsFullscreen);

    private void SyncFullscreenButton() =>
        FullscreenButton.Content = App.Window.IsFullscreen ? "退出全屏" : "全屏";

    private void PlaylistButton_Click(object sender, RoutedEventArgs e) =>
        SetPlaylist(PlaylistButton.IsChecked == true);

    private void CollapsePlaylist_Click(object sender, RoutedEventArgs e) => SetPlaylist(false);

    private void SetPlaylist(bool open)
    {
        if (_playlistSync)
            return;
        _playlistSync = true;
        PlaylistPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PlaylistButton.IsChecked = open;
        _playlistSync = false;
        Stage.UpdateLayout();
        _presenter.Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Playback.Stop();
        Playback.Playlist.Clear();
    }

    private void Repeat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RepeatBox.SelectedIndex >= 0)
            Playback.Playlist.Repeat = (RepeatMode)RepeatBox.SelectedIndex;
    }

    private void Shuffle_Toggled(object sender, RoutedEventArgs e) =>
        Playback.Playlist.Shuffle = ShuffleSwitch.IsOn;

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
                    PipelinePanel.Visibility = Visibility.Collapsed;
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
                ShuffleSwitch.IsOn = !ShuffleSwitch.IsOn;
                break;
            case HotkeyCatalog.CycleRepeat:
                RepeatBox.SelectedIndex = (RepeatBox.SelectedIndex + 1) % RepeatBox.Items.Count;
                break;
        }
    }

    private void TogglePipeline()
    {
        var open = PipelinePanel.Visibility != Visibility.Visible;
        PipelinePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
            UpdatePipelineText();
    }

    private void UpdatePipelineText()
    {
        try
        {
            PipelineText.Text = Playback.Pipeline()?.Format() ?? "当前没有播放。";
        }
        catch (Exception ex)
        {
            PipelineText.Text = "读取管线信息失败：" + ex.Message;
        }
    }
}
