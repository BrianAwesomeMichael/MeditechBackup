namespace MeditechBackup;

/// <summary>
/// The main loop. Every <c>interval_hours</c> it starts a pass: fetch the job list
/// from 201, then back up each job one at a time, the job that was backed up
/// longest ago first. If a pass runs past the interval, the next one starts as
/// soon as it ends.
/// </summary>
public sealed class BackupEngine : BackgroundService
{
    private readonly StatusHub _hub;
    private readonly ServerClient _server;
    private readonly LocalConfig _cfg;
    private readonly BackupDrives _drives;
    private readonly JobRunner _runner;
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _jobCts;
    private volatile bool _runNow;

    public BackupEngine(StatusHub hub, ServerClient server, LocalConfig cfg, BackupDrives drives, JobRunner runner)
    {
        _hub = hub; _server = server; _cfg = cfg; _drives = drives; _runner = runner;
    }

    /// <summary>From the local page or the 201 admin tab.</summary>
    public void Command(string cmd, string from)
    {
        switch (cmd)
        {
            case "run_now":
                _hub.Info($"Run now requested ({from}).");
                _runNow = true;
                if (_hub.State.Paused) { _hub.Mutate(s => s.Paused = false); _hub.SaveState(); }
                _wake.Release();
                break;
            case "pause":
                _hub.Info($"Paused ({from}). The current job stops after the file it is copying.");
                _hub.Mutate(s => s.Paused = true);
                _hub.SaveState();
                _jobCts?.Cancel();
                _wake.Release();
                break;
            case "resume":
                _hub.Info($"Resumed ({from}).");
                _hub.Mutate(s => s.Paused = false);
                _hub.SaveState();
                _wake.Release();
                break;
            case "config_changed":
                _wake.Release();
                break;
            case "skip":
                if (_hub.Current != null) _hub.Info($"Skipping {_hub.Current.Code} ({from}).");
                _jobCts?.Cancel();
                break;
        }
    }

    private async Task Sleep(TimeSpan t, CancellationToken ct)
    {
        try { await _wake.WaitAsync(t, ct); } catch (OperationCanceledException) { }
    }

