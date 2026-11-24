using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MeditechBackup;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Write JSON to a temp file, then swap it in so a crash never leaves half a file.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>The on-disk shape of MeditechBackup.json.</summary>
public sealed class LocalConfigData
{
    public string ServerUrl { get; set; } = "http://192.168.1.201/Meditech/backupService/api.php";
    /// <summary>Backup drives (or folders), e.g. "E:\\", "G:\\".</summary>
    public List<string> Drives { get; set; } = new();
    /// <summary>Folder at the top of each drive that holds everything, e.g. "Backups".</summary>
    public string RootFolderName { get; set; } = "MeditechBackup";
    /// <summary>Where brand-new jobs go inside the root folder ("" = straight in the root, as Client\Job).</summary>
    public string JobsSubfolder { get; set; } = "Jobs";
    /// <summary>Port of the local status page (http://this-pc:8099/). Needs a restart to change.</summary>
    public int WebPort { get; set; } = 8099;
}

/// <summary>
/// Per-PC settings in MeditechBackup.json next to the exe. Everything else
/// (interval, retention, excludes...) comes from the 201 admin tab.
/// Re-read automatically when the file changes; also editable from the local page.
/// </summary>
public sealed class LocalConfig
{
    private readonly object _lock = new();
    private LocalConfigData _d = new();
    private DateTime _fileTime;

    public string FilePath { get; }
    /// <summary>Bumped on every change so dependants can rebuild.</summary>
    public int Version { get; private set; }
    public string? LoadError { get; private set; }

    public LocalConfig(string exeDir)
    {
        FilePath = Path.Combine(exeDir, "MeditechBackup.json");
        if (!File.Exists(FilePath)) Save(new LocalConfigData());
        Reload();
    }

    public LocalConfigData Data { get { lock (_lock) return _d; } }
    public string ServerUrl => Data.ServerUrl;
    public List<string> Drives => Data.Drives;
    public string RootFolderName => Data.RootFolderName;
    public string JobsSubfolder => Data.JobsSubfolder ?? "";
    public int WebPort => Data.WebPort;

    /// <summary>Re-read the file if it changed. Returns true when something changed.</summary>
    public bool ReloadIfChanged()
    {
        try { if (File.GetLastWriteTimeUtc(FilePath) == _fileTime) return false; } catch { return false; }
        return Reload();
    }

    private bool Reload()
    {
        try
        {
            var t = File.GetLastWriteTimeUtc(FilePath);
            var d = JsonSerializer.Deserialize<LocalConfigData>(File.ReadAllText(FilePath), Json.Options) ?? new();
            Clean(d);
            lock (_lock) { _d = d; _fileTime = t; Version++; }
            LoadError = null;
            return true;
        }
        catch (Exception e)
        {
            try { _fileTime = File.GetLastWriteTimeUtc(FilePath); } catch { }
            LoadError = $"{FilePath} could not be read: {e.Message}";
            return false;
        }
    }

    public void Save(LocalConfigData d)
    {
        Clean(d);
        Json.WriteAtomic(FilePath, d);
        lock (_lock) { _d = d; _fileTime = File.GetLastWriteTimeUtc(FilePath); Version++; }
        LoadError = null;
    }

    private static void Clean(LocalConfigData d)
    {
        d.Drives = (d.Drives ?? new()).Select(NormalizeDrive).Where(x => x.Length > 0)
                                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        d.RootFolderName = (d.RootFolderName ?? "").Trim().Trim('\\', '/');
        d.JobsSubfolder = (d.JobsSubfolder ?? "").Trim().Trim('\\', '/');
        if (string.IsNullOrWhiteSpace(d.ServerUrl)) d.ServerUrl = new LocalConfigData().ServerUrl;
        if (d.WebPort <= 0) d.WebPort = 8099;
    }

    /// <summary>"e", "E:", "E:/" -> "E:\". Folders are left as typed (with backslashes).</summary>
    public static string NormalizeDrive(string? s)
    {
        s = (s ?? "").Trim().Trim('"').Replace('/', '\\');
        if (Regex.IsMatch(s, @"^[A-Za-z]$")) s += ":";
        if (Regex.IsMatch(s, @"^[A-Za-z]:$")) s += "\\";
        if (s.Length > 3) s = s.TrimEnd('\\');
        return s.Length >= 2 && s[1] == ':' ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;
    }

    public IEnumerable<string> BackupRoots() =>
        Drives.Select(d => RootFolderName.Length > 0 ? Path.Combine(d, RootFolderName) : d);
}

/// <summary>Shared settings edited on admin.php?view=backup_service (stored on 201).</summary>
public sealed class ServiceSettings
{
    public double IntervalHours { get; set; } = 4;
    public int RetentionDaysActive { get; set; } = 30;
    public int RetentionDaysPending { get; set; }
    public List<string> ExcludePatterns { get; set; } = new();
    public double ReserveFreeGb { get; set; } = 50;
    public double MaxMbPerSec { get; set; }
    public bool DeleteCompleted { get; set; } = true;
    public bool Rebalance { get; set; } = true;
    public Dictionary<string, JobOverride> JobOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- When a drive is full ----
    /// <summary>A drive with less free space than this gets cleaned up (to back above it).</summary>
    public double CleanupFreeGb { get; set; } = 500;
    /// <summary>Only things with nothing written in this many days are deleted by the cleanup.</summary>
    public int CleanupMinAgeDays { get; set; } = 7;
    /// <summary>A: delete old versions older than CleanupMinAgeDays (sooner than their usual retention).</summary>
    public bool CleanupVersions { get; set; } = true;
    /// <summary>B: delete folders on the backup drives that no job uses (old bvckup2 copies etc.).</summary>
    public bool CleanupUnowned { get; set; }
    /// <summary>Never deleted by the cleanup: "Archive\Keep" (a path under the backup folder, and everything in it)
    /// or "KEEP*" (any folder with that name).</summary>
    public List<string> ProtectedFolders { get; set; } = new();
    /// <summary>Always backed up like a production job, whatever the database says: "\\server\share\Folder".</summary>
    public List<string> PermanentFolders { get; set; } = new();

    public long ReserveBytes => (long)(ReserveFreeGb * 1024 * 1024 * 1024);
    public long CleanupFreeBytes => Math.Max(ReserveBytes, (long)(CleanupFreeGb * 1024 * 1024 * 1024));

    public JobOverride? OverrideFor(string code) =>
        JobOverrides?.FirstOrDefault(kv => string.Equals(kv.Key, code, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed class JobOverride
{
    public int? RetentionDays { get; set; }
    public bool Skip { get; set; }
}
