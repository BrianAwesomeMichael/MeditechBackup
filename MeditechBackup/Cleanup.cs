using System.Text.RegularExpressions;

namespace MeditechBackup;

/// <summary>
/// Frees space when a backup drive gets full (less free than <c>cleanup_free_gb</c>), oldest first, until it's
/// back above that. Only things with nothing written for <c>cleanup_min_age_days</c> (7) are deleted:
/// <list type="bullet">
/// <item>A (<c>cleanup_versions</c>): jobs' old versions, sooner than their usual retention.</item>
/// <item>B (<c>cleanup_unowned</c>): folders under the backup folder that no job uses, e.g. bvckup2 copies of
///   jobs that went to tape before MeditechBackup took over. A job's folder is never one of these, whatever
///   its status (zipped, not in the database, permanent...).</item>
/// </list>
/// Folders listed under "Safe folders" (<c>protected_folders</c>) are never deleted.
/// </summary>
public sealed class DriveCleaner
{
    // Looks like a job folder ("MTH126 - ...", "SAN064"): deleted as a whole, never looked inside (as ExistingCopies).
    private static readonly Regex JobLike = new(@"^[A-Za-z]{2,6}\d{1,5}(?![A-Za-z0-9])", RegexOptions.Compiled);
    private const int MaxDepth = 4;
    private static readonly TimeSpan MeasureAgain = TimeSpan.FromHours(24);
    private static readonly TimeSpan RecheckBeforeDelete = TimeSpan.FromHours(1);
    private static readonly TimeSpan RecheckFullDrive = TimeSpan.FromMinutes(30);
    private const int HistoryKeep = 200;

    private readonly StatusHub _hub;
    private readonly Dictionary<string, Measured> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastClean = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Measured(DateTime At, long Bytes, long Files, DateTime Newest, string? ProtectedInside);
    private sealed record Unit(BackupDrive Drive, string Rel)
    {
        public string Full => Under(Drive, Rel);
        public string Display => PathUtil.Display(Full);
        public string Key => Drive.Drive + "|" + Rel;
    }

    public DriveCleaner(StatusHub hub) { _hub = hub; }

    // ------------------------------------------------------------------ cleaning

    /// <summary>Cleans every drive that's under the free-space level. Returns the bytes freed.</summary>
    /// <param name="force">Clean even if this drive was cleaned in the last 30 minutes (a job just ran out of space).</param>
    public long CleanIfFull(ServiceSettings set, List<BackupDrive> ready, CancellationToken ct, bool force = false)
    {
        if (!set.CleanupVersions && !set.CleanupUnowned) return 0;
        if (_hub.State.Jobs.Count == 0) return 0;   // no job history loaded: can't tell what's in use
        long total = 0;
        foreach (var d in ready)
        {
            var target = set.CleanupFreeBytes;
            var free = d.FreeBytes;
            if (free >= target) continue;
            if (!force && _lastClean.TryGetValue(d.Drive, out var t) && DateTime.UtcNow - t < RecheckFullDrive) continue;
            _lastClean[d.Drive] = DateTime.UtcNow;

            _hub.Info($"Cleanup: {d.Name} has {StatusFmt.Bytes(free)} free (under {set.CleanupFreeGb:0.##} GB). " +
                      $"Deleting the oldest things with nothing written in {set.CleanupMinAgeDays} days.");
            int count = 0;
            if (set.CleanupVersions) CleanVersions(d, set, target, ct, ref count);
            if (d.FreeBytes < target && set.CleanupUnowned) CleanUnowned(d, set, target, ct, ref count);

            var after = d.FreeBytes;
            var freed = Math.Max(0, after - free);
            total += freed;
            var msg = $"{d.Name}: deleted {count} folder(s), freed {StatusFmt.Bytes(freed)}; now {StatusFmt.Bytes(after)} free.";
            if (after < target)
                msg += set.CleanupUnowned
                    ? $" Still under {set.CleanupFreeGb:0.##} GB: nothing else is old enough or allowed to be deleted."
                    : $" Still under {set.CleanupFreeGb:0.##} GB. Turning on \"Delete folders no job uses\" would free more.";
            if (after < target) _hub.Warn("Cleanup: " + msg); else _hub.Info("Cleanup: " + msg);
            SetLast(msg);
        }
        return total;
    }

