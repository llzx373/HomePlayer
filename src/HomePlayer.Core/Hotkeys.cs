namespace HomePlayer.Core;

public static class HotkeyCatalog
{
    public const string PlayPause = "PlayPause";
    public const string Stop = "Stop";
    public const string Previous = "Previous";
    public const string Next = "Next";
    public const string SeekBack = "SeekBack";
    public const string SeekForward = "SeekForward";
    public const string VolumeUp = "VolumeUp";
    public const string VolumeDown = "VolumeDown";
    public const string Mute = "Mute";
    public const string Fullscreen = "Fullscreen";
    public const string Escape = "Escape";
    public const string TogglePlaylist = "TogglePlaylist";
    public const string TogglePipeline = "TogglePipeline";
    public const string OpenFiles = "OpenFiles";
    public const string PlayWithFfmpeg = "PlayWithFfmpeg";
    public const string ToggleShuffle = "ToggleShuffle";
    public const string CycleRepeat = "CycleRepeat";
    public const string FitWindow = "FitWindow";
    public const string CycleAspect = "CycleAspect";
    public const string ToggleAlwaysOnTop = "ToggleAlwaysOnTop";
    public const string Snapshot = "Snapshot";

    public static readonly HotkeyAction[] Actions =
    [
        new(PlayPause, "播放/暂停", "Space"),
        new(Stop, "停止", ""),
        new(Previous, "上一个", ""),
        new(Next, "下一个", ""),
        new(SeekBack, "后退 5 秒", "Left"),
        new(SeekForward, "前进 5 秒", "Right"),
        new(VolumeUp, "音量加", "Up"),
        new(VolumeDown, "音量减", "Down"),
        new(Mute, "静音", ""),
        new(Fullscreen, "全屏", "F"),
        new(Escape, "退出全屏", "Escape"),
        new(TogglePlaylist, "播放列表", ""),
        new(TogglePipeline, "管线信息", "Ctrl+I"),
        new(OpenFiles, "打开文件", ""),
        new(PlayWithFfmpeg, "用 FFmpeg 打开", ""),
        new(ToggleShuffle, "随机", ""),
        new(CycleRepeat, "循环模式", ""),
        new(FitWindow, "自适应窗口", ""),
        new(CycleAspect, "循环画面比例", ""),
        new(ToggleAlwaysOnTop, "置顶", ""),
        new(Snapshot, "截图", "Ctrl+S")
    ];

    public static Dictionary<string, string> Defaults()
        => Actions.ToDictionary(item => item.Id, item => item.Default);

    public static Dictionary<string, string> Merge(Dictionary<string, string>? saved)
    {
        var result = Defaults();
        if (saved == null)
            return result;
        foreach (var (id, chord) in saved)
        {
            if (result.ContainsKey(id))
                result[id] = chord ?? "";
        }

        return result;
    }
}

public sealed record HotkeyAction(string Id, string Title, string Default);
