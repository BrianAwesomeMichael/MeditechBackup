using System.Buffers;
using System.Diagnostics;

namespace MeditechBackup;

/// <summary>Backs up one job: mirror source -> backup drives, keeping old versions.</summary>
public sealed class JobRunner
{
    private readonly StatusHub _hub;
    private readonly BackupDrives _drives;
    private const int MaxLoggedFileErrors = 15;

    public JobRunner(StatusHub hub, BackupDrives drives) { _hub = hub; _drives = drives; }

    private sealed class Run
    {
        public long Copied, CopiedBytes, Versioned, Deleted, Errors;
        public bool Spilled;
        public string? FirstError;
    }

    public void Backup(JobState job, ServiceSettings set, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var cur = new CurrentJob { Code = job.Code, Title = job.Title, Phase = "Scanning source", StartedAt = started };
        _hub.Current = cur;
        job.LastStart = started;
        var run = new Run();

        if (string.IsNullOrEmpty(job.DestPath) || string.IsNullOrEmpty(job.DestRel))
        {
            Finish(job, "error", "No backup folder assigned yet (needs a pass with the drives connected).", run);
            return;
        }
        var src = PathUtil.Long(job.Source);
        if (!Directory.Exists(src))
        {
            Finish(job, "error", $"Source folder not found: {job.Source}", run);
            return;
        }

        var ready = _drives.Ready();
        if (ready.Count == 0) { Finish(job, "error", "No backup drive is available.", run); return; }

        // ---- 1. Scan the source (over the network: the slow part) ----
        var ex = new Excluder(set.ExcludePatterns);
        ScanResult s;
        try { s = Scanner.Scan(src, ex, ct, r => cur.FilesTotal = r.Files.Count); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Finish(job, "error", "Could not read source: " + e.Message, run);
            return;
        }
        cur.FilesTotal = s.Files.Count;
        foreach (var fd in s.FailedDirs.Take(5)) _hub.Warn($"{job.Code}: could not read folder {fd} (its backup copy is left alone)");

        // ---- 2. Scan what we already have on each drive (local, fast) ----
        cur.Phase = "Checking backup copy";
        var have = new Dictionary<string, (BackupDrive drive, FileEntry e)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in ready)
        {
            var dir = d.JobDir(job.DestPath);
            if (!Directory.Exists(dir)) continue;
            var ds = Scanner.Scan(dir, null, ct);
            foreach (var t in ds.TempFiles) TryDelete(Path.Combine(dir, t)); // leftovers from a crash
            foreach (var (rel, e) in ds.Files)
            {
                if (!have.TryGetValue(rel, out var prev)) { have[rel] = (d, e); continue; }
                // Same file on two drives (after a spill or an interrupted move): keep the better one.
                s.Files.TryGetValue(rel, out var se);
                bool prevGood = prev.e.SameAs(se), thisGood = e.SameAs(se);
                bool keepThis = thisGood && !prevGood || (thisGood == prevGood && d.Drive == job.HomeDrive);
                (BackupDrive drive, FileEntry e) loser = keepThis ? prev : (d, e);
                TryDelete(Path.Combine(loser.drive.JobDir(job.DestPath), rel));
                if (keepThis) have[rel] = (d, e);
            }
        }

        // ---- 3. Home drive ----
        var home = _drives.ByName(job.HomeDrive);
        if (home == null || !ready.Contains(home))
        {
            home = have.Count > 0
                ? have.Values.GroupBy(v => v.drive).OrderByDescending(g => g.Sum(x => x.e.Size)).First().Key
                : ready.OrderByDescending(d => d.FreeBytes).First();
            if (!string.IsNullOrEmpty(job.HomeDrive) && job.HomeDrive != home.Drive)
                _hub.Warn($"{job.Code}: home drive {job.HomeDrive} not available, using {home.Name}");
            job.HomeDrive = home.Drive;
        }

