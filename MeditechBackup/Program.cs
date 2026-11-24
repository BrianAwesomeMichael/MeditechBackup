using Microsoft.Extensions.Hosting.WindowsServices;

namespace MeditechBackup;

public static class Program
{
    public const string ShowEventName = @"Global\MeditechBackup.Show";

    [STAThread]
    public static int Main(string[] args)
    {
        var exeDir = AppContext.BaseDirectory;
        CrashLog.Hook(exeDir);
        var isService = WindowsServiceHelpers.IsWindowsService();

        // One copy per PC. Starting it again just brings the running copy's window forward.
        using var mutex = new Mutex(true, @"Global\MeditechBackup", out var firstInstance);
        if (!firstInstance)
        {
            if (!isService) DesktopUi.ShowOtherInstance(new LocalConfig(exeDir).WebPort);
            return 1;
        }

        var cfg = new LocalConfig(exeDir);
        var app = BuildApp(args, exeDir, cfg);

        if (isService)
        {
            app.Run();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        try { app.StartAsync().GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            MessageBox.Show($"MeditechBackup could not start.\n\nIs port {cfg.WebPort} already used by another program? " +
                            $"You can change \"web_port\" in\n{cfg.FilePath}\n\n{e.Message}",
                            "Meditech Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        using (var ui = new DesktopUi(app.Services, cfg, startHidden: args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)))
            Application.Run(ui);
        try { app.StopAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); } catch { }
        return 0;
    }

    private static WebApplication BuildApp(string[] args, string exeDir, LocalConfig cfg)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = exeDir });
        builder.Host.UseWindowsService(o => o.ServiceName = "MeditechBackup");
        builder.Logging.ClearProviders();       // our own log goes to logs\ and the status page
        builder.WebHost.UseUrls($"http://0.0.0.0:{cfg.WebPort}");

        builder.Services.AddSingleton(cfg);
        builder.Services.AddSingleton(sp => new StatusHub(cfg, exeDir));
        builder.Services.AddSingleton(sp => new ServerClient(cfg, exeDir));
        builder.Services.AddSingleton(sp => new BackupDrives(cfg));
        builder.Services.AddSingleton<JobRunner>();
        builder.Services.AddSingleton<BackupEngine>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupEngine>());
        builder.Services.AddHostedService<Reporter>();

        var app = builder.Build();

        // ---- Local status page: http://localhost:8099/ ----
        IResult Embedded(string name, string type)
        {
            var s = typeof(StatusHub).Assembly.GetManifestResourceStream(name);
            return s == null ? Results.NotFound() : Results.Stream(s, type + "; charset=utf-8");
        }
        app.MapGet("/", () => Embedded("index.html", "text/html"));
        app.MapGet("/status.js", () => Embedded("status.js", "text/javascript"));
        app.MapGet("/status.css", () => Embedded("status.css", "text/css"));
        app.MapGet("/api/status", (StatusHub hub) => Results.Json(hub.Snapshot(), Json.Options));
        app.MapPost("/api/command/{cmd}", (string cmd, BackupEngine engine, HttpContext ctx) =>
        {
            if (cmd is not ("run_now" or "pause" or "resume" or "skip")) return Results.BadRequest();
            engine.Command(cmd, IsLocal(ctx) ? "backup PC" : "status page from " + ctx.Connection.RemoteIpAddress);
            return Results.Ok(new { ok = true });
        });

        // ---- "This PC" settings (drives etc.). Changing them only works on the backup PC itself. ----
        app.MapGet("/api/local", (HttpContext ctx) => Results.Json(LocalSettingsApi.Describe(cfg, cfg.Data, IsLocal(ctx)), Json.Options));
        app.MapPost("/api/local/check", async (HttpContext ctx) =>
        {
            var d = await ctx.Request.ReadFromJsonAsync<LocalConfigData>(Json.Options) ?? new();
            return Results.Json(LocalSettingsApi.Describe(cfg, d, IsLocal(ctx)), Json.Options);
        });
        app.MapPost("/api/local", async (HttpContext ctx, BackupEngine engine, StatusHub hub) =>
        {
            if (!IsLocal(ctx)) return Results.Json(new { ok = false, error = "Settings can only be changed on the backup PC itself." }, Json.Options, statusCode: 403);
            var d = await ctx.Request.ReadFromJsonAsync<LocalConfigData>(Json.Options) ?? new();
            d.WebPort = cfg.WebPort;           // port changes need a restart; keep it out of this form
            cfg.Save(d);
            hub.Info("This PC's settings were saved from the status window.");
            engine.Command("config_changed", "backup PC");
            return Results.Json(LocalSettingsApi.Describe(cfg, cfg.Data, true), Json.Options);
        });
        return app;
    }

    private static bool IsLocal(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
}

/// <summary>Builds the "This PC" panel data: what each configured backup folder resolves to and what's in it.</summary>
public static class LocalSettingsApi
{
    public static object Describe(LocalConfig cfg, LocalConfigData d, bool canEdit)
    {
        var tmp = new LocalConfigData
        {
            Drives = d.Drives ?? new(), RootFolderName = d.RootFolderName ?? "", JobsSubfolder = d.JobsSubfolder ?? "",
            ServerUrl = d.ServerUrl, WebPort = cfg.WebPort,
        };
        var drives = (tmp.Drives).Select(LocalConfig.NormalizeDrive).Where(x => x.Length > 0).Select(x =>
        {
            var root = (tmp.RootFolderName ?? "").Trim('\\', '/');
            var bd = new BackupDrive(x, root);
            var disp = bd.RootDisplay;
            bool driveOk = false, rootExists = false; long free = 0, total = 0; int folders = 0; var sample = new List<string>();
            string? error = null;
            try
            {
                var pr = Path.GetPathRoot(disp);
                driveOk = pr != null && Directory.Exists(pr);
                if (driveOk) { free = bd.FreeBytes; total = bd.TotalBytes; }
                rootExists = Directory.Exists(disp);
                if (rootExists)
                {
                    var names = Directory.EnumerateDirectories(disp).Select(Path.GetFileName).Where(n => n != null).Select(n => n!)
                                         .Where(n => n is not "_state" and not "_versions").ToList();
                    folders = names.Count;
                    sample = names.OrderBy(n => n).Take(8).ToList();
                }
            }
            catch (Exception e) { error = e.Message; }
            return new { drive = x, folder = disp, drive_ok = driveOk, folder_exists = rootExists, free_bytes = free, total_bytes = total, folder_count = folders, sample, error };
        }).ToList();

        var available = DriveInfo.GetDrives().Where(di => { try { return di.IsReady; } catch { return false; } })
            .Select(di => new { drive = di.Name, label = SafeLabel(di), type = di.DriveType.ToString(), free_bytes = di.AvailableFreeSpace, total_bytes = di.TotalSize })
            .ToList();

        return new
        {
            can_edit = canEdit,
            file = cfg.FilePath,
            load_error = cfg.LoadError,
            config = tmp,
            drives,
            available,
            machine = Environment.MachineName,
        };
    }

    private static string SafeLabel(DriveInfo di) { try { return di.VolumeLabel; } catch { return ""; } }
}

/// <summary>Write unhandled exceptions to logs\crash.log so a restart tool's restarts can be explained.</summary>
public static class CrashLog
{
    public static void Hook(string exeDir)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(exeDir, e.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(exeDir, e.Exception.ToString()); e.SetObserved(); };
    }

    public static void Write(string exeDir, string? text)
    {
        try
        {
            var dir = Path.Combine(exeDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
