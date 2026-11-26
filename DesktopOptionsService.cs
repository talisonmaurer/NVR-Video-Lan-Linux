using System.Diagnostics;
using System.Text.Json;

namespace NvrVideoLan;

/// <summary>
/// Installed Windows integration and the local monitor's persisted choices.
/// Settings live in ProgramData; only SYSTEM/admin have write access.
/// </summary>
public sealed record DesktopOptions(bool StartWithWindows, bool MonitorEnabled,
    Guid? MonitorCameraId);

public sealed record DesktopOptionsRequest(bool StartWithWindows,
    bool MonitorEnabled, Guid? MonitorCameraId);

public sealed class DesktopOptionsService
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly bool _installed;
    private DesktopOptions _options;

    public DesktopOptionsService(string dataRoot)
    {
        _path = Path.Combine(dataRoot, "desktop-options.json");
        _installed = File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.marker"));
        _options = new DesktopOptions(true, false, null);
        if (File.Exists(_path))
        {
            try
            {
                _options = JsonSerializer.Deserialize<DesktopOptions>(File.ReadAllText(_path))
                    ?? _options;
            }
            catch (JsonException)
            {
                // Fail safe: do not start a monitor from unreadable settings.
                _options = new DesktopOptions(true, false, null);
            }
        }
    }

    public DesktopOptions Current
    {
        get { lock (_gate) return _options; }
    }

    public bool Installed => _installed;

    public object Status()
    {
        var o = Current;
        return new
        {
            installed = _installed,
            startWithWindows = o.StartWithWindows,
            monitorEnabled = o.MonitorEnabled,
            monitorCameraId = o.MonitorCameraId,
            monitorUrl = "http://127.0.0.1:5088/monitor",
            note = _installed
                ? "O gravador inicia antes do login. O monitor em tela cheia inicia apos entrar no Windows."
                : "Ajustes de inicializacao exigem instalacao pelo NVR-Video-LAN-Setup.exe."
        };
    }

    public void Update(DesktopOptionsRequest request, CameraStore cameras)
    {
        if (!_installed || !OperatingSystem.IsWindows())
            throw new InvalidOperationException("Instale o NVR pelo Setup.exe para configurar a inicializacao do Windows.");

        if (request.MonitorEnabled)
        {
            if (!request.StartWithWindows)
                throw new ArgumentException("Ative Iniciar com o Windows para habilitar o monitor automatico.");
            if (request.MonitorCameraId is not Guid id || cameras.Get(id) is not { PreviewEnabled: true })
                throw new ArgumentException("Escolha uma camera cadastrada com visualizacao habilitada.");
        }
        var proposed = new DesktopOptions(request.StartWithWindows,
            request.MonitorEnabled, request.MonitorCameraId);

        lock (_gate)
        {
            // Change Windows Task Scheduler + the user's startup shortcut first.
            // Only commit preferences when the Windows configuration succeeded.
            ApplyToWindows(proposed);

            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(proposed,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, true);
                _options = proposed;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private static void ApplyToWindows(DesktopOptions requested)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "monitor-windows.ps1");
        if (!File.Exists(script))
            throw new InvalidOperationException("Componente Windows nao encontrado. Atualize usando o Setup.exe.");

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (string arg in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, "-InstallPath", AppContext.BaseDirectory,
            "-StartWithWindows", requested.StartWithWindows ? "true" : "false",
            "-MonitorEnabled", requested.MonitorEnabled ? "true" : "false"
        })
            start.ArgumentList.Add(arg);

        using var proc = Process.Start(start)
            ?? throw new IOException("Falha ao iniciar o configurador do Windows.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30000))
        {
            proc.Kill(entireProcessTree: true);
            throw new IOException("Tempo esgotado ao configurar a inicializacao do Windows.");
        }
        if (proc.ExitCode != 0)
            throw new IOException("Falha ao configurar Windows: " +
                (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
    }
}
