using System.Net.Http.Json;
using System.Text.Json;

namespace MeditechBackup;

/// <summary>Talks to backupService/api.php on 201.</summary>
public sealed class ServerClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly LocalConfig _cfg;
    private readonly string _cachePath;
    private ServiceSettings _settings = new();

    public ServerClient(LocalConfig cfg, string exeDir)
    {
        _cfg = cfg;
        _cachePath = Path.Combine(exeDir, "settings-cache.json");
        try
        {
            if (File.Exists(_cachePath))
                _settings = JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(_cachePath), Json.Options) ?? new();
        }
        catch { /* use defaults */ }
    }

    /// <summary>Last settings we managed to load (cached on disk so a server outage keeps the old values).</summary>
    public ServiceSettings Settings => _settings;

    private string Url(string action) => _cfg.ServerUrl + (_cfg.ServerUrl.Contains('?') ? "&" : "?") + "action=" + action;

    private sealed class JobsReply { public bool Ok { get; set; } public string? Error { get; set; } public List<ServerJob>? Jobs { get; set; } }
    private sealed class SettingsReply { public bool Ok { get; set; } public ServiceSettings? Settings { get; set; } }
    public sealed class Command { public string Cmd { get; set; } = ""; }
    private sealed class ReportReply { public bool Ok { get; set; } public List<Command>? Commands { get; set; } public ServiceSettings? Settings { get; set; } }

    /// <summary>All not-yet-on-tape jobs, plus the current status of every code we hold.</summary>
    public async Task<List<ServerJob>> GetJobsAsync(IEnumerable<string> heldCodes, CancellationToken ct)
    {
        var resp = await PostJson("jobs", new { codes = heldCodes.ToArray() }, ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<JobsReply>(Json.Options, ct);
        if (r is null || !r.Ok || r.Jobs is null) throw new Exception("Server returned an error: " + (r?.Error ?? "no data"));
        return r.Jobs;
    }

    public async Task<ServiceSettings> RefreshSettingsAsync(CancellationToken ct)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<SettingsReply>(Url("settings"), Json.Options, ct);
            if (r?.Settings != null) Apply(r.Settings);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* keep cached */ }
        return _settings;
    }

    /// <summary>Send the status snapshot. Returns any commands queued from the admin tab.</summary>
    public async Task<List<Command>> ReportAsync(StatusSnapshot snap, CancellationToken ct)
    {
        var resp = await PostJson("report", snap, ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<ReportReply>(Json.Options, ct);
        if (r?.Settings != null) Apply(r.Settings);
        return r?.Commands ?? new();
    }

    /// <summary>POST with a Content-Length (not chunked) so Apache/PHP always sees the body.</summary>
    private Task<HttpResponseMessage> PostJson<T>(string action, T body, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(body, Json.Options), System.Text.Encoding.UTF8, "application/json");
        return _http.PostAsync(Url(action), content, ct);
    }

    private void Apply(ServiceSettings s)
    {
        s.JobOverrides = new Dictionary<string, JobOverride>(s.JobOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        s.ExcludePatterns ??= new();
        s.ProtectedFolders ??= new();
        s.PermanentFolders ??= new();
        if (s.CleanupMinAgeDays < 1) s.CleanupMinAgeDays = 1;
        if (s.IntervalHours < 0.25) s.IntervalHours = 0.25;
        _settings = s;
        try { Json.WriteAtomic(_cachePath, s); } catch { }
    }
}
