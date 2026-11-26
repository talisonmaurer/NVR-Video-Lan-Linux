using System.Text.Json;

namespace NvrVideoLan;

public sealed record StorageUpdateRequest(string? DriveLetter, long MaxGb, long MinFreeGb, string[]? DriveLetters = null, int? SegmentSeconds = null);

/// <summary>
/// Retention settings can be changed from the browser. The NVR only writes/deletes
/// its own recordings, in a dedicated directory on the selected drive.
/// </summary>
public sealed class StorageSettings
{
    public sealed record Stored(string Root, long MaxGb, long MinFreeGb, string? MountPoint = null, string[]? AdditionalDrives = null, int SegmentSeconds = 60);
    private readonly object _gate = new();
    private readonly string _file;
    private Stored? _value;

    public StorageSettings(string dataRoot)
    {
        _file = Path.Combine(dataRoot, "storage.json");
        // Never silently choose a recording drive on first start.
        // Existing storage.json files are still honored after upgrades.
        if (File.Exists(_file))
        {
            _value = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file))
                ?? throw new InvalidDataException("storage.json invalido.");
            // A missing Linux mount must not prevent the web UI from starting.
            // Do not create recording directories until the selected mount is available.
            if (OperatingSystem.IsWindows())
            {
                Validate(_value);
                Directory.CreateDirectory(_value.Root);
            }
        }
    }

    public bool IsConfigured { get { lock (_gate) return _value is not null; } }
    public string Root { get { lock (_gate) return _value?.Root ?? ""; } }
    public int SegmentSeconds { get { lock (_gate) return _value?.SegmentSeconds is 30 or 60 or 120 or 300 or 600 or 900 or 1800 ? _value.SegmentSeconds : 60; } }
    public long MaxBytes { get { lock (_gate) return (_value?.MaxGb ?? 0) * 1_000_000_000L; } }
    public long MinFreeBytes { get { lock (_gate) return (_value?.MinFreeGb ?? 0) * 1_000_000_000L; } }
    public long MaxGb { get { lock (_gate) return _value?.MaxGb ?? 0; } }
    public long MinFreeGb { get { lock (_gate) return _value?.MinFreeGb ?? 0; } }

    private static bool Eligible(DriveInfo drive)
    {
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            return false;
        // On Linux, expose local filesystems, not /proc, /sys, tmpfs or container bind mounts.
        return OperatingSystem.IsWindows() || drive.DriveFormat is
            "ext4" or "ext3" or "ext2" or "xfs" or "btrfs" or "f2fs" or "ntfs" or "ntfs3" or "fuseblk" or "exfat" or "vfat" or "zfs";
    }

    private static string DriveId(DriveInfo drive) =>
        OperatingSystem.IsWindows() ? drive.Name[..1].ToUpperInvariant() : drive.Name;

    private static DriveInfo? FindDrive(string id) =>
        DriveInfo.GetDrives().FirstOrDefault(d =>
        {
            try { return Eligible(d) && string.Equals(DriveId(d), id,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        });

    public string? SelectedDrive
    {
        get
        {
            lock (_gate)
                return _value is null ? null : OperatingSystem.IsWindows()
                    ? Path.GetPathRoot(_value.Root)?[..1].ToUpperInvariant()
                    : _value.MountPoint;
        }
    }

    public string[] SelectedDrives
    {
        get
        {
            lock (_gate)
                return _value is null ? [] : new[] { SelectedDrive! }
                    .Concat(_value.AdditionalDrives ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct().ToArray();
        }
    }

    public string[] Roots
    {
        get
        {
            lock (_gate)
            {
                if (_value is null) return [];
                return new[] { _value.Root }.Concat((_value.AdditionalDrives ?? [])
                    .Select(id => Path.Combine(OperatingSystem.IsWindows() ? id + @":\" : id,
                        "NVR-Video-Lan", "Gravacoes"))).Distinct().ToArray();
            }
        }
    }

    public string[] AvailableRoots => Roots.Where(IsAvailable).ToArray();

    public DriveInfo? RecordingDrive(string expectedRoot)
    {
        lock (_gate)
        {
            if (_value is null || !Roots.Contains(expectedRoot)) return null;
            string? id = expectedRoot == _value.Root ? SelectedDrive :
                SelectedDrives.FirstOrDefault(candidate => Path.Combine(
                    OperatingSystem.IsWindows() ? candidate + @":\" : candidate,
                    "NVR-Video-Lan", "Gravacoes") == expectedRoot);
            if (!OperatingSystem.IsWindows() && id == "/") return null;
            if (id is null) return null;
            try
            {
                var drive = FindDrive(id);
                if (drive is null) return null;
                string expected = Path.Combine(drive.Name, "NVR-Video-Lan", "Gravacoes");
                // Retain legacy Windows paths; Linux always uses the selected mount.
                if (!OperatingSystem.IsWindows() &&
                    !string.Equals(expected, expectedRoot, StringComparison.Ordinal)) return null;
                return drive;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    public bool IsAvailable(string root) => RecordingDrive(root) is not null;

    public object[] Drives()
    {
        var drives = new List<object>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!Eligible(drive) || (!OperatingSystem.IsWindows() && (drive.Name == "/" || drive.Name.StartsWith("/boot")))) continue;
                drives.Add(new {
                    letter = DriveId(drive),
                    display = OperatingSystem.IsWindows() ? DriveId(drive) + ":" : drive.Name,
                    label = drive.VolumeLabel,
                    freeGb = Math.Round(drive.AvailableFreeSpace / 1e9, 1),
                    sizeGb = Math.Round(drive.TotalSize / 1e9, 1)
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return drives.ToArray();
    }

    public void Update(StorageUpdateRequest request)
    {
        var ids = (request.DriveLetters is { Length: > 0 } ? request.DriveLetters :
            new[] { request.DriveLetter ?? "" }).Distinct().ToArray();
        if (ids.Length == 0 || ids.Length > 32 || ids.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Selecione pelo menos uma unidade local valida.");
        int seconds = request.SegmentSeconds ?? SegmentSeconds;
        if (seconds is not (30 or 60 or 120 or 300 or 600 or 900 or 1800))
            throw new ArgumentException("Escolha 30 s, 1, 2, 5, 10, 15 ou 30 minutos.");
        var candidates = new List<Stored>();
        foreach (var id in ids)
        {
            if (!OperatingSystem.IsWindows() && (id == "/" || id.StartsWith("/boot")))
                throw new ArgumentException("Disco do sistema nao permitido.");
            var drive = FindDrive(id) ?? throw new ArgumentException("Unidade indisponivel: " + id);
            var candidate = new Stored(Path.Combine(drive.Name, "NVR-Video-Lan", "Gravacoes"),
                request.MaxGb, request.MinFreeGb, OperatingSystem.IsWindows() ? null : drive.Name);
            Validate(candidate);
            if (request.MaxGb * 1_000_000_000L > drive.TotalSize ||
                request.MinFreeGb * 1_000_000_000L >= drive.TotalSize)
                throw new ArgumentException("Cota ou reserva excede a capacidade de " + id);
            candidates.Add(candidate);
        }
        foreach (var candidate in candidates) Directory.CreateDirectory(candidate.Root);
        var next = candidates[0] with { AdditionalDrives = ids.Skip(1).ToArray(), SegmentSeconds = seconds };

        lock (_gate)
        {
            string temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _file, true);
                _value = next;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private static void Validate(Stored saved)
    {
        if (saved.MaxGb is < 1 or > 50000 || saved.MinFreeGb is < 1 or > 50000)
            throw new ArgumentException("A cota e a reserva devem ficar entre 1 e 50.000 GB.");
        if (!Path.IsPathFullyQualified(saved.Root))
            throw new ArgumentException("A pasta de gravacoes precisa ter caminho absoluto.");
        string? id = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(saved.Root)?[..1].ToUpperInvariant()
            : saved.MountPoint;
        if (id is null || FindDrive(id) is null)
            throw new ArgumentException("Disco de gravacoes indisponivel.");
    }
}
