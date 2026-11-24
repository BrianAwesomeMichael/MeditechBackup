using System.Text.RegularExpressions;

namespace MeditechBackup;

public readonly record struct FileEntry(long Size, DateTime MtimeUtc)
{
    /// <summary>Same size and modified time (2 s slack for FAT/exFAT drives and NAS rounding).</summary>
    public bool SameAs(FileEntry o) => Size == o.Size && Math.Abs((MtimeUtc - o.MtimeUtc).TotalSeconds) <= 2;
}

public sealed class ScanResult
{
    public Dictionary<string, FileEntry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders we couldn't read: nothing under these is treated as deleted.</summary>
    public List<string> FailedDirs { get; } = new();
    public List<string> TempFiles { get; } = new();
    public long Bytes { get; set; }

    public bool UnderFailedDir(string rel) =>
        FailedDirs.Any(d => rel.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase));
}

/// <summary>File/folder name patterns from the admin tab ("_old", "*.tmp", ...).</summary>
public sealed class Excluder
{
    private readonly List<Regex> _rx;
    public Excluder(IEnumerable<string>? patterns)
    {
        _rx = (patterns ?? Enumerable.Empty<string>())
            .Select(p => p.Trim().Trim('\\', '/'))
            .Where(p => p.Length > 0)
            .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }
    public bool Any => _rx.Count > 0;
    public bool IsExcluded(string name) => _rx.Count > 0 && _rx.Any(r => r.IsMatch(name));
}

public static class Scanner
{
    public const string TempSuffix = ".mbtmp";

    private static readonly EnumerationOptions Opts = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,            // include hidden and system files
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Walk a folder tree. Relative paths use backslashes. Size and date come
    /// straight from the directory listing, so files are never opened.
    /// </summary>
    public static ScanResult Scan(string root, Excluder? ex, CancellationToken ct, Action<ScanResult>? progress = null)
    {
        var r = new ScanResult();
        var stack = new Stack<string>();
        stack.Push("");
        int sinceProgress = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var rel = stack.Pop();
            var full = rel.Length == 0 ? root : Path.Combine(root, rel);
            try
            {
                foreach (var fsi in new DirectoryInfo(full).EnumerateFileSystemInfos("*", Opts))
                {
                    var name = fsi.Name;
                    var childRel = rel.Length == 0 ? name : rel + "\\" + name;
                    if (fsi is DirectoryInfo d)
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; // junctions/symlinks: avoid loops
                        if (ex != null && ex.IsExcluded(name)) continue;
                        r.Dirs.Add(childRel);
                        stack.Push(childRel);
                    }
                    else if (fsi is FileInfo f)
                    {
                        if (name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) { r.TempFiles.Add(childRel); continue; }
                        if (ex != null && ex.IsExcluded(name)) continue;
                        r.Files[childRel] = new FileEntry(f.Length, f.LastWriteTimeUtc);
                        r.Bytes += f.Length;
                        if (++sinceProgress >= 500) { sinceProgress = 0; progress?.Invoke(r); }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (rel.Length == 0) throw;
                r.FailedDirs.Add(rel);
            }
        }
        progress?.Invoke(r);
        return r;
    }
}

public static class PathUtil
{
    /// <summary>
    /// Add the \\?\ prefix so long paths and names ending in a space or dot work.
    /// Also turns the database's forward slashes into backslashes.
    /// </summary>
    public static string Long(string path)
    {
        var p = path.Replace('/', '\\').TrimEnd('\\');
        if (p.StartsWith(@"\\?\")) return p;
        if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
        return @"\\?\" + Path.GetFullPath(p.Length == 2 && p[1] == ':' ? p + "\\" : p).TrimEnd('\\');
    }

    public static string Display(string path)
    {
        if (path.StartsWith(@"\\?\UNC\")) return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\")) return path.Substring(4);
        return path;
    }

    /// <summary>"//192.168.1.14/Projects2/Meditech/MTH126 - X/" -> "\\192.168.1.14\Projects2\Meditech\MTH126 - X"</summary>
    public static string NormalizeSource(string? p) => (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\');

    /// <summary>Client\JobFolder from the source path. Used under Jobs\ and _versions\ on each drive.</summary>
    public static string DestRelFor(string source, string code)
    {
        var parts = NormalizeSource(source).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var invalid = Path.GetInvalidFileNameChars();
        string Clean(string s) => new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        // Need at least \\server\share\client\job (4 parts) for a client folder.
        if (parts.Length >= 4) return Clean(parts[^2]) + "\\" + Clean(parts[^1]);
        if (parts.Length >= 3) return "_\\" + Clean(parts[^1]);
        return "_\\" + Clean(code);
    }

    public static void ForceDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    public static void ForceDeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(path, true);
        }
    }

    /// <summary>Remove empty folders under root (bottom up), keeping any in <paramref name="keep"/>.</summary>
    public static void RemoveEmptyDirs(string root, HashSet<string>? keep)
    {
        if (!Directory.Exists(root)) return;
        var dirs = Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                            .OrderByDescending(d => d.Length).ToList();
        foreach (var d in dirs)
        {
            var rel = d.Substring(root.Length).TrimStart('\\');
            if (keep != null && keep.Contains(rel)) continue;
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
    }
}
