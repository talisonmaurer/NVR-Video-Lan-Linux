using System.Net;
using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using NvrVideoLan;

// An installed NVR runs in the background at Windows startup.
// The installer puts an explicit marker next to the executable, so manually
// published builds still default to local-only and preserve existing behavior.
bool installedMode = File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.marker"));
string bindAddress = Environment.GetEnvironmentVariable("NVR_BIND")
    ?? (installedMode ? "http://0.0.0.0:5088" : "http://127.0.0.1:5088");
string data = Environment.GetEnvironmentVariable("NVR_DATA")
    ?? (installedMode
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NVR Video LAN", "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NvrVideoLan", "data"));
// The recording disk and quota are selected in Sistema > Armazenamento.
// The PowerShell environment only supplies the persistent data directory.
string preview = Path.Combine(data, "preview");
Directory.CreateDirectory(data);
Directory.CreateDirectory(preview);

var security = new SecuritySettings(data);
// The installed program must listen on LAN even before first setup, but until
// a password is configured every protected endpoint remains loopback-only.
// Non-installed developer builds preserve the old fail-closed bind rule.
if (!installedMode && (!security.Configured || !security.PasswordEnabled) &&
    !IsLoopbackBind(bindAddress))
    throw new InvalidOperationException(
        "Antes de liberar acesso LAN, defina uma senha no NVR local.");

// Resolve web assets relative to the published executable, not PowerShell's current
// working directory. This also supports launching the EXE from a UNC share.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.WebHost.UseUrls(bindAddress);

string ffmpegExecutable = FfmpegProvisioner.Resolve(
    Environment.GetEnvironmentVariable("NVR_FFMPEG"), data);
Console.WriteLine("NVR FFmpeg: " + ffmpegExecutable);
var storage = new StorageSettings(data);
var config = new NvrConfig(data, storage.Root, preview,
    ffmpegExecutable, 0, 0);
if (!storage.IsConfigured)
    Console.WriteLine("Gravacao aguardando configuracao em Sistema > Armazenamento.");
else
    Console.WriteLine("Disco de gravacoes: " + storage.Root);

builder.Services.AddSingleton(storage);
builder.Services.AddSingleton(new LinuxMonitorSettings(data));
builder.Services.AddSingleton(new SystemMaintenance(data));
builder.Services.AddSingleton(new DesktopOptionsService(data));
builder.Services.AddSingleton<NetworkTelemetry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkTelemetry>());
builder.Services.AddSingleton<LoginLimiter>();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new CameraStore(Path.Combine(data, "cameras.json")));
builder.Services.AddSingleton<OnvifService>();
builder.Services.AddSingleton<RecorderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecorderService>());
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "NVR.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.LoginPath = "/";
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
// Always serve the UI embedded in this executable. A stale wwwroot directory
// beside the old shared EXE must not override the new interface.
app.UseAuthentication();
app.UseAuthorization();