    private void Status(string state, string msg) { _hub.RunState = state; _hub.RunMessage = msg; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();
        _hub.Info($"MeditechBackup started on {Environment.MachineName}. Drives: {string.Join(", ", _cfg.Drives)}. Server: {_cfg.ServerUrl}");
        _seenConfig = _cfg.Version;
        _hub.LoadState();
        _hub.Cycle.Number = _hub.State.CycleNumber;
        if (_hub.State.LastCycleEnd is DateTime le && _hub.State.LastCycleStart is DateTime ls && le >= ls)
        {
            _hub.Cycle.LastEndedAt = le;
            _hub.Cycle.LastDurationSec = (le - ls).TotalSeconds;
        }
        RefreshDrives();
        await _server.RefreshSettingsAsync(stop);
        _hub.Settings = _server.Settings;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                CheckConfig();
                if (_cfg.LoadError != null && _drives.All.Count == 0)
                {
                    Status("error", _cfg.LoadError);
                    await Sleep(TimeSpan.FromSeconds(15), stop);
                    continue;
                }
                if (_drives.All.Count == 0)
                {
                    Status("error", "No backup drives set. Add them under \"This PC\" on the backup PC's status window (or in " + _cfg.FilePath + ").");
                    await Sleep(TimeSpan.FromSeconds(15), stop);
                    continue;
                }
                if (_drives.Ready().Count == 0)
                {
                    Status("error", "None of the backup drives can be reached: " + string.Join(", ", _drives.All.Select(d => d.RootDisplay)));
                    RefreshDrives();
                    await Sleep(TimeSpan.FromMinutes(1), stop);
                    continue;
                }
                if (_hub.State.Paused)
                {
                    Status("paused", "Paused. Press Resume or Run now to continue.");
                    _hub.Cycle.NextAt = null;
                    await Sleep(TimeSpan.FromMinutes(1), stop);
                    continue;
                }

                var interval = TimeSpan.FromHours(_server.Settings.IntervalHours);
                var due = (_hub.State.LastCycleStart ?? DateTime.MinValue) + interval;
                if (!_runNow && DateTime.UtcNow < due)
                {
                    _hub.Cycle.NextAt = due;
                    Status("waiting", $"Next pass at {due.ToLocalTime():ddd h:mm tt}.");
                    var wait = due - DateTime.UtcNow;
                    await Sleep(wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), stop);
                    await _server.RefreshSettingsAsync(stop);
                    _hub.Settings = _server.Settings;
                    continue;
                }
                _runNow = false;
                await RunCycle(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _hub.Error("Unexpected error: " + e);
                Status("error", "Unexpected error: " + e.Message);
                await Sleep(TimeSpan.FromMinutes(5), stop);
            }
        }
        _hub.Info("MeditechBackup stopping.");
        _hub.SaveState();
    }

    private async Task RunCycle(CancellationToken stop)
    {
        Status("running", "Getting the job list from the server…");
        var set = await _server.RefreshSettingsAsync(stop);
        _hub.Settings = set;

        List<ServerJob> rows;
        try { rows = await _server.GetJobsAsync(_hub.State.Jobs.Keys.ToList(), stop); }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            _hub.Error("Could not get the job list from the server: " + e.Message);
            Status("error", "Can't reach the server for the job list. Retrying in 5 minutes. (" + e.Message + ")");
            await Sleep(TimeSpan.FromMinutes(5), stop);
            return;
        }

        var cycleStart = DateTime.UtcNow;
        _hub.Mutate(s => { s.LastCycleStart = cycleStart; s.CycleNumber++; });
        _hub.Cycle.Number = _hub.State.CycleNumber;
        _hub.Cycle.StartedAt = cycleStart;
        _hub.Cycle.EndedAt = null;
        _hub.Cycle.NextAt = null;

        var ready = _drives.Ready();
        foreach (var d in _drives.All.Where(d => !ready.Contains(d)))
            _hub.Error($"Backup drive {d.Name} is not available.");

        SyncJobs(rows, set);
        RemoveCompleted(set, ready, stop);
        AssignDestPaths(ready, stop);

        // Zipping/zipped jobs aren't re-scanned, but their old versions still expire.
        foreach (var j in _hub.State.Jobs.Values.Where(j => j.Category is Category.Held or Category.Unknown).ToList())
            try { _runner.PruneVersions(j, ready); } catch { }

        var jobs = _hub.State.Jobs.Values
            .Where(j => Category.ShouldScan(j.Category) && !j.Skip && j.DuplicateOf == null)
            .OrderBy(j => j.LastSuccess ?? DateTime.MinValue).ThenBy(j => j.Code)
            .ToList();
        _hub.Cycle.JobCount = jobs.Count;
        _hub.Info($"Pass {_hub.Cycle.Number} started: {jobs.Count} jobs to check.");
        _hub.SaveState();

        for (int i = 0; i < jobs.Count; i++)
        {
            if (stop.IsCancellationRequested || _hub.State.Paused) break;
            var job = jobs[i];

            // Settings can change mid-pass (admin tab): reload before each job.
            set = await _server.RefreshSettingsAsync(stop);
            _hub.Settings = set;
            ApplyOverrides(job, set);
            if (job.Skip) continue;

            _hub.Cycle.JobIndex = i + 1;
            Status("running", $"Backing up {job.Code} ({i + 1} of {jobs.Count}).");
            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            try
            {
                var token = _jobCts.Token;
                var s = set;
                await Task.Run(() => _runner.Backup(job, s, token), token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                job.LastEnd = DateTime.UtcNow;
                job.LastResult = "interrupted";
                job.LastMessage = _hub.State.Paused ? "Stopped by Pause" : "Skipped by request";
            }
            catch (OperationCanceledException) { job.LastResult = "interrupted"; job.LastMessage = "Service stopped"; throw; }
            catch (Exception e)
            {
                job.LastEnd = DateTime.UtcNow;
                job.LastResult = "error";
                job.LastMessage = e.Message;
                _hub.Error($"{job.Code}: {e}");
            }
            finally
            {
                _jobCts.Dispose();
                _jobCts = null;
                _hub.Current = null;
                RefreshDrives();
                _hub.SaveState();
            }
        }

        if (!stop.IsCancellationRequested && !_hub.State.Paused && set.Rebalance)
            await Rebalance(set, stop);

        var end = DateTime.UtcNow;
        _hub.Mutate(s => s.LastCycleEnd = end);
        _hub.Cycle.EndedAt = end;
        _hub.Cycle.LastEndedAt = end;
        _hub.Cycle.LastDurationSec = (end - cycleStart).TotalSeconds;
        _hub.Cycle.JobIndex = 0;
        var probs = jobs.Count(j => j.LastResult is "error" or "warnings");
        _hub.Info($"Pass {_hub.Cycle.Number} finished in {StatusFmt.Dur(end - cycleStart)}" + (probs > 0 ? $"; {probs} job(s) had problems." : "."));
        if (end - cycleStart > TimeSpan.FromHours(set.IntervalHours))
            _hub.Warn($"That pass took longer than {set.IntervalHours} h, so the next one starts now.");
        RefreshDrives();
        _hub.SaveState();
    }

    /// <summary>Merge the server's job list into our state.</summary>
    private void SyncJobs(List<ServerJob> rows, ServiceSettings set)
    {
        var ready = _drives.Ready();
        _hub.Mutate(state =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var code = (r.JobCode ?? "").Trim();
                if (code.Length == 0) continue;
                seen.Add(code);
                var cat = Category.FromStatus(r.JobProjectStatus);
                if (!state.Jobs.TryGetValue(code, out var j))
                {
                    if (!Category.ShouldScan(cat)) continue;   // never had it; zipped jobs etc. aren't ours
                    j = new JobState { Code = code, FirstSeen = DateTime.UtcNow };
                    state.Jobs[code] = j;
                }
                j.Title = r.JobTitle ?? "";
                j.DbStatus = r.JobProjectStatus ?? "";
                j.Category = cat;

                var source = PathUtil.NormalizeSource(r.JobFileLocation);
                if (!string.Equals(source, j.Source, StringComparison.OrdinalIgnoreCase))
                {
                    var newRel = PathUtil.DestRelFor(source, code);
                    if (!string.IsNullOrEmpty(j.DestRel) && !string.Equals(newRel, j.DestRel, StringComparison.OrdinalIgnoreCase))
                        MoveDest(j, j.DestRel, newRel, ready);
                    if (!string.IsNullOrEmpty(j.Source)) _hub.Info($"{code}: folder changed to {source}");
                    j.Source = source;
                    j.DestRel = newRel;
                }
            }
            foreach (var j in state.Jobs.Values.Where(j => !seen.Contains(j.Code)))
            {
                j.Category = Category.Unknown;
                j.DbStatus = "(not in database)";
            }

            // Two jobs pointing at the same folder: back it up once.
            foreach (var j in state.Jobs.Values) { j.DuplicateOf = null; ApplyOverrides(j, set); }
            foreach (var g in state.Jobs.Values.Where(j => Category.ShouldScan(j.Category) && j.Source.Length > 0)
                                               .GroupBy(j => j.Source, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var first = g.OrderBy(j => j.Code).First();
                foreach (var other in g.Where(j => j != first)) other.DuplicateOf = first.Code;
            }
        });
    }

    private void ApplyOverrides(JobState j, ServiceSettings set)
    {
        var o = set.OverrideFor(j.Code);
        j.Skip = o?.Skip ?? false;
        j.RetentionDays = o?.RetentionDays ?? (j.Category == Category.Active ? set.RetentionDaysActive : set.RetentionDaysPending);
    }

    private string DefaultDestPath(string destRel) =>
        _cfg.JobsSubfolder.Length > 0 ? Path.Combine(_cfg.JobsSubfolder, destRel) : destRel;

    /// <summary>MeditechBackup.json changed (by hand or from the local page): pick it up without a restart.</summary>
    private int _seenConfig = -1;
    private void CheckConfig()
    {
        _cfg.ReloadIfChanged();
        if (_seenConfig == _cfg.Version) return;
        var first = _seenConfig < 0;
        _seenConfig = _cfg.Version;
        if (!first) _hub.Info($"Settings for this PC changed. Backup folders: {string.Join(", ", _drives.All.Select(d => d.RootDisplay))}");
        if (_hub.State.Jobs.Count == 0) _hub.LoadState();   // drives just set: pick up history saved on them
        RefreshDrives();
    }

    /// <summary>
    /// Give each new job its folder on the backup drives. If a copy already exists
    /// (from bvckup2, or ours from before), keep using it where it is so nothing is copied again.
    /// </summary>
    private void AssignDestPaths(List<BackupDrive> ready, CancellationToken stop)
    {
        var need = _hub.State.Jobs.Values.Where(j => string.IsNullOrEmpty(j.DestPath) && j.DestRel.Length > 0).OrderBy(j => j.Code).ToList();
        if (need.Count == 0) return;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in _hub.State.Jobs.Values.Where(j => !string.IsNullOrEmpty(j.DestPath)))
            foreach (var d in _drives.All) taken.Add(d.Drive + "|" + j.DestPath);

        Status("running", "Looking for existing backup folders on the drives…");
        ExistingCopies? found = null;
        int adopted = 0;
        foreach (var j in need)
        {
            var def = DefaultDestPath(j.DestRel);
            if (ready.Any(d => Directory.Exists(d.JobDir(def)))) { j.DestPath = def; continue; }
            found ??= new ExistingCopies(ready, stop);
            var folderName = Path.GetFileName(PathUtil.NormalizeSource(j.Source));
            var m = found.Match(j.Code, folderName, taken);
            if (m != null)
            {
                j.DestPath = m.Rel;
                if (string.IsNullOrEmpty(j.HomeDrive)) j.HomeDrive = m.Drive.Drive;
                taken.Add(m.Drive.Drive + "|" + m.Rel);
                adopted++;
                _hub.Info($"{j.Code}: using the existing backup folder {Path.Combine(m.Drive.RootDisplay, m.Rel)}");
            }
            else j.DestPath = def;
        }
        if (adopted > 0) _hub.Info($"Found existing backup folders for {adopted} job(s); only changed files will be copied into them.");
        _hub.SaveState();
    }

    private void MoveDest(JobState j, string oldRel, string newRel, List<BackupDrive> ready)
    {
        // Only folders we named ourselves follow the server's rename; adopted (bvckup) folders stay put.
        var moveJobDir = !string.IsNullOrEmpty(j.DestPath) && string.Equals(j.DestPath, DefaultDestPath(oldRel), StringComparison.OrdinalIgnoreCase);
        if (moveJobDir) j.DestPath = DefaultDestPath(newRel);
        foreach (var d in ready)
        {
            var pairs = new List<(string, string)> { (d.VersionDir(oldRel), d.VersionDir(newRel)) };
            if (moveJobDir) pairs.Add((d.JobDir(DefaultDestPath(oldRel)), d.JobDir(DefaultDestPath(newRel))));
            foreach (var (from, to) in pairs)
            {
                try
                {
                    if (!Directory.Exists(from) || Directory.Exists(to)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    Directory.Move(from, to);
                    _hub.Info($"{j.Code}: moved backup folder on {d.Name} to match the new server folder.");
                }
                catch (Exception e) { _hub.Warn($"{j.Code}: could not rename backup folder on {d.Name}: {e.Message}"); }
            }
        }
    }

    /// <summary>Jobs that reached 5. Complete / 6. Archived are on tape: delete their backup.</summary>
    private void RemoveCompleted(ServiceSettings set, List<BackupDrive> ready, CancellationToken stop)
    {
        if (!set.DeleteCompleted || ready.Count < _drives.All.Count) return; // wait until every drive is present
        foreach (var j in _hub.State.Jobs.Values.Where(j => j.Category == Category.Done).ToList())
        {
            stop.ThrowIfCancellationRequested();
            var shared = _hub.State.Jobs.Values.Any(o => o != j && o.Category != Category.Done &&
                                                         string.Equals(o.DestRel, j.DestRel, StringComparison.OrdinalIgnoreCase));
            try
            {
                Status("running", $"Removing {j.Code} (completed, on tape)…");
                if (!shared) _runner.DeleteJob(j, ready);
                _hub.Mutate(s => s.Jobs.Remove(j.Code));
                _hub.Info($"{j.Code}: status is \"{j.DbStatus}\", removed its backup ({StatusFmt.Bytes(j.Bytes + j.VersionBytes)}).");
            }
            catch (Exception e) { _hub.Warn($"{j.Code}: could not remove completed job: {e.Message}"); }
        }
        _hub.SaveState();
    }

    /// <summary>One drive nearly full and the other has room: move one whole job across.</summary>
    private async Task Rebalance(ServiceSettings set, CancellationToken stop)
    {
        var ready = _drives.Ready();
        if (ready.Count < 2) return;
        double FreeFrac(BackupDrive d) { var t = d.TotalBytes; return t > 0 ? (double)d.FreeBytes / t : 1; }

        foreach (var full in ready.Where(d => FreeFrac(d) < 0.10).ToList())
        {
            var target = ready.Where(d => d != full && FreeFrac(d) > 0.25).OrderByDescending(d => d.FreeBytes).FirstOrDefault();
            if (target == null) continue;
            var room = (target.FreeBytes - set.ReserveBytes) / 2;
            var job = _hub.State.Jobs.Values
                .Where(j => j.Drives.Count == 1 && j.Drives[0] == full.Drive && j.Bytes > 0 && j.Bytes < room && _hub.Current == null)
                .OrderByDescending(j => j.Bytes).FirstOrDefault();
            if (job == null) continue;

            _hub.Info($"Rebalance: moving {job.Code} ({StatusFmt.Bytes(job.Bytes)}) from {full.Name} to {target.Name}.");
            Status("running", $"Rebalancing: moving {job.Code} to {target.Name}.");
            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            try
            {
                var token = _jobCts.Token;
                await Task.Run(() => _runner.MoveJob(job, full, target, set, token), token);
                _hub.Info($"Rebalance: {job.Code} is now on {target.Name}.");
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { _hub.Warn($"Rebalance of {job.Code} stopped."); }
            catch (Exception e) { _hub.Warn($"Rebalance of {job.Code} failed: {e.Message}"); }
            finally { _jobCts.Dispose(); _jobCts = null; _hub.Current = null; _hub.SaveState(); RefreshDrives(); }
            return; // one move per pass
        }
    }

    private void RefreshDrives()
    {
        var list = new List<DriveInfoDto>();
        foreach (var d in _drives.All)
        {
            var dto = new DriveInfoDto { Path = d.RootDisplay };
            if (d.IsReady)
            {
                dto.Ok = true;
                dto.Label = d.Label;
                dto.TotalBytes = d.TotalBytes;
                dto.FreeBytes = d.FreeBytes;
                dto.Jobs = _hub.State.Jobs.Values.Count(j => j.Drives.Contains(d.Drive));
            }
            else dto.Error = "Drive not found or not ready";
            list.Add(dto);
        }
        _hub.Drives = list;
    }
}

/// <summary>Sends the status to 201 every few seconds and runs any commands queued there.</summary>
public sealed class Reporter : BackgroundService
{
    private readonly StatusHub _hub;
    private readonly ServerClient _server;
    private readonly BackupEngine _engine;
    private bool _failing;

    public Reporter(StatusHub hub, ServerClient server, BackupEngine engine) { _hub = hub; _server = server; _engine = engine; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(2000, stop);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var cmds = await _server.ReportAsync(_hub.Snapshot(), stop);
                if (_failing) { _failing = false; _hub.Info("Reporting to the server again."); }
                foreach (var c in cmds) _engine.Command(c.Cmd, "admin page");
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                if (!_failing) { _failing = true; _hub.Warn("Can't send status to the server: " + e.Message); }
            }
            try { await Task.Delay(_hub.RunState == "running" ? 5000 : 15000, stop); } catch (OperationCanceledException) { }
        }
    }
}
