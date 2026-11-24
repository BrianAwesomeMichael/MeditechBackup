using System.Text.Json;

namespace MeditechBackup;

/// <summary>
/// Shared live status, the log, and the persisted job state. The job state is
/// saved in &lt;drive&gt;\MeditechBackup\_state\state.json on every backup drive,
/// so moving the drives to a new PC brings the history along.
/// </summary>
public sealed class StatusHub
{
    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _log = new();
    private const int LogKeep = 400;
    private readonly string _logDir;
    private readonly LocalConfig _cfg;

    public PersistedState State { get; private set; } = new();
    public string RunState { get; set; } = "starting";
    public string RunMessage { get; set; } = "";
    public CurrentJob? Current { get; set; }
    public CycleInfo Cycle { get; } = new();
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public List<DriveInfoDto> Drives { get; set; } = new();
    public ServiceSettings? Settings { get; set; }
    public int WebPort => _cfg.WebPort;

    public StatusHub(LocalConfig cfg, string exeDir)
    {
        _cfg = cfg;
        _logDir = Path.Combine(exeDir, "logs");
        Directory.CreateDirectory(_logDir);
        PruneLogFiles();
    }

    // ---------------- log ----------------

    public void Info(string msg) => Add("info", msg);
    public void Warn(string msg) => Add("warn", msg);
    public void Error(string msg) => Add("error", msg);

    private void Add(string level, string msg)
    {
        var e = new LogEntry { T = DateTime.UtcNow, Level = level, Msg = msg };
        lock (_lock)
        {
            _log.AddLast(e);
            while (_log.Count > LogKeep) _log.RemoveFirst();
            try
            {
                File.AppendAllText(Path.Combine(_logDir, $"backup-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToUpperInvariant()}] {msg}{Environment.NewLine}");
            }
            catch { }
        }
        try { Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}"); } catch { }
    }

    private void PruneLogFiles()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_logDir, "backup-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-60)) File.Delete(f);
        }
        catch { }
    }

    // ---------------- persisted state ----------------

    private IEnumerable<string> StateFiles() =>
        _cfg.BackupRoots().Select(r => Path.Combine(r, "_state", "state.json"));

    /// <summary>Load the newest state.json found on any backup drive.</summary>
    public void LoadState()
    {
        PersistedState? best = null; DateTime bestTime = DateTime.MinValue;
        foreach (var f in StateFiles())
        {
            try
            {
                if (!File.Exists(f)) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t <= bestTime) continue;
                var s = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(f), Json.Options);
                if (s != null) { best = s; bestTime = t; }
            }
            catch (Exception ex) { Warn($"Could not read {f}: {ex.Message}"); }
        }
        if (best != null)
        {
            best.Jobs = new Dictionary<string, JobState>(best.Jobs ?? new(), StringComparer.OrdinalIgnoreCase);
            State = best;
            Info($"Loaded state for {best.Jobs.Count} jobs.");
        }
    }

    public void SaveState()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(State, Json.Options);
        foreach (var f in StateFiles())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f + ".tmp", json);
                File.Move(f + ".tmp", f, true);
            }
            catch { /* a missing drive is reported elsewhere */ }
        }
    }

    // ---------------- snapshot ----------------

    public StatusSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new StatusSnapshot
            {
                Version = typeof(StatusHub).Assembly.GetName().Version?.ToString(3) ?? "",
                WebPort = _cfg.WebPort,
                StartedAt = StartedAt,
                Now = DateTime.UtcNow,
                State = State.Paused && RunState != "running" ? "paused" : RunState,
                StateMessage = RunMessage,
                Paused = State.Paused,
                Cycle = new CycleInfo
                {
                    Number = Cycle.Number, StartedAt = Cycle.StartedAt, EndedAt = Cycle.EndedAt,
                    JobIndex = Cycle.JobIndex, JobCount = Cycle.JobCount, NextAt = Cycle.NextAt,
                    LastEndedAt = Cycle.LastEndedAt, LastDurationSec = Cycle.LastDurationSec,
                },
                Current = Current,
                Drives = Drives.ToList(),
                Jobs = State.Jobs.Values.OrderBy(j => j.Code).ToList(),
                Log = _log.ToList(),
                Settings = Settings,
            };
        }
    }

    /// <summary>Run an update to job state under the lock (the web thread reads it concurrently).</summary>
    public void Mutate(Action<PersistedState> fn) { lock (_lock) fn(State); }
}