// Protect API, live video and recordings based on the saved choice.
// This middleware is dynamic, allowing the first-run wizard to set the mode
// without restarting the server. Only setup/session/login are public APIs.
app.Use(async (ctx, next) =>
{
    string path = ctx.Request.Path.Value ?? "";
    bool protectedPath =
        (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
         !path.Equals("/api/session", StringComparison.OrdinalIgnoreCase) &&
         !path.Equals("/api/setup", StringComparison.OrdinalIgnoreCase) &&
         !path.Equals("/api/login", StringComparison.OrdinalIgnoreCase)) ||
        path.StartsWith("/hls/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/recordings/", StringComparison.OrdinalIgnoreCase);
    if (protectedPath)
    {
        if (!security.Configured)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (security.PasswordEnabled &&
            (ctx.User.Identity?.IsAuthenticated != true ||
             ctx.User.FindFirst("nvr.security-revision")?.Value != security.Revision))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!security.PasswordEnabled && !IsTrustedLocalRequest(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});

// Serve dashboard assets from the executable when the physical wwwroot folder
// is missing (for instance, when distributing only the compiled EXE).
app.MapGet("/", () => EmbeddedWebAsset("NvrVideoLan.Web.index.html", "text/html; charset=utf-8"));
app.MapGet("/index.html", () => EmbeddedWebAsset("NvrVideoLan.Web.index.html", "text/html; charset=utf-8"));
app.MapGet("/app.js", () => EmbeddedWebAsset("NvrVideoLan.Web.app.js", "text/javascript; charset=utf-8"));
app.MapGet("/style.css", () => EmbeddedWebAsset("NvrVideoLan.Web.style.css", "text/css; charset=utf-8"));
app.MapGet("/hls.min.js", () => EmbeddedWebAsset("NvrVideoLan.Web.hls.min.js", "text/javascript; charset=utf-8"));

// The kiosk viewer is strictly loopback-only, even when the main NVR listens
// on the entire LAN. Never reuse the administrative session on this viewer.
app.MapGet("/monitor", (HttpContext ctx) =>
    IsTrustedLocalRequest(ctx)
        ? EmbeddedWebAsset("NvrVideoLan.Web.monitor.html", "text/html; charset=utf-8")
        : Results.NotFound());
app.MapGet("/monitor.js", (HttpContext ctx) =>
    IsTrustedLocalRequest(ctx)
        ? EmbeddedWebAsset("NvrVideoLan.Web.monitor.js", "text/javascript; charset=utf-8")
        : Results.NotFound());

app.MapGet("/monitor/config", (HttpContext ctx,
    DesktopOptionsService desktop, CameraStore cameras, LinuxMonitorSettings linuxMonitor) =>
{
    if (!IsTrustedLocalRequest(ctx)) return Results.NotFound();
    if (OperatingSystem.IsLinux())
    {
        var layout = linuxMonitor.Current(cameras);
        return Results.Ok(new { rows = layout.Rows, columns = layout.Columns,
            cameras = layout.CameraIds.Select(id => cameras.Get(id)).Where(c => c is { PreviewEnabled: true })
                .Select(c => new { id = c!.Id, name = c.Name, audioEnabled = c.PreviewAudio }).ToArray() });
    }
    var options = desktop.Current;
    if (!desktop.Installed || !options.MonitorEnabled ||
        options.MonitorCameraId is not Guid id ||
        cameras.Get(id) is not { PreviewEnabled: true } camera)
        return Results.NotFound();
    return Results.Ok(new
    {
        id, name = camera.Name, audioEnabled = camera.PreviewAudio
    });
});

app.MapGet("/monitor/hls/{id:guid}/{file}",
    (Guid id, string file, HttpContext ctx,
        DesktopOptionsService desktop, CameraStore cameras, NvrConfig cfg, LinuxMonitorSettings linuxMonitor) =>
    {
        if (OperatingSystem.IsLinux())
        {
            if (!IsTrustedLocalRequest(ctx) || !linuxMonitor.Current(cameras).CameraIds.Contains(id) || cameras.Get(id) is not { PreviewEnabled: true }) return Results.NotFound();
            return VideoFile(cameras.Get(id), cfg.PreviewRoot, id, file, true);
        }
        var options = desktop.Current;
        if (!IsTrustedLocalRequest(ctx) || !desktop.Installed ||
            !options.MonitorEnabled || options.MonitorCameraId != id)
            return Results.NotFound();
        return VideoFile(cameras.Get(id), cfg.PreviewRoot, id, file, true);
    });

app.MapGet("/api/session", (HttpContext ctx) =>
    Results.Ok(new
    {
        setupRequired = !security.Configured,
        passwordEnabled = security.PasswordEnabled,
        authenticated = security.Configured &&
            (security.PasswordEnabled
                ? (ctx.User.Identity?.IsAuthenticated == true &&
                   ctx.User.FindFirst("nvr.security-revision")?.Value == security.Revision)
                : IsTrustedLocalRequest(ctx))
    }));

app.MapPost("/api/setup", async (SecuritySetupRequest request, HttpContext ctx) =>
{
    if (!IsTrustedLocalRequest(ctx) || !IsSafeStateChangingRequest(ctx))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        security.FirstSetup(request);
        if (request.UsePassword)
            await SignInAdmin(ctx, security.Revision);
        return Results.Ok(new { ok = true, passwordEnabled = security.PasswordEnabled });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapPost("/api/login", async (LoginRequest req, HttpContext ctx, LoginLimiter limiter) =>
{
    if (!security.Configured) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!security.PasswordEnabled) return Results.Ok(new { ok = true });
    string key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!limiter.CanAttempt(key))
        return Results.Problem("Muitas tentativas de senha. Tente novamente em 90 segundos.", statusCode: 429);
    if (!security.CheckPassword(req.Password))
    {
        limiter.Failed(key);
        return Results.Unauthorized();
    }
    limiter.Succeeded(key);
    await SignInAdmin(ctx, security.Revision);
    return Results.Ok(new { ok = true });
});

var api = app.MapGroup("/api");
api.MapPost("/security/change", async (SecurityChangeRequest request, HttpContext ctx, LoginLimiter limiter) =>
{
    if (!IsTrustedLocalRequest(ctx) || !IsSafeStateChangingRequest(ctx))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!request.UsePassword && !IsLoopbackBind(bindAddress))
        return Results.BadRequest(new { error = "Para desativar a senha, configure o NVR para ouvir apenas em 127.0.0.1 e reinicie." });
    string key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!limiter.CanAttempt(key))
        return Results.Problem("Muitas tentativas de senha. Aguarde 90 segundos.", statusCode: 429);
    try
    {
        security.Change(request);
        limiter.Succeeded(key);
        if (request.UsePassword)
            await SignInAdmin(ctx, security.Revision);
        else
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok(new { ok = true, passwordEnabled = security.PasswordEnabled });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException) { limiter.Failed(key); return Results.Unauthorized(); }
});
api.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Ok(new { ok = true });
});