    /// <summary>A: old versions older than the minimum age, oldest first.</summary>
    private void CleanVersions(BackupDrive d, ServiceSettings set, long target, CancellationToken ct, ref int count)
    {
        var cutoff = DateTime.Now.AddDays(-set.CleanupMinAgeDays);   // version folder names are local time
        var sets = new List<(JobState job, string dir, DateTime stamp)>();
        foreach (var job in _hub.State.Jobs.Values.ToList())
        {
            if (string.IsNullOrEmpty(job.DestRel)) continue;
            string vdir;
            try { vdir = d.VersionDir(job.DestRel); } catch { continue; }
            if (!Directory.Exists(vdir)) continue;
            try
            {
                foreach (var s in Directory.GetDirectories(vdir))
                    if (DateTime.TryParseExact(Path.GetFileName(s), "yyyy-MM-dd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var t) && t < cutoff)
                        sets.Add((job, s, t));
            }
            catch { }
        }

        foreach (var (job, dir, stamp) in sets.OrderBy(x => x.stamp))
        {
            if (d.FreeBytes >= target) break;
            ct.ThrowIfCancellationRequested();
            var bytes = SizeOf(dir);
            try
            {
                PathUtil.ForceDeleteDir(dir);
                count++;
                _hub.Mutate(_ => { job.VersionBytes = Math.Max(0, job.VersionBytes - bytes); job.VersionSets = Math.Max(0, job.VersionSets - 1); });
                Record(PathUtil.Display(dir), "versions", bytes, stamp.ToUniversalTime());
                _hub.Info($"Cleanup: {job.Code}: deleted old versions from {stamp:yyyy-MM-dd HH:mm} ({StatusFmt.Bytes(bytes)}) on {d.Name}.");
                TryRemoveEmpty(Path.GetDirectoryName(dir)!);
            }
            catch (Exception e) { _hub.Warn($"Cleanup: could not delete {PathUtil.Display(dir)}: {e.Message}"); }
        }
    }

    /// <summary>B: folders no job uses, the ones written to longest ago first.</summary>
    private void CleanUnowned(BackupDrive d, ServiceSettings set, long target, CancellationToken ct, ref int count)
    {
        var prot = new Protector(set.ProtectedFolders, new[] { d });
        var cutoff = DateTime.UtcNow.AddDays(-set.CleanupMinAgeDays);
        var units = FindUnits(d, prot, ct);
        var measured = new List<(Unit u, Measured m)>();
        foreach (var u in units)
        {
            ct.ThrowIfCancellationRequested();
            _hub.RunMessageOverride = $"Cleanup: checking {u.Display}…";
            var m = Measure(u, prot, ct);
            if (m != null && m.ProtectedInside == null && m.Newest < cutoff) measured.Add((u, m));
        }
        _hub.RunMessageOverride = null;

        foreach (var (u, cached) in measured.OrderBy(x => x.m.Newest))
        {
            if (d.FreeBytes >= target) break;
            ct.ThrowIfCancellationRequested();
            // Look again right before deleting if the last look wasn't recent.
            var m = DateTime.UtcNow - cached.At > RecheckBeforeDelete ? Measure(u, prot, ct, fresh: true) : cached;
            if (m == null || m.ProtectedInside != null || m.Newest >= cutoff) continue;
            try
            {
                _hub.RunMessageOverride = $"Cleanup: deleting {u.Display}…";
                PathUtil.ForceDeleteDir(u.Full);
                count++;
                _cache.Remove(Key(u, prot));
                Record(u.Display, "folder", m.Bytes, m.Newest);
                _hub.Info($"Cleanup: deleted {u.Display} ({StatusFmt.Bytes(m.Bytes)}, {m.Files:N0} files; nothing written since {m.Newest.ToLocalTime():yyyy-MM-dd}). No job uses it.");
                // Empty parent folders go too (not the backup folder itself).
                var parent = Path.GetDirectoryName(u.Rel);
                while (!string.IsNullOrEmpty(parent) && !parent.Equals("_versions", StringComparison.OrdinalIgnoreCase) &&
                       !prot.Protects(parent, Path.GetFileName(parent)))
                {
                    if (!TryRemoveEmpty(Under(d, parent))) break;
                    parent = Path.GetDirectoryName(parent);
                }
            }
            catch (Exception e) { _hub.Warn($"Cleanup: could not delete all of {u.Display}: {e.Message}"); }
            finally { _hub.RunMessageOverride = null; }
        }
    }

    // ------------------------------------------------------------------ preview

    /// <summary>Works out what B would delete (for the status page). Folder sizes are remembered for a day.</summary>
    public void Survey(ServiceSettings set, List<BackupDrive> ready, CancellationToken ct)
    {
        if (_hub.State.Jobs.Count == 0) return;
        var cutoff = DateTime.UtcNow.AddDays(-set.CleanupMinAgeDays);
        var list = new List<CleanupCandidate>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in ready)
        {
            var prot = new Protector(set.ProtectedFolders, new[] { d });
            foreach (var u in FindUnits(d, prot, ct))
            {
                ct.ThrowIfCancellationRequested();
                keys.Add(Key(u, prot));
                if (!_cache.ContainsKey(Key(u, prot))) _hub.RunMessageOverride = $"Measuring folders no job uses: {u.Display}…";
                var m = Measure(u, prot, ct);
                if (m == null) continue;
                var eligible = m.ProtectedInside == null && m.Newest < cutoff;
                list.Add(new CleanupCandidate
                {
                    Path = u.Display, Drive = d.Name, Bytes = m.Bytes, Files = m.Files, Newest = m.Newest, Eligible = eligible,
                    Note = m.ProtectedInside != null ? $"has a safe folder inside ({m.ProtectedInside})" :
                           !eligible ? $"written to in the last {set.CleanupMinAgeDays} days" : null,
                });
            }
        }
        _hub.RunMessageOverride = null;
        foreach (var k in _cache.Keys.Where(k => !keys.Contains(k)).ToList()) _cache.Remove(k);

        var old = _hub.Cleanup ?? new CleanupStatus();
        _hub.Cleanup = new CleanupStatus
        {
            SurveyedAt = DateTime.UtcNow,
            Candidates = list.OrderBy(c => c.Newest).ToList(),
            EligibleBytes = list.Where(c => c.Eligible).Sum(c => c.Bytes),
            TotalBytes = list.Sum(c => c.Bytes),
            LastRun = old.LastRun, LastMessage = old.LastMessage,
        };
    }

    // ------------------------------------------------------------------ what's in use

    /// <summary>
    /// The folders on a drive that no job uses, as whole units to delete: a job-like folder ("ABC123 - ..."),
    /// or a folder 4 levels down, or a folder with no sub-folders. Folders that hold a job's folder (or a safe
    /// folder) are looked inside instead; loose files in them are left alone.
    /// </summary>
    private List<Unit> FindUnits(BackupDrive d, Protector prot, CancellationToken ct)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in _hub.State.Jobs.Values.ToList())
        {
            if (!string.IsNullOrEmpty(j.DestPath)) owned.Add(Clean(j.DestPath));
            if (!string.IsNullOrEmpty(j.DestRel)) owned.Add("_versions\\" + Clean(j.DestRel));
        }
        var holds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_versions" };
        foreach (var o in owned)
            for (var p = Path.GetDirectoryName(o); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p)) holds.Add(p);

        var units = new List<Unit>();
        void Walk(string rel, int depth)
        {
            List<DirectoryInfo> subs;
            try { subs = new DirectoryInfo(rel.Length == 0 ? d.Root : Path.Combine(d.Root, rel)).EnumerateDirectories().ToList(); }   // only listing
            catch { return; }   // can't read it: leave it alone
            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                var name = sub.Name;
                var child = rel.Length == 0 ? name : rel + "\\" + name;
                if (depth == 0 && name.Equals("_state", StringComparison.OrdinalIgnoreCase)) continue;
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (owned.Contains(child) || prot.Protects(child, name)) continue;
                if (holds.Contains(child) || prot.HasInside(child)) { Walk(child, depth + 1); continue; }
                if (JobLike.IsMatch(name) || depth + 1 >= MaxDepth || !HasSubdirs(sub)) units.Add(new Unit(d, child));
                else Walk(child, depth + 1);
            }
        }
        Walk("", 0);
        return units;
    }

    private Measured? Measure(Unit u, Protector prot, CancellationToken ct, bool fresh = false)
    {
        var key = Key(u, prot);
        if (!fresh && _cache.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < MeasureAgain) return c;
        try
        {
            var di = new DirectoryInfo(u.Full);
            if (!di.Exists) { _cache.Remove(key); return null; }
            long bytes = 0, files = 0, n = 0;
            var newest = Max(di.LastWriteTimeUtc, di.CreationTimeUtc);
            string? inside = null;
            foreach (var fsi in di.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if ((++n & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                var t = Max(fsi.LastWriteTimeUtc, fsi.CreationTimeUtc);   // created = when it was copied there
                if (t > newest) newest = t;
                if (fsi is FileInfo f) { bytes += f.Length; files++; }
                else if (inside == null && prot.NameProtected(fsi.Name)) inside = fsi.Name;
            }
            var m = new Measured(DateTime.UtcNow, bytes, files, newest, inside);
            _cache[key] = m;
            return m;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _hub.Warn($"Cleanup: could not look inside {u.Display}: {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Record(string path, string kind, long bytes, DateTime? newest)
    {
        _hub.Mutate(s =>
        {
            s.CleanupHistory ??= new();
            s.CleanupHistory.Insert(0, new CleanupDeleted { At = DateTime.UtcNow, Path = path, Kind = kind, Bytes = bytes, Newest = newest });
            if (s.CleanupHistory.Count > HistoryKeep) s.CleanupHistory.RemoveRange(HistoryKeep, s.CleanupHistory.Count - HistoryKeep);
        });
        _hub.SaveState();
    }

    private void SetLast(string msg)
    {
        var c = _hub.Cleanup ?? new CleanupStatus();
        _hub.Cleanup = new CleanupStatus
        {
            SurveyedAt = c.SurveyedAt, Candidates = c.Candidates, EligibleBytes = c.EligibleBytes, TotalBytes = c.TotalBytes,
            LastRun = DateTime.UtcNow, LastMessage = msg,
        };
    }

    /// <summary>A folder under the drive's backup folder. Never the backup folder itself, _state or _versions
    /// (BackupDrive.JobDir refuses _versions paths, which the cleanup does need).</summary>
    private static string Under(BackupDrive d, string rel)
    {
        var r = Clean(rel);
        if (r.Length == 0 || r.Split('\\').Any(p => p is "" or "." or "..") ||
            r.Equals("_state", StringComparison.OrdinalIgnoreCase) || r.StartsWith("_state\\", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("_versions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to use backup folder path \"{rel}\".");
        return Path.Combine(d.Root, r);
    }

    private static string Key(Unit u, Protector p) => u.Key + "|" + p.Signature;
    private static string Clean(string rel) => rel.Replace('/', '\\').Trim().Trim('\\');
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static bool HasSubdirs(DirectoryInfo d)
    {
        try { return d.EnumerateDirectories().Any(); } catch { return true; }   // unsure: look inside rather than delete whole
    }

    private static long SizeOf(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }).Sum(f => f.Length); }
        catch { return 0; }
    }

    private static bool TryRemoveEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) { Directory.Delete(dir); return true; } }
        catch { }
        return false;
    }

    /// <summary>
    /// "Safe folders" from the settings. An entry with a backslash is a path under the backup folder
    /// ("Archive\Keep", or the full "E:\Backups\Archive\Keep"): that folder and everything in it. An entry
    /// without one is a folder name ("KEEP", "*_keep"): any folder with that name, wherever it is.
    /// </summary>
    public sealed class Protector
    {
        private readonly List<string> _paths = new();
        private readonly List<Regex> _names = new();
        public string Signature { get; }

        public Protector(IEnumerable<string>? entries, IEnumerable<BackupDrive> drives)
        {
            var roots = drives.Select(d => d.RootDisplay.TrimEnd('\\') + "\\").ToList();
            foreach (var raw in entries ?? Enumerable.Empty<string>())
            {
                var e = (raw ?? "").Trim().Trim('"').Replace('/', '\\').Trim('\\');
                foreach (var r in roots)
                    if (e.StartsWith(r, StringComparison.OrdinalIgnoreCase)) { e = e.Substring(r.Length).Trim('\\'); break; }
                if (e.Length == 0) continue;
                if (e.Contains('\\')) _paths.Add(e);
                else _names.Add(new Regex("^" + Regex.Escape(e).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                                          RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            Signature = string.Join("/", _paths.Concat(_names.Select(r => r.ToString()))).ToLowerInvariant().GetHashCode().ToString("x");
        }

        /// <summary>This folder is safe (it, or a folder it's in, is listed).</summary>
        public bool Protects(string rel, string name) =>
            NameProtected(name) ||
            _paths.Any(p => rel.Equals(p, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)) ||
            // Any folder on the way down with a safe name protects what's in it.
            rel.Split('\\').Any(NameProtected);

        /// <summary>A listed safe folder is somewhere inside this one: look inside instead of deleting it whole.</summary>
        public bool HasInside(string rel) => _paths.Any(p => p.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase));

        public bool NameProtected(string name) => _names.Any(r => r.IsMatch(name));
    }
}
