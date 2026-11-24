using System.Text.RegularExpressions;

namespace MeditechBackup;

/// <summary>
/// Finds backup folders that already exist on the drives (e.g. made by bvckup2),
/// so a job can carry on in that folder instead of copying everything again.
/// A folder matches a job when its name equals the job's folder name on the
/// server ("MTH126 - Fix up existing Human"), or when it starts with the job
/// code ("MTH126", "MTH126 - old title").
/// </summary>
public sealed class ExistingCopies
{
    public sealed record Found(BackupDrive Drive, string Rel, string Name, DateTime Modified);

    // Looks like a job folder: letters then digits ("MTH126 - ...", "SAN064"). We don't look inside these.
    private static readonly Regex JobLike = new(@"^[A-Za-z]{2,6}\d{1,5}(?![A-Za-z0-9])", RegexOptions.Compiled);
    private const int MaxDepth = 4;

    private readonly List<Found> _all = new();

    public int Count => _all.Count;

    public ExistingCopies(IEnumerable<BackupDrive> drives, CancellationToken ct)
    {
        foreach (var d in drives)
        {
            if (!Directory.Exists(d.Root)) continue;
            var queue = new Queue<(string rel, int depth)>();
            queue.Enqueue(("", 0));
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (rel, depth) = queue.Dequeue();
                IEnumerable<DirectoryInfo> subs;
                try { subs = new DirectoryInfo(rel.Length == 0 ? d.Root : Path.Combine(d.Root, rel)).EnumerateDirectories().ToList(); }
                catch { continue; }
                foreach (var sub in subs)
                {
                    var name = sub.Name;
                    if (depth == 0 && (name.Equals("_state", StringComparison.OrdinalIgnoreCase) ||
                                       name.Equals("_versions", StringComparison.OrdinalIgnoreCase))) continue;
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var childRel = rel.Length == 0 ? name : rel + "\\" + name;
                    _all.Add(new Found(d, childRel, name, sub.LastWriteTimeUtc));
                    if (JobLike.IsMatch(name)) continue;   // a job folder: don't look inside it
                    if (depth + 1 < MaxDepth) queue.Enqueue((childRel, depth + 1));
                }
            }
        }
    }

    /// <summary>Best existing folder for this job, ignoring folders already used by another job.</summary>
    public Found? Match(string code, string sourceFolderName, ISet<string> taken)
    {
        bool Free(Found f) => !taken.Contains(f.Drive.Drive + "|" + f.Rel);
        var exact = _all.Where(f => Free(f) && f.Name.Equals(sourceFolderName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0)
        {
            var rx = new Regex("^" + Regex.Escape(code) + "(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
            exact = _all.Where(f => Free(f) && JobLike.IsMatch(f.Name) && rx.IsMatch(f.Name)).ToList();
        }
        // On more than one drive (or twice): use the one written to most recently.
        return exact.OrderByDescending(f => f.Modified).FirstOrDefault();
    }
}
