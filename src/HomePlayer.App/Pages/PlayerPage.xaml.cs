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
    private bool _scrubbing;
    private bool _updatingVolume;

    public PlayerPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _presenter = new VideoPresenter(VideoSurface);
        var playback = App.Host.Playback;
        playback.FrameArrived += _presenter.Submit;
        playback.Changed += () => DispatcherQueue.TryEnqueue(UpdateChrome);
        PlaylistView.ItemsSource = playback.Playlist.Items;
        RepeatBox.SelectedIndex = (int)playback.Playlist.Repeat;
        _updatingVolume = true;
        VolumeSlider.Value = playback.Volume * 100;
        _updatingVolume = false;
        _timer.Tick += (_, _) =>
        {
            if (_scrubbing)
                return;
            var duration = playback.Duration.TotalSeconds;
            PositionSlider.Maximum = duration > 0 ? duration : 1;
            PositionSlider.Value = Math.Clamp(playback.Position.TotalSeconds, 0, PositionSlider.Maximum);
            TimeText.Text = $"{ClockText.Format(playback.Position)} / {ClockText.Format(playback.Duration)}";
            if (PipelinePanel.Visibility == Visibility.Visible)
                PipelineText.Text = playback.Pipeline()?.Format() ?? "当前没有播放。";
        };
        _timer.Start();
        Loaded += (_, _) => UpdateChrome();
    }

    private PlaybackController Playback => App.Host.Playback;

    private void UpdateChrome()
    {
        PlayButton.Content = Playback.IsPlaying ? "暂停" : "播放";
        StatusText.Text = $"{Playback.EngineLabel} · {Playback.Status}";
        if (PlaylistView.SelectedIndex != Playback.Playlist.CurrentIndex)
            PlaylistView.SelectedIndex = Playback.Playlist.CurrentIndex;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Playback.TogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => Playback.Stop();
    private async void Next_Click(object sender, RoutedEventArgs e) => await Playback.NextAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await Playback.PreviousAsync();

    private void Slider_Pressed(object sender, PointerRoutedEventArgs e) => _scrubbing = true;

    private void Slider_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing)
            return;
        _scrubbing = false;
        Playback.Seek(TimeSpan.FromSeconds(PositionSlider.Value));
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

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => App.Window.SetFullscreen(true);

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
        if (PlaylistView.SelectedIndex < 0)
            return;
        Playback.Playlist.CurrentIndex = PlaylistView.SelectedIndex;
        await Playback.PlayPathsAsync(Playback.Playlist.Items.Select(item => item.Path).ToList(), PlaylistView.SelectedIndex);
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
        App.Window.SetFullscreen(true);
        args.Handled = true;
    }

    private void Accel_Escape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        App.Window.SetFullscreen(false);
        args.Handled = true;
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
