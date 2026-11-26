using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NvrVideoLan;

/// <summary>
/// Restarts this process without restarting Windows. For upgrades, stages a
/// trusted executable already published in an administrator-controlled folder.
/// No GitHub credentials or network source code builds are required on the NVR.
/// </summary>
public sealed class SystemMaintenance
{
    private readonly string _dataRoot;
    private readonly string _currentExe;
    private readonly string _updateSource;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly bool _installedMode;
    private int _busy;

    public SystemMaintenance(string dataRoot)
    {
        _dataRoot = dataRoot;
        _currentExe = Environment.ProcessPath ?? "";
        _installedMode = File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.marker"));
        string? explicitSource = Environment.GetEnvironmentVariable("NVR_UPDATE_SOURCE");
        _updateSource = !string.IsNullOrWhiteSpace(explicitSource)
            ? Path.GetFullPath(explicitSource)
            : Path.Combine(AppContext.BaseDirectory, "updates", "NvrVideoLan.exe");

        // Child processes of a staged update need to retain the original source.
        Environment.SetEnvironmentVariable("NVR_UPDATE_SOURCE", _updateSource);
    }

    public object GetStatus()
    {
        if (OperatingSystem.IsLinux())
            return new { available = false, message = "Linux: atualize pelo pacote ou instalador. Reinicio gerenciado pelo systemd." };
        if (_installedMode)
            return new { available = false,
                message = "Instalacao gerenciada pelo Windows. Para atualizar, execute o NVR-Video-LAN-Setup.exe mais recente. O botao Reiniciar NVR continua disponivel." };
        if (string.IsNullOrWhiteSpace(_currentExe) || !File.Exists(_currentExe))
            return new { available = false, message = "Executavel atual nao foi localizado." };
        if (!File.Exists(_updateSource))
            return new { available = false, message =
                "Nenhum novo NvrVideoLan.exe publicado na pasta updates. Atualize no computador de desenvolvimento e tente novamente.",
                updateSource = _updateSource };
        try
        {
            var candidate = new FileInfo(_updateSource);
            var active = new FileInfo(_currentExe);
            if (candidate.Length < 4_000_000)
                return new { available = false, message = "Arquivo de atualizacao incompleto.", updateSource = _updateSource };

            bool available = !Path.GetFullPath(_currentExe).Equals(
                    Path.GetFullPath(_updateSource), StringComparison.OrdinalIgnoreCase) &&
                !HashesEqual(_currentExe, _updateSource);
            return new {
                available,
                message = available
                    ? "Nova versao publicada encontrada. Clique em Aplicar atualizacao para reiniciar com ela."
                    : "O executavel publicado e igual ao que esta em execucao.",
                updateSource = _updateSource,
                publishedUtc = candidate.LastWriteTimeUtc,
                startedUtc = _startedUtc
            };
        }
        catch (IOException)
        {
            return new { available = false, message = "O arquivo publicado esta em uso ou indisponivel.", updateSource = _updateSource };
        }
        catch (UnauthorizedAccessException)
        {
            return new { available = false, message = "Sem permissao para ler o arquivo de atualizacao.", updateSource = _updateSource };
        }
    }

    public string Restart(bool installPublishedVersion, IHostApplicationLifetime lifetime)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            throw new InvalidOperationException("Ja existe uma reinicializacao em andamento.");
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (installPublishedVersion || Environment.GetEnvironmentVariable("NVR_SYSTEMD") != "1")
                    throw new InvalidOperationException("Use o instalador ou systemctl para gerenciar o NVR no Linux.");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    lifetime.StopApplication();
                });
                return "Reinicio solicitado. O systemd reiniciara o NVR.";
            }
            if (string.IsNullOrWhiteSpace(_currentExe) || !File.Exists(_currentExe))
                throw new InvalidOperationException("Executavel atual nao encontrado; reinicie manualmente.");
            if (_installedMode && installPublishedVersion)
                throw new InvalidOperationException("A versao instalada deve ser atualizada pelo NVR-Video-LAN-Setup.exe.");

            string target = _currentExe;
            if (installPublishedVersion)
            {
                if (!File.Exists(_updateSource))
                    throw new InvalidOperationException("Nao existe um executavel novo na pasta updates.");
                if (Path.GetFullPath(_currentExe).Equals(
                    Path.GetFullPath(_updateSource), StringComparison.OrdinalIgnoreCase) ||
                    HashesEqual(_currentExe, _updateSource))
                    throw new InvalidOperationException("Nao ha uma versao diferente para aplicar.");

                // Do not overwrite the running binary. A new version runs from
                // a separate local file and uses the same NVR_DATA and recordings.
                string staging = Path.Combine(_dataRoot, "updates");
                Directory.CreateDirectory(staging);
                string temp = Path.Combine(staging, "candidate-" + Guid.NewGuid().ToString("N") + ".tmp");
                string staged = Path.Combine(staging,
                    "NvrVideoLan-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".exe");
                try
                {
                    File.Copy(_updateSource, temp);
                    if (!HashesEqual(temp, _updateSource))
                        throw new IOException("O download local da atualizacao nao passou na verificacao de integridade.");
                    File.Move(temp, staged);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                target = staged;
            }

            SpawnRestartHelper(target);
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                lifetime.StopApplication();
            });
            return installPublishedVersion
                ? "Atualizacao preparada. O NVR sera reiniciado com o executavel novo."
                : "Reinicio solicitado. O NVR sera reiniciado automaticamente.";
        }
        catch
        {
            Interlocked.Exchange(ref _busy, 0);
            throw;
        }
    }

    private void SpawnRestartHelper(string target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Reinicio automatico disponivel apenas no Windows.");
        string workingDir = _dataRoot;
        int oldPid = Environment.ProcessId;
        string script = "$oldPid=" + oldPid + "; " +
            "$exe='" + EscapePowerShellString(target) + "'; " +
            "$dir='" + EscapePowerShellString(workingDir) + "'; " +
            "for($i=0;$i -lt 240;$i++){" +
            "if(-not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)){break}; " +
            "Start-Sleep -Milliseconds 250 }; " +
            "if(Get-Process -Id $oldPid -ErrorAction SilentlyContinue){exit 2}; " +
            (_installedMode
                ? "for($retry=0;$retry -lt 40;$retry++){" +
                  "try{ $task=Get-ScheduledTask -TaskName 'NVR Video LAN' -ErrorAction Stop; " +
                  "if($task.State -ne 'Running'){ Start-ScheduledTask -TaskName 'NVR Video LAN' -ErrorAction Stop; break } }" +
                  "catch{ } ; Start-Sleep -Milliseconds 300 }"
                : "Start-Process -FilePath $exe -WorkingDirectory $dir -WindowStyle Hidden");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var helper = Process.Start(psi)
            ?? throw new InvalidOperationException("Nao foi possivel iniciar o processo auxiliar do Windows.");
    }

    private static string EscapePowerShellString(string value) => value.Replace("'", "''");

    private static bool HashesEqual(string a, string b)
    {
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(first), SHA256.HashData(second));
    }
}