api.MapGet("/system/update/status", (SystemMaintenance maintenance) =>
    Results.Ok(maintenance.GetStatus()));

api.MapPost("/system/restart", (SystemMaintenance maintenance,
    IHostApplicationLifetime lifetime, HttpContext ctx) =>
{
    if (!IsSafeStateChangingRequest(ctx))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return Results.Accepted("/api/system/update/status",
            new { message = maintenance.Restart(false, lifetime) });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    {
        return Results.Problem("Nao foi possivel iniciar o reinicio automatico do NVR.", statusCode: 500);
    }
});

api.MapPost("/system/update/apply", (SystemMaintenance maintenance,
    IHostApplicationLifetime lifetime, HttpContext ctx) =>
{
    if (!IsSafeStateChangingRequest(ctx))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return Results.Accepted("/api/system/update/status",
            new { message = maintenance.Restart(true, lifetime) });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    {
        return Results.Problem("Nao foi possivel aplicar a atualizacao publicada.", statusCode: 500);
    }
});

api.MapGet("/status", (CameraStore store, RecorderService worker, StorageSettings settings) =>
{
    bool configured = settings.IsConfigured;
    string? path = configured ? settings.Root : null;
    var drives = settings.AvailableRoots.Select(settings.RecordingDrive).OfType<DriveInfo>().ToArray();
    double? freeGb = drives.Length == 0 ? null :
        Math.Round(drives.Sum(d => (double)d.AvailableFreeSpace) / 1e9, 1);
    string version = typeof(RecorderService).Assembly
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "desconhecida";
    return Results.Ok(new
    {
        cameraCount = store.GetAll().Length,
        recordingProcesses = worker.ActiveRecorders,
        previewProcesses = worker.ActivePreviews,
        recordingCameraIds = worker.ActiveRecordingIds,
        previewCameraIds = worker.ActivePreviewIds,
        storageConfigured = configured,
        freeGb, maxGb = configured ? settings.MaxGb : (long?)null,
        recordingsPath = path,
        version
    });
});