        // ---- 4. Copy new and changed files ----
        var todo = s.Files.Where(kv => !have.TryGetValue(kv.Key, out var h) || !h.e.SameAs(kv.Value))
                          .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        cur.BytesToCopy = todo.Sum(kv => kv.Value.Size);
        cur.FilesDone = s.Files.Count - todo.Count;
        cur.Phase = todo.Count == 0 ? "Up to date" : $"Copying {todo.Count:N0} files";
        var speed = new SpeedMeter(cur);
        var throttle = set.MaxMbPerSec > 0 ? set.MaxMbPerSec * 1024 * 1024 : 0;

        foreach (var (rel, se) in todo)
        {
            ct.ThrowIfCancellationRequested();
            cur.CurrentFile = rel;
            have.TryGetValue(rel, out var old);
            var target = PickDrive(old.drive, home, ready, se.Size, set.ReserveBytes);
            if (target == null)
            {
                Finish(job, "error", $"Out of space: no backup drive has room for {rel} ({StatusFmt.Bytes(se.Size)}) while keeping {set.ReserveFreeGb} GB free.", run);
                return;
            }
            if (target != home && !run.Spilled) { run.Spilled = true; _hub.Warn($"{job.Code}: {home.Name} is full, continuing on {target.Name}"); }

            var dest = Path.Combine(target.JobDir(job.DestPath), rel);
            var tmp = dest + Scanner.TempSuffix;
            try
            {
                CopyFile(Path.Combine(src, rel), tmp, se, speed, throttle, ct);
                if (old.drive != null)
                {
                    PreserveOld(job, old.drive, rel, stamp, ct);
                    run.Versioned++;
                }
                File.Move(tmp, dest, true);
                run.Copied++; run.CopiedBytes += se.Size;
                cur.FilesCopied++;
            }
            catch (OperationCanceledException) { TryDelete(tmp); throw; }
            catch (Exception e)
            {
                TryDelete(tmp);
                FileError(job, run, rel, e);
            }
            cur.FilesDone++;
        }
        cur.CurrentFile = null;

        // ---- 5. Files deleted at the source -> old versions ----
        cur.Phase = "Handling deleted files";
        var gone = have.Keys.Where(rel => !s.Files.ContainsKey(rel) && !s.UnderFailedDir(rel)).ToList();
        if (gone.Count > 100 && gone.Count >= have.Count * 0.9)
        {
            // Almost everything vanished: far more likely a share problem than a real cleanup.
            _hub.Warn($"{job.Code}: {gone.Count:N0} of {have.Count:N0} files are missing at the source; NOT removing them from the backup. " +
                      "If this is real, the job will need its backup cleared by hand.");
            run.Errors++;
            run.FirstError ??= $"{gone.Count:N0} files missing at source (safety stop, nothing removed)";
        }
        else
        {
            foreach (var rel in gone)
            {
                ct.ThrowIfCancellationRequested();
                try { PreserveOld(job, have[rel].drive, rel, stamp, ct); run.Deleted++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { FileError(job, run, rel, e); }
            }
        }

        // ---- 6. Folders: mirror empty source folders, drop ones that are gone ----
        foreach (var d in s.Dirs)
            if (!ready.Any(r => Directory.Exists(Path.Combine(r.JobDir(job.DestPath), d))))
                try { Directory.CreateDirectory(Path.Combine(home.JobDir(job.DestPath), d)); } catch { }
        foreach (var d in ready)
        {
            var keep = d == home ? s.Dirs : null;
            PathUtil.RemoveEmptyDirs(d.JobDir(job.DestPath), keep);
            if (d != home) TryRemoveEmpty(d.JobDir(job.DestPath));
        }

        // ---- 7. Prune old versions ----
        cur.Phase = "Pruning old versions";
        PruneVersions(job, ready);

        job.Files = s.Files.Count;
        job.Bytes = s.Bytes;
        job.Drives = ready.Where(d => Directory.Exists(d.JobDir(job.DestPath))).Select(d => d.Drive).ToList();
        job.LastCopiedFiles = run.Copied;
        job.LastCopiedBytes = run.CopiedBytes;
        job.LastVersionedFiles = run.Versioned + run.Deleted;

        var msg = run.Copied == 0 && run.Deleted == 0 ? "No changes" :
                  $"{run.Copied:N0} copied ({StatusFmt.Bytes(run.CopiedBytes)})" +
                  (run.Versioned + run.Deleted > 0 ? $", {run.Versioned + run.Deleted:N0} old versions {(job.RetentionDays > 0 ? "kept" : "removed")}" : "");
        if (run.Errors > 0) msg += $"; {run.Errors:N0} problem(s), first: {run.FirstError}";
        Finish(job, run.Errors > 0 ? "warnings" : "ok", msg, run);
        _hub.Info($"{job.Code}: {msg} [{StatusFmt.Dur(DateTime.UtcNow - started)}]");
    }

