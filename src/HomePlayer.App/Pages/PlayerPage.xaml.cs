using HomePlayer.Core;
using HomePlayer.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class PlayerPage : Page
{
    private readonly VideoPresenter _presenter;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _scrubIdle = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private bool _scrubbing;
    private bool _pointerDown;
    private bool _syncSlider;
    private bool _updatingVolume;
    private bool _playlistSync;
    private long _lastSeekMs;

    public PlayerPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _presenter = new VideoPresenter(VideoSurface);
        VideoHost.SizeChanged += (_, _) => _presenter.Refresh();
        var playback = App.Host.Playback;
        playback.FrameArrived += _presenter.Submit;
        playback.Changed += () => DispatcherQueue.TryEnqueue(UpdateChrome);
        App.Window.FullscreenChanged += () => DispatcherQueue.TryEnqueue(SyncFullscreenButton);
        PlaylistView.ItemsSource = playback.Playlist.Items;
        RepeatBox.SelectedIndex = (int)playback.Playlist.Repeat;
        _updatingVolume = true;
        VolumeSlider.Value = playback.Volume * 100;
        _updatingVolume = false;
        PositionSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(Slider_Pressed), true);
        PositionSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(Slider_Released), true);
        PositionSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Slider_Released), true);
        PositionSlider.AddHandler(PointerCanceledEvent, new PointerEventHandler(Slider_Released), true);
        PositionSlider.ValueChanged += Position_Changed;
        _scrubIdle.Tick += (_, _) =>
        {
            if (_pointerDown)
                return;
            FinishScrub();
        };
        _timer.Tick += (_, _) =>
        {
            if (_scrubbing || _pointerDown)
            {
                ShowScrubTime();
                return;
            }

            var duration = playback.Duration.TotalSeconds;
            var maximum = duration > 0 ? duration : 1;
            var position = Math.Clamp(playback.Position.TotalSeconds, 0, maximum);
            _syncSlider = true;
            if (Math.Abs(PositionSlider.Maximum - maximum) > 0.05)
                PositionSlider.Maximum = maximum;
            if (Math.Abs(PositionSlider.Value - position) > 0.05)
                PositionSlider.Value = position;
            _syncSlider = false;
            TimeText.Text = $"{ClockText.Format(playback.Position)} / {ClockText.Format(playback.Duration)}";
            if (PipelinePanel.Visibility == Visibility.Visible)
                PipelineText.Text = playback.Pipeline()?.Format() ?? "当前没有播放。";
        };
        _timer.Start();
        Loaded += (_, _) =>
        {
            UpdateChrome();
            SyncFullscreenButton();
            _presenter.Refresh();
        };
    }

    private PlaybackController Playback => App.Host.Playback;

    private void UpdateChrome()
    {
        PlayButton.Content = Playback.IsPlaying ? "暂停" : "播放";
        var name = Playback.Playlist.Current?.DisplayName;
        StatusText.Text = string.IsNullOrEmpty(name)
            ? $"{Playback.EngineLabel} · {Playback.Status}"
            : $"{name} · {Playback.EngineLabel} · {Playback.Status}";
        if (PlaylistView.SelectedIndex != Playback.Playlist.CurrentIndex)
            PlaylistView.SelectedIndex = Playback.Playlist.CurrentIndex;
        MuteButton.IsChecked = Playback.Muted;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Playback.TogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => Playback.Stop();
    private async void Next_Click(object sender, RoutedEventArgs e) => await Playback.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await Playback.PreviousAsync();

    private void Slider_Pressed(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown = true;
        _scrubbing = true;
    }

    private void Slider_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown && !_scrubbing)
            return;
        FinishScrub();
    }

    private void Position_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncSlider)
            return;
        _scrubbing = true;
        ShowScrubTime();
        var now = Environment.TickCount64;
        if (now - _lastSeekMs >= 180)
        {
            _lastSeekMs = now;
            SeekToSlider();
        }

        _scrubIdle.Stop();
        _scrubIdle.Start();
    }

    private void FinishScrub()
    {
        if (!_scrubbing && !_pointerDown)
            return;
        _scrubbing = false;
        _pointerDown = false;
        _scrubIdle.Stop();
        _lastSeekMs = Environment.TickCount64;
        SeekToSlider();
    }

    private void SeekToSlider()
    {
        if (Playback.Duration <= TimeSpan.Zero)
            return;
        Playback.Seek(TimeSpan.FromSeconds(PositionSlider.Value));
    }

    private void ShowScrubTime()
    {
        var duration = Playback.Duration > TimeSpan.Zero
            ? Playback.Duration
            : TimeSpan.FromSeconds(PositionSlider.Maximum);
        TimeText.Text = $"{ClockText.Format(TimeSpan.FromSeconds(PositionSlider.Value))} / {ClockText.Format(duration)}";
    }

    private void Volume_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolume)
            return;
        Playback.SetVolume(VolumeSlider.Value / 100);
        App.Host.SaveSettings();
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
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
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

    private void Accel_Play(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Playback.TogglePlay();
        args.Handled = true;
    }

    private void Accel_Back(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Playback.Seek(Playback.Position - TimeSpan.FromSeconds(5));
        args.Handled = true;
    }

    private void Accel_Forward(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Playback.Seek(Playback.Position + TimeSpan.FromSeconds(5));
        args.Handled = true;
    }

    private void Accel_VolumeUp(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
        args.Handled = true;
    }

    private void Accel_VolumeDown(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
        args.Handled = true;
    }

    private void Accel_Fullscreen(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        App.Window.SetFullscreen(!App.Window.IsFullscreen);
        args.Handled = true;
    }

    private void Accel_Escape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (App.Window.IsFullscreen)
        {
            App.Window.SetFullscreen(false);
            args.Handled = true;
            return;
        }

        if (PipelinePanel.Visibility == Visibility.Visible)
        {
            PipelinePanel.Visibility = Visibility.Collapsed;
            args.Handled = true;
        }
    }

    private void Accel_Pipeline(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        TogglePipeline();
        args.Handled = true;
    }

    private void TogglePipeline()
    {
        var open = PipelinePanel.Visibility != Visibility.Visible;
        PipelinePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
            PipelineText.Text = Playback.Pipeline()?.Format() ?? "当前没有播放。";
    }
}