api.MapGet("/storage/drives", (StorageSettings settings) => Results.Ok(settings.Drives()));
api.MapGet("/storage/settings", (StorageSettings settings) => Results.Ok(new
{
    configured = settings.IsConfigured,
    path = settings.IsConfigured ? settings.Root : null,
    maxGb = settings.IsConfigured ? settings.MaxGb : (long?)null,
    minFreeGb = settings.IsConfigured ? settings.MinFreeGb : (long?)null,
    drive = settings.SelectedDrive,
    segmentSeconds = settings.SegmentSeconds,
    drives = settings.SelectedDrives,
    paths = settings.Roots,
    available = settings.AvailableRoots.Length > 0
}));
api.MapPost("/storage/settings", (StorageUpdateRequest input,
    StorageSettings settings, HttpContext ctx) =>
{
    if (!IsSafeStateChangingRequest(ctx))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        settings.Update(input);
        return Results.Ok(new { ok = true, path = settings.Root, maxGb = settings.MaxGb,
            minFreeGb = settings.MinFreeGb });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (IOException) { return Results.BadRequest(new { error = "Nao foi possivel acessar ou criar a pasta no disco selecionado." }); }
    catch (UnauthorizedAccessException) { return Results.BadRequest(new { error = "Sem permissao de gravacao na unidade." }); }
});

api.MapGet("/cameras", (CameraStore store) =>
    Results.Ok(store.GetAll().Select(c => new {
        c.Id, c.Name, c.OnvifUrl,
        ip = new Uri(c.MainRtsp).Host,
        port = new Uri(c.MainRtsp).Port,
        protocol = string.IsNullOrWhiteSpace(c.OnvifUrl) ? "RTSP" : "RTSP / ONVIF",
        hasSubstream = !string.IsNullOrWhiteSpace(c.SubRtsp),
        ptzConfigured = !string.IsNullOrWhiteSpace(c.OnvifUrl),
        c.RecordEnabled, c.PreviewEnabled, c.RecordAudio, c.PreviewAudio,
        c.RecordingStream, c.RecordingCodec, c.PreviewStream,
        c.ScheduleMode, c.ScheduleDays, c.ScheduleStart, c.ScheduleEnd,
        scheduledNow = RecorderService.IsRecordingScheduledNow(c, DateTime.Now)
    })));

api.MapGet("/cameras/{id:guid}", (Guid id, CameraStore store) =>
    store.Get(id) is { } camera ? Results.Ok(camera) : Results.NotFound());