    private void Finish(JobState job, string result, string msg, Run run)
    {
        job.LastEnd = DateTime.UtcNow;
        job.LastResult = result;
        job.LastMessage = msg;
        job.LastErrors = run.Errors;
        if (result is "ok" or "warnings") job.LastSuccess = job.LastEnd;
        if (result == "error") _hub.Error($"{job.Code}: {msg}");
    }

    private void FileError(JobState job, Run run, string rel, Exception e)
    {
        run.Errors++;
        var why = e is IOException && (e.HResult & 0xFFFF) is 32 or 33 ? "file is open/locked" : e.Message;
        run.FirstError ??= $"{rel}: {why}";
        if (run.Errors <= MaxLoggedFileErrors) _hub.Warn($"{job.Code}: {rel}: {why}");
        else if (run.Errors == MaxLoggedFileErrors + 1) _hub.Warn($"{job.Code}: more file problems, not logging each one");
    }

    private static BackupDrive? PickDrive(BackupDrive? current, BackupDrive home, List<BackupDrive> ready, long size, long reserve)
    {
        var order = new List<BackupDrive>();
        if (current != null) order.Add(current);
        if (!order.Contains(home)) order.Add(home);
        order.AddRange(ready.Where(d => !order.Contains(d)).OrderByDescending(d => d.FreeBytes));
        // Replacing a file in place frees the old copy afterwards, but the new copy is written first.
        return order.FirstOrDefault(d => d.FreeBytes - size > reserve);
    }

