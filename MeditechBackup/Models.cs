namespace MeditechBackup;

/// <summary>A row from api.php?action=jobs.</summary>
public sealed class ServerJob
{
    public string JobCode { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? JobFileLocation { get; set; }
    public string? JobProjectStatus { get; set; }
}

public static class Category
{
    public const string Active = "active";    // 1. In Progress / On Hold          -> back up, keep versions N days
    public const string Pending = "pending";  // 2. Completed - Pending Zip        -> back up, pending retention
    public const string Held = "held";        // 3. Zipping / error, 4. Zipped     -> keep copy, don't scan
    public const string Done = "done";        // 5. Complete / 6. Archived         -> delete the copy (on tape)
    public const string Unknown = "unknown";  // not in the database any more      -> keep, flag it
    public const string Permanent = "permanent"; // a "permanent folder" from the settings -> always back up, never remove

    /// <summary>Codes of permanent folders start with this; they're not database jobs.</summary>
    public const string PermanentPrefix = "PERM-";

    /// <summary>Statuses start with their pipeline step number ("1. In Progress"), so go by that.</summary>
    public static string FromStatus(string? status)
    {
        var s = (status ?? "").TrimStart();
        if (s.Length == 0) return Unknown;
        return s[0] switch
        {
            '1' => Active,
            '2' => Pending,
            '3' or '4' => Held,
            '5' or '6' => Done,
            _ => Unknown,
        };
    }

    public static bool ShouldScan(string cat) => cat is Active or Pending or Permanent;
}

/// <summary>What the service remembers about each job (saved on the backup drives).</summary>
public sealed class JobState
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string DbStatus { get; set; } = "";
    public string Category { get; set; } = MeditechBackup.Category.Unknown;
    /// <summary>Path under Jobs\ and _versions\ on each drive, e.g. "Meditech\MTH126 - Fix up existing Human".</summary>
    public string DestRel { get; set; } = "";
    /// <summary>Where the current copy lives, relative to the root folder on each drive:
    /// "Jobs\Client\Job" for new jobs, or an existing (e.g. bvckup) folder that was adopted.</summary>
    public string DestPath { get; set; } = "";
    /// <summary>Index into the drive list where new files for this job go first.</summary>
    public string HomeDrive { get; set; } = "";
    public List<string> Drives { get; set; } = new();
    public string? DuplicateOf { get; set; }
    public bool Skip { get; set; }
    public int RetentionDays { get; set; }

    public DateTime? FirstSeen { get; set; }
    public DateTime? LastStart { get; set; }
    public DateTime? LastEnd { get; set; }
    public DateTime? LastSuccess { get; set; }
    public string? LastResult { get; set; }     // ok | warnings | error | interrupted | held | skipped
    public string? LastMessage { get; set; }
    public long Files { get; set; }
    public long Bytes { get; set; }
    public long LastCopiedFiles { get; set; }
    public long LastCopiedBytes { get; set; }
    public long LastVersionedFiles { get; set; }
    public long LastErrors { get; set; }
    public long VersionBytes { get; set; }
    public int VersionSets { get; set; }
}

public sealed class PersistedState
{
    public bool Paused { get; set; }
    public DateTime? LastCycleStart { get; set; }
    public DateTime? LastCycleEnd { get; set; }
    public int CycleNumber { get; set; }
    public Dictionary<string, JobState> Jobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What the full-drive cleanup deleted, newest first (kept to the last 200).</summary>
    public List<CleanupDeleted> CleanupHistory { get; set; } = new();
}

/// <summary>One folder the full-drive cleanup deleted.</summary>
public sealed class CleanupDeleted
{
    public DateTime At { get; set; }
    public string Path { get; set; } = "";
    /// <summary>"versions" (old versions of a job, A) or "folder" (a folder no job uses, B).</summary>
    public string Kind { get; set; } = "";
    public long Bytes { get; set; }
    /// <summary>Newest write time of anything in it.</summary>
    public DateTime? Newest { get; set; }
}

/// <summary>A folder on a backup drive that no job uses: deleted (oldest first) when the drive is full.</summary>
public sealed class CleanupCandidate
{
    public string Path { get; set; } = "";
    public string Drive { get; set; } = "";
    public long Bytes { get; set; }
    public long Files { get; set; }
    public DateTime? Newest { get; set; }
    /// <summary>Old enough to delete (and nothing protected inside).</summary>
    public bool Eligible { get; set; }
    public string? Note { get; set; }
}

public sealed class CleanupStatus
{
    /// <summary>When the folder list below was last worked out.</summary>
    public DateTime? SurveyedAt { get; set; }
    public List<CleanupCandidate> Candidates { get; set; } = new();
    public long EligibleBytes { get; set; }
    public long TotalBytes { get; set; }
    public DateTime? LastRun { get; set; }
    public string? LastMessage { get; set; }
    public List<CleanupDeleted> Recent { get; set; } = new();
}

// ----- Live status snapshot (sent to 201 and shown on the local page) -----

public sealed class CurrentJob
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Phase { get; set; } = "";
    public long FilesTotal { get; set; }
    public long FilesDone { get; set; }
    public long FilesCopied { get; set; }
    public long BytesToCopy { get; set; }
    public long BytesCopied { get; set; }
    public double SpeedBps { get; set; }
    public string? CurrentFile { get; set; }
    public DateTime StartedAt { get; set; }
}

public sealed class CycleInfo
{
    public int Number { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int JobIndex { get; set; }
    public int JobCount { get; set; }
    public DateTime? NextAt { get; set; }
    public DateTime? LastEndedAt { get; set; }
    public double LastDurationSec { get; set; }
}

public sealed class DriveInfoDto
{
    public string Path { get; set; } = "";
    public string? Label { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public int Jobs { get; set; }
}

public sealed class LogEntry
{
    public DateTime T { get; set; }
    public string Level { get; set; } = "info";
    public string Msg { get; set; } = "";
}

public sealed class StatusSnapshot
{
    public string Machine { get; set; } = Environment.MachineName;
    public string Version { get; set; } = "";
    public int WebPort { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime Now { get; set; }
    public string State { get; set; } = "starting";
    public string StateMessage { get; set; } = "";
    public bool Paused { get; set; }
    public CycleInfo Cycle { get; set; } = new();
    public CurrentJob? Current { get; set; }
    public List<DriveInfoDto> Drives { get; set; } = new();
    public List<JobState> Jobs { get; set; } = new();
    public List<LogEntry> Log { get; set; } = new();
    public ServiceSettings? Settings { get; set; }
    public CleanupStatus? Cleanup { get; set; }
}