api.MapPost("/cameras", (AddCamera input, CameraStore store) =>
{
    try
    {
        var c = store.Add(input);
        return Results.Created($"/api/cameras/{c.Id}", new { c.Id, c.Name });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPut("/cameras/{id:guid}", (Guid id, AddCamera input, CameraStore store) =>
{
    try
    {
        var camera = store.Update(id, input);
        return camera is null ? Results.NotFound() : Results.Ok(new { camera.Id, camera.Name });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapDelete("/cameras/{id:guid}", (Guid id, CameraStore store) =>
    store.Remove(id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/discover/onvif", async (OnvifService onvif, CancellationToken ct) =>
    Results.Ok(await onvif.DiscoverAsync(ct)));

api.MapGet("/network/subnets", () => Results.Ok(NetworkDiscovery.LocalSubnets()));
api.MapGet("/system/traffic", (NetworkTelemetry telemetry) =>
    Results.Ok(telemetry.Snapshot()));

api.MapGet("/system/linux-monitor", (LinuxMonitorSettings settings, CameraStore cameras) =>
    OperatingSystem.IsLinux() ? Results.Ok(settings.Current(cameras)) : Results.NotFound());
api.MapPost("/system/linux-monitor", (LinuxMonitorRequest request, LinuxMonitorSettings settings, CameraStore cameras, HttpContext ctx) =>
{
    if (!OperatingSystem.IsLinux()) return Results.NotFound();
    if (!IsSafeStateChangingRequest(ctx)) return Results.StatusCode(403);
    try { settings.Save(request, cameras); return Results.Ok(settings.Current(cameras)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (IOException) { return Results.Problem("Nao foi possivel salvar o mosaico."); }
});
api.MapGet("/system/windows", (DesktopOptionsService settings) =>
    Results.Ok(settings.Status()));

api.MapPost("/system/windows", (DesktopOptionsRequest request,
    DesktopOptionsService settings, CameraStore cameras, HttpContext ctx) =>
{
    if (!IsSafeStateChangingRequest(ctx))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        settings.Update(request, cameras);
        return Results.Ok(settings.Status());
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (IOException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException) { return Results.BadRequest(new { error =
        "Sem permissao para alterar a inicializacao do Windows. Verifique a tarefa do instalador." }); }
});

api.MapGet("/system/network", () =>
{
    int port = Uri.TryCreate(bindAddress, UriKind.Absolute, out var uri) ? uri.Port : 5088;
    return Results.Ok(new
    {
        installed = installedMode,
        lanListening = bindAddress.Contains("0.0.0.0", StringComparison.Ordinal) ||
            bindAddress.Contains("[::]", StringComparison.Ordinal),
        localUrl = $"http://127.0.0.1:{port}",
        urls = NetworkDiscovery.LocalSubnets()
            .Select(n => new { n.InterfaceName, n.Address, url = $"http://{n.Address}:{port}" })
            .ToArray()
    });
});

api.MapPost("/discover/rtsp", async (RtspScanRequest input, CancellationToken ct) =>
{
    try { return Results.Ok(await NetworkDiscovery.ScanRtspAsync(input.Cidr, input.Mode, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/onvif/streams", async (
    OnvifStreamRequest input, OnvifService onvif, CancellationToken ct) =>
{
    try { return Results.Ok(await onvif.ResolveStreamsAsync(input, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
        or TaskCanceledException or System.Xml.XmlException)
    {
        return Results.BadRequest(new {
            error = "Não foi possível obter os perfis RTSP automaticamente. Confira ONVIF, endereço, usuário e senha ou cadastre a câmera manualmente."
        });
    }
});

api.MapGet("/cameras/{id:guid}/ptz/check", async (
    Guid id, CameraStore store, OnvifService onvif, CancellationToken ct) =>
{
    var camera = store.Get(id);
    if (camera is null) return Results.NotFound();
    try { return Results.Ok(await onvif.CheckPtzAsync(camera, ct)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (HttpRequestException ex)
    {
        return Results.BadRequest(new { error =
            $"Servico ONVIF nao respondeu corretamente (HTTP {(int?)ex.StatusCode ?? 0}). Verifique a porta e as credenciais." });
    }
    catch (Exception ex) when (ex is TaskCanceledException or System.Xml.XmlException)
    {
        return Results.BadRequest(new { error = "Timeout ou resposta ONVIF invalida. Verifique IP, porta e credenciais." });
    }
});

api.MapPost("/cameras/{id:guid}/ptz", async (
    Guid id, PtzRequest req, CameraStore store, OnvifService onvif, CancellationToken ct) =>
{
    var camera = store.Get(id);
    if (camera is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(camera.OnvifUrl))
        return Results.BadRequest(new { error = "Configure o endereço ONVIF desta câmera." });
    try
    {
        await onvif.MoveAsync(camera, req.Action, ct);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
        or TaskCanceledException or System.Xml.XmlException)
    {
        return Results.Problem(
            ex is InvalidOperationException ? ex.Message :
            "Comando PTZ nao executado. Verifique ONVIF, credenciais e suporte PTZ da camera.",
            statusCode: 502);
    }
});

// Date-scoped recording listing, used by the four-camera playback calendar.
// The date is local to the NVR server, matching FFmpeg's strftime filenames.
api.MapGet("/cameras/{id:guid}/recordings", (Guid id, string? date,
    CameraStore store, StorageSettings settings) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    string? prefix = null;
    if (!string.IsNullOrWhiteSpace(date))
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var chosen))
            return Results.BadRequest(new { error = "Data invalida. Selecione uma data no calendario." });
        prefix = chosen.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
    }
    if (!settings.IsConfigured) return Results.Ok(Array.Empty<object>());
    var files = settings.AvailableRoots
        .SelectMany(root => RecordingFolders.Files(root, id, prefix).Select(file => (Root: root, File: file)))
        .Where(item => prefix is null ||
            Path.GetFileName(item.File).StartsWith(prefix, StringComparison.Ordinal))
        .OrderByDescending(item => Path.GetFileName(item.File))
        .Take(prefix is null ? 500 : 2000)
        .Select(item =>
        {
            string file = item.File;
            string name = Path.GetFileName(file);
            string? clock = name.Length >= 15 && name[8] == '_' &&
                name.AsSpan(9, 6).ToString().All(char.IsAsciiDigit)
                ? $"{name.Substring(9, 2)}:{name.Substring(11, 2)}:{name.Substring(13, 2)}"
                : null;
            return new {
                file = name,
                time = clock,
                bytes = new FileInfo(file).Length,
                url = $"/recordings/{id}/{Uri.EscapeDataString(name)}?disk={Uri.EscapeDataString(item.Root)}"
            };
        }).ToArray();
    return Results.Ok(files);
});

app.MapGet("/hls/{id:guid}/{file}", (Guid id, string file, CameraStore store, NvrConfig cfg) =>
    VideoFile(store.Get(id), cfg.PreviewRoot, id, file, true));
app.MapGet("/recordings/{id:guid}/{file}", (Guid id, string file, string? disk, CameraStore store, StorageSettings settings) =>
{
    if (store.Get(id) is null || Path.GetFileName(file) != file ||
        !file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    string? prefix = file.Length >= 8 && file[..8].All(char.IsAsciiDigit) ? file[..8] : null;
    foreach (var root in settings.AvailableRoots.Where(root => disk is null || disk == root))
    {
        var match = RecordingFolders.Files(root, id, prefix)
            .FirstOrDefault(path => Path.GetFileName(path) == file);
        if (match is not null && settings.IsAvailable(root))
            return Results.File(match, "video/mp4", enableRangeProcessing: true);
    }
    return Results.NotFound();
});

app.Run();

static bool IsLoopbackBind(string bind) => bind is
    "http://127.0.0.1:5088" or "http://localhost:5088" or "http://[::1]:5088";

static bool IsTrustedLocalRequest(HttpContext ctx)
{
    IPAddress? remote = ctx.Connection.RemoteIpAddress;
    if (remote is null) return false;
    if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
    return IPAddress.IsLoopback(remote) &&
        (ctx.Request.Host.Host is "127.0.0.1" or "localhost" or "::1");
}

static bool IsSafeStateChangingRequest(HttpContext ctx)
{
    if (ctx.Request.Headers["X-NVR-Request"].ToString() != "1") return false;
    string origin = ctx.Request.Headers["Origin"].ToString();
    if (string.IsNullOrEmpty(origin)) return true;
    return Uri.TryCreate(origin, UriKind.Absolute, out var url) &&
        url.Scheme == "http" &&
        url.Host.Equals(ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
        url.Port == ctx.Request.Host.Port;
}

static Task SignInAdmin(HttpContext ctx, string revision)
{
    var identity = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.Name, "admin"),
        new Claim("nvr.security-revision", revision)
    }, CookieAuthenticationDefaults.AuthenticationScheme);
    return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));
}

static IResult EmbeddedWebAsset(string resourceName, string contentType)
{
    using Stream? stream = typeof(FfmpegProvisioner).Assembly
        .GetManifestResourceStream(resourceName);
    if (stream is null) return Results.NotFound();
    using var output = new MemoryStream();
    stream.CopyTo(output);
    return Results.Bytes(output.ToArray(), contentType);
}

static IResult VideoFile(Camera? camera, string root, Guid id, string file, bool isHls)
{
    if (camera is null || Path.GetFileName(file) != file) return Results.NotFound();
    if (isHls && !(file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))) return Results.NotFound();
    if (!isHls && !file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    string fullPath = Path.Combine(root, id.ToString(), file);
    if (!File.Exists(fullPath)) return Results.NotFound();
    string mime = file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        ? "application/vnd.apple.mpegurl"
        : file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            ? "video/mp2t" : "video/mp4";
    return Results.File(fullPath, mime, enableRangeProcessing: !isHls);
}
