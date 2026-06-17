using System.IO;

namespace AOI_Monitor.Services;

public sealed class FolderCameraSource : ICameraSource
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    private readonly Dictionary<CameraViewType, string> _folders = new();
    private readonly Dictionary<CameraViewType, List<string>> _frames = new();
    private readonly Dictionary<CameraViewType, int> _indices = new();
    private long _frameSequence;

    public FolderCameraSource(
        IReadOnlyDictionary<CameraViewType, string> folders,
        string boardModel = "TBOX-MAIN",
        string lotId = "POC-LOT")
    {
        BoardModel = string.IsNullOrWhiteSpace(boardModel) ? "TBOX-MAIN" : boardModel.Trim();
        LotId = string.IsNullOrWhiteSpace(lotId) ? "POC-LOT" : lotId.Trim();

        foreach (var (view, folder) in folders)
            SetFolder(view, folder);
    }

    public string Name => "Folder Camera Source";
    public CameraViewType SelectedView { get; set; } = CameraViewType.Top;
    public CameraSourceStatus ConnectionStatus => HasAnyFrames
        ? CameraSourceStatus.Simulated
        : MissingConfiguredFolder
            ? CameraSourceStatus.Error
            : CameraSourceStatus.NotConnected;
    public string StatusMessage => ConnectionStatus switch
    {
        CameraSourceStatus.Simulated => "Folder Camera Simulation is ready.",
        CameraSourceStatus.Error => "One or more configured simulation folders cannot be read or contain no images.",
        _ => "No simulation image folders are configured.",
    };
    public bool IsAcquiring { get; private set; }
    public string BoardModel { get; }
    public string LotId { get; }

    public IReadOnlyDictionary<CameraViewType, string> Folders => _folders;

    public void SetFolder(CameraViewType viewType, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            _folders.Remove(viewType);
            _frames.Remove(viewType);
            _indices.Remove(viewType);
            return;
        }

        _folders[viewType] = folderPath;
        _frames[viewType] = Directory.Exists(folderPath)
            ? Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(Path.GetFileName)
                .ToList()
            : new List<string>();
        _indices[viewType] = -1;
    }

    public void StartAcquisition()
    {
        IsAcquiring = HasAnyFrames;
    }

    public void StopAcquisition()
    {
        IsAcquiring = false;
    }

    public CameraFrame? GetNextFrame()
    {
        if (!IsAcquiring)
            return null;

        if (!_frames.TryGetValue(SelectedView, out var frames) || frames.Count == 0)
            return null;

        var index = _indices.TryGetValue(SelectedView, out var current) ? current : -1;
        index = (index + 1) % frames.Count;
        _indices[SelectedView] = index;

        var frameId = $"{SelectedView}-{Interlocked.Increment(ref _frameSequence):000000}";
        return new CameraFrame(frameId, frames[index], SelectedView, DateTime.Now, Name, BoardModel, LotId);
    }

    private bool HasAnyFrames => _frames.Values.Any(frames => frames.Count > 0);
    private bool MissingConfiguredFolder => _folders.Count > 0 && !HasAnyFrames;
}