    /// <summary>Move the backup's copy of a file aside (or delete it when this job keeps no versions).</summary>
    private void PreserveOld(JobState job, BackupDrive drive, string rel, string stamp, CancellationToken ct)
    {
        var path = Path.Combine(drive.JobDir(job.DestPath), rel);
        if (!File.Exists(path)) return;
        if (job.RetentionDays <= 0) { PathUtil.ForceDeleteFile(path); return; }
        var to = Path.Combine(drive.VersionDir(job.DestRel), stamp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(path, to, true); // same drive: instant
    }

    public void PruneVersions(JobState job, List<BackupDrive> ready)
    {
        var cutoff = DateTime.Now.AddDays(-job.RetentionDays);
        long bytes = 0; int sets = 0;
        foreach (var d in ready)
        {
            var vdir = d.VersionDir(job.DestRel);
            if (!Directory.Exists(vdir)) continue;
            foreach (var setDir in Directory.GetDirectories(vdir))
            {
                var name = Path.GetFileName(setDir);
                bool old = !DateTime.TryParseExact(name, "yyyy-MM-dd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var t)
                           ? false : (job.RetentionDays <= 0 || t < cutoff);
                if (old)
                {
                    try { PathUtil.ForceDeleteDir(setDir); continue; }
                    catch (Exception e) { _hub.Warn($"{job.Code}: could not remove old versions {name}: {e.Message}"); }
                }
                sets++;
                try
                {
                    bytes += new DirectoryInfo(setDir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                                                      .Sum(f => f.Length);
                }
                catch { }
            }
            TryRemoveEmpty(vdir);
        }
        job.VersionBytes = bytes;
        job.VersionSets = sets;
    }

    /// <summary>Delete everything this job has on the backup drives.</summary>
    public void DeleteJob(JobState job, List<BackupDrive> ready)
    {
        foreach (var d in ready)
        {
            if (string.IsNullOrEmpty(job.DestPath)) { PathUtil.ForceDeleteDir(d.VersionDir(job.DestRel)); continue; } // never had a copy
            PathUtil.ForceDeleteDir(d.JobDir(job.DestPath));
            PathUtil.ForceDeleteDir(d.VersionDir(job.DestRel));
            var parent = Path.GetDirectoryName(d.JobDir(job.DestPath))!;          // client folder
            if (!string.Equals(parent, d.Root, StringComparison.OrdinalIgnoreCase)) TryRemoveEmpty(parent);
            TryRemoveEmpty(Path.GetDirectoryName(d.VersionDir(job.DestRel))!);
        }
    }

    /// <summary>Move a whole job's current copy to another drive (rebalancing).</summary>
    public void MoveJob(JobState job, BackupDrive from, BackupDrive to, ServiceSettings set, CancellationToken ct)
    {
        var srcDir = from.JobDir(job.DestPath);
        var s = Scanner.Scan(srcDir, null, ct);
        var cur = new CurrentJob
        {
            Code = job.Code, Title = job.Title, Phase = $"Moving to {to.Name} (rebalance)",
            FilesTotal = s.Files.Count, BytesToCopy = s.Bytes, StartedAt = DateTime.UtcNow,
        };
        _hub.Current = cur;
        var speed = new SpeedMeter(cur);
        var dstDir = to.JobDir(job.DestPath);
        foreach (var (rel, e) in s.Files)
        {
            ct.ThrowIfCancellationRequested();
            cur.CurrentFile = rel;
            var dest = Path.Combine(dstDir, rel);
            if (File.Exists(dest) && new FileInfo(dest) is var fi && new FileEntry(fi.Length, fi.LastWriteTimeUtc).SameAs(e)) { cur.FilesDone++; continue; }
            CopyFile(Path.Combine(srcDir, rel), dest + Scanner.TempSuffix, e, speed, 0, ct);
            File.Move(dest + Scanner.TempSuffix, dest, true);
            cur.FilesDone++; cur.FilesCopied++;
        }
        foreach (var d in s.Dirs) Directory.CreateDirectory(Path.Combine(dstDir, d));
        job.HomeDrive = to.Drive;   // switch before deleting, so an interruption keeps the new copy
        PathUtil.ForceDeleteDir(srcDir);
        var parent = Path.GetDirectoryName(srcDir)!;
        if (!string.Equals(parent, from.Root, StringComparison.OrdinalIgnoreCase)) TryRemoveEmpty(parent);
        job.Drives = new List<string> { to.Drive };
    }

    // ---------------------------------------------------------------

    private static void CopyFile(string from, string tmp, FileEntry e, SpeedMeter speed, double maxBytesPerSec, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        const int BufSize = 4 * 1024 * 1024;
        var buf = ArrayPool<byte>.Shared.Rent(BufSize);
        try
        {
            using (var fin = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            using (var fout = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                try { if (fin.Length > 0) fout.SetLength(fin.Length); } catch { }
                var sw = Stopwatch.StartNew();
                long written = 0;
                int n;
                while ((n = fin.Read(buf, 0, BufSize)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    fout.Write(buf, 0, n);
                    written += n;
                    speed.Add(n);
                    if (maxBytesPerSec > 0)
                    {
                        var ahead = written / maxBytesPerSec - sw.Elapsed.TotalSeconds;
                        if (ahead > 0.02) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(ahead, 5)));
                    }
                }
                fout.SetLength(written);
            }
            // The date we saw while scanning: if the file changed during the copy, the next pass sees a difference and copies it again.
            File.SetLastWriteTimeUtc(tmp, e.MtimeUtc);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) PathUtil.ForceDeleteFile(path); } catch { } }
    private static void TryRemoveEmpty(string dir) { try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { } }

    private sealed class SpeedMeter
    {
        private readonly CurrentJob _cur;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _windowBytes; private double _windowStart;
        public SpeedMeter(CurrentJob cur) { _cur = cur; }
        public void Add(long n)
        {
            _cur.BytesCopied += n;
            _windowBytes += n;
            var now = _sw.Elapsed.TotalSeconds;
            if (now - _windowStart >= 2)
            {
                _cur.SpeedBps = _windowBytes / (now - _windowStart);
                _windowBytes = 0; _windowStart = now;
            }
        }
    }
}

public static class StatusFmt
{
    public static string Bytes(long n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = n; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{n} B" : $"{v:0.##} {u[i]}";
    }
    public static string Dur(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";
}
