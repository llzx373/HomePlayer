using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer;

internal sealed class SubtitleSession
{
    public enum Choice
    {
        Off,
        Auto,
        Sidecar,
        Embedded
    }

    private Choice _choice = Choice.Auto;
    private Choice _restore = Choice.Auto;
    private string? _restoreSidecar;
    private int _restoreEmbedded = -1;
    private string? _sidecar;
    private string? _manualSidecar;
    private int _embedded = -1;
    private SubtitleTrack _track = SubtitleTrack.Empty;
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

    public string? VideoPath { get; private set; }
    public IReadOnlyList<string> Sidecars { get; private set; } = [];
    public IReadOnlyList<FfmpegSubtitleStream> Embedded { get; private set; } = [];
    public Choice Mode => _choice;
    public string? SidecarPath => _sidecar;
    public int EmbeddedIndex => _embedded;
    public string? Error { get; private set; }
    public bool IsOff => _choice == Choice.Off;

    public IReadOnlyList<string> MenuSidecars
    {
        get
        {
            if (string.IsNullOrEmpty(_manualSidecar))
                return Sidecars;
            foreach (var path in Sidecars)
            {
                if (path.Equals(_manualSidecar, StringComparison.OrdinalIgnoreCase))
                    return Sidecars;
            }

            var list = new List<string>(Sidecars.Count + 1);
            list.AddRange(Sidecars);
            list.Add(_manualSidecar);
            return list;
        }
    }

    public void Load(string? videoPath)
    {
        VideoPath = videoPath;
        _choice = Choice.Auto;
        _restore = Choice.Auto;
        _restoreSidecar = null;
        _restoreEmbedded = -1;
        _sidecar = null;
        _manualSidecar = null;
        _embedded = -1;
        _track = SubtitleTrack.Empty;
        _unreadable.Clear();
        Error = null;
        Sidecars = string.IsNullOrWhiteSpace(videoPath) ? [] : SubtitleFiles.Find(videoPath);
        Embedded = [];
        ResolveAuto();
        Remember();
    }

    public void SetEmbeddedList(IReadOnlyList<FfmpegSubtitleStream> tracks)
    {
        Embedded = tracks;
        if (_choice == Choice.Auto)
            ResolveAuto();
    }

    public void Off()
    {
        Remember();
        _choice = Choice.Off;
        _sidecar = null;
        _embedded = -1;
        _track = SubtitleTrack.Empty;
        Error = null;
    }

    public void Auto()
    {
        _choice = Choice.Auto;
        _sidecar = null;
        _embedded = -1;
        ResolveAuto();
        Remember();
    }

    public void UseSidecar(string path)
    {
        _choice = Choice.Sidecar;
        _sidecar = path;
        _manualSidecar = path;
        _embedded = -1;
        LoadSidecar(path);
        Remember();
    }

    public bool UseEmbedded(int index)
    {
        FfmpegSubtitleStream? stream = null;
        foreach (var item in Embedded)
        {
            if (item.Index == index)
            {
                stream = item;
                break;
            }
        }

        if (stream == null || !stream.IsText)
            return false;
        _choice = Choice.Embedded;
        _embedded = index;
        _sidecar = null;
        _track = SubtitleTrack.Empty;
        Error = null;
        Remember();
        return true;
    }

    public void SetCues(IReadOnlyList<SubtitleCue> cues, string? error)
    {
        _track = new SubtitleTrack(cues);
        Error = error;
    }

    public string? TextAt(TimeSpan position) =>
        _choice == Choice.Off ? null : _track.At(position)?.Text;

    public bool OffChecked => _choice == Choice.Off;
    public bool AutoChecked => _choice == Choice.Auto;

    public bool SidecarChecked(string path) =>
        _choice == Choice.Sidecar && path.Equals(_sidecar, StringComparison.OrdinalIgnoreCase);

    public bool EmbeddedChecked(int index) =>
        _choice == Choice.Embedded && _embedded == index;

    public bool Unreadable(string path) => _unreadable.Contains(path);

    public FfmpegSubtitleStream? AutoEmbedded =>
        _choice == Choice.Auto && _embedded >= 0
            ? Embedded.FirstOrDefault(item => item.Index == _embedded)
            : null;

    public void Cycle()
    {
        var tracks = CycleTracks();
        if (_choice == Choice.Off)
        {
            Auto();
            return;
        }

        if (_choice == Choice.Auto)
        {
            if (tracks.Count == 0)
                Off();
            else
                Apply(tracks[0]);
            return;
        }

        var current = CurrentTrack(tracks);
        var next = current + 1;
        if (next >= tracks.Count)
            Off();
        else
            Apply(tracks[next]);
    }

    public void Toggle()
    {
        if (_choice == Choice.Off)
            Restore();
        else
            Off();
    }

    private void ResolveAuto()
    {
        _embedded = -1;
        foreach (var path in Sidecars)
        {
            LoadSidecar(path);
            if (!_unreadable.Contains(path))
            {
                _sidecar = path;
                return;
            }
        }

        _sidecar = null;
        _track = SubtitleTrack.Empty;
        Error = null;
        foreach (var track in Embedded)
        {
            if (!track.IsText)
                continue;
            _embedded = track.Index;
            return;
        }
    }

    private void LoadSidecar(string path)
    {
        try
        {
            var cues = SubtitleText.ParseFile(path);
            _track = new SubtitleTrack(cues);
            _unreadable.Remove(path);
            Error = cues.Count == 0 ? Path.GetFileName(path) + " 没有可显示的字幕" : null;
            if (cues.Count == 0)
                _unreadable.Add(path);
        }
        catch (Exception ex)
        {
            _track = SubtitleTrack.Empty;
            _unreadable.Add(path);
            Error = Path.GetFileName(path) + " 无法读取：" + ex.Message;
        }
    }

    private void Remember()
    {
        if (_choice == Choice.Off)
            return;
        _restore = _choice;
        _restoreSidecar = _sidecar;
        _restoreEmbedded = _embedded;
    }

    private void Restore()
    {
        switch (_restore)
        {
            case Choice.Sidecar when !string.IsNullOrEmpty(_restoreSidecar):
                UseSidecar(_restoreSidecar);
                break;
            case Choice.Embedded when _restoreEmbedded >= 0:
                if (!UseEmbedded(_restoreEmbedded))
                    Auto();
                break;
            default:
                Auto();
                break;
        }
    }

    private List<(bool Sidecar, string? Path, int Index)> CycleTracks()
    {
        var tracks = new List<(bool Sidecar, string? Path, int Index)>();
        foreach (var path in MenuSidecars)
            tracks.Add((true, path, -1));
        foreach (var track in Embedded)
        {
            if (track.IsText)
                tracks.Add((false, null, track.Index));
        }

        return tracks;
    }

    private int CurrentTrack(List<(bool Sidecar, string? Path, int Index)> tracks)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (_choice == Choice.Sidecar && track.Sidecar && track.Path != null && SidecarChecked(track.Path))
                return i;
            if (_choice == Choice.Embedded && !track.Sidecar && track.Index == _embedded)
                return i;
        }

        return -1;
    }

    private void Apply((bool Sidecar, string? Path, int Index) track)
    {
        if (track.Sidecar && track.Path != null)
            UseSidecar(track.Path);
        else
            UseEmbedded(track.Index);
    }
}
