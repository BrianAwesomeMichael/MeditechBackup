namespace MeditechBackup;

/// <summary>One configured backup drive. Layout on it:
/// <code>
///   E:\MeditechBackup\Jobs\&lt;Client&gt;\&lt;Job folder&gt;\...                 the current copy
///   E:\MeditechBackup\_versions\&lt;Client&gt;\&lt;Job folder&gt;\&lt;date&gt;\...    old versions
///   E:\MeditechBackup\_state\state.json                            job history
/// </code></summary>
public sealed class BackupDrive
{
    public string Drive { get; }     // as written in MeditechBackup.json, e.g. "E:\"
    public string Root { get; }      // \\?\E:\MeditechBackup

    public BackupDrive(string drive, string folderName)
    {
        Drive = drive.Trim();
        Root = PathUtil.Long(folderName.Length > 0 ? Path.Combine(Drive, folderName) : Drive);
    }

    public string Name => PathUtil.Display(Drive).TrimEnd('\\');
    public string RootDisplay => PathUtil.Display(Root);
    /// <summary>The job's current copy. destPath is relative to the root: "Jobs\Client\Job" for new jobs,
    /// or wherever an existing (bvckup) copy was found.</summary>
    public string JobDir(string destPath) => Path.Combine(Root, Check(destPath));
    public string VersionDir(string destRel) => Path.Combine(Root, "_versions", Check(destRel));

    /// <summary>Never let an empty or "..\" path resolve to the root folder itself (a delete there would wipe everything).</summary>
    private static string Check(string rel)
    {
        var r = (rel ?? "").Trim().Trim('\\');
        if (r.Length == 0 || r == "." || r.Split('\\').Any(p => p == ".." || p.Length == 0))
            throw new InvalidOperationException($"Refusing to use backup folder path \"{rel}\".");
        if (r.Equals("_state", StringComparison.OrdinalIgnoreCase) || r.Equals("_versions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to use reserved folder \"{rel}\".");
        return r;
    }

    public bool IsReady
    {
        get
        {
            try
            {
                var di = Info();
                if (di is { IsReady: false }) return false;
                Directory.CreateDirectory(Root);
                return true;
            }
            catch { return false; }
        }
    }

    private DriveInfo? Info()
    {
        var root = Path.GetPathRoot(PathUtil.Display(Root));
        return root != null && root.Length <= 3 ? new DriveInfo(root) : null; // UNC folder: no DriveInfo
    }

    public long FreeBytes
    {
        get
        {
            try
            {
                var di = Info();
                if (di != null) return di.AvailableFreeSpace;
                return GetDiskFreeSpace(PathUtil.Display(Root)).free;
            }
            catch { return 0; }
        }
    }

    public long TotalBytes
    {
        get
        {
            try
            {
                var di = Info();
                if (di != null) return di.TotalSize;
                return GetDiskFreeSpace(PathUtil.Display(Root)).total;
            }
            catch { return 0; }
        }
    }

    public string? Label { get { try { return Info()?.VolumeLabel; } catch { return null; } } }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeToCaller, out ulong total, out ulong totalFree);

    private static (long free, long total) GetDiskFreeSpace(string dir)
    {
        return GetDiskFreeSpaceEx(dir, out var f, out var t, out _) ? ((long)f, (long)t) : (0, 0);
    }
}

public sealed class BackupDrives
{
    private readonly LocalConfig _cfg;
    private List<BackupDrive> _all = new();
    private int _version = -1;

    public BackupDrives(LocalConfig cfg) { _cfg = cfg; }

    /// <summary>Rebuilt whenever MeditechBackup.json changes.</summary>
    public List<BackupDrive> All
    {
        get
        {
            if (_version != _cfg.Version)
            {
                _all = _cfg.Drives.Select(d => new BackupDrive(d, _cfg.RootFolderName)).ToList();
                _version = _cfg.Version;
            }
            return _all;
        }
    }

    public List<BackupDrive> Ready() => All.Where(d => d.IsReady).ToList();

    public BackupDrive? ByName(string? drive) =>
        drive == null ? null : All.FirstOrDefault(d => string.Equals(d.Drive, drive, StringComparison.OrdinalIgnoreCase));
}
