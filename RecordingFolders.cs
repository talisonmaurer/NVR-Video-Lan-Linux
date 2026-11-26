using System.Globalization;

namespace NvrVideoLan;

public static class RecordingFolders
{
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => char.IsControl(c) || invalid.Contains(c) ||
            "<>:\"/\\|?*%".Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 65) clean = clean[..65].TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(clean) ? "Camera" : clean;
    }

    public static string ForDay(string root, Camera camera, DateTime now) =>
        Path.Combine(root, SafeName(camera.Name) + "--" + camera.Id.ToString("N"),
            now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public static bool SafeDirectory(string path) => Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    public static bool SafeFile(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static bool Managed(string name) => Guid.TryParse(name, out _) ||
        (name.Length > 34 && name[^34..^32] == "--" &&
            Guid.TryParseExact(name[^32..], "N", out _));

    private static IEnumerable<string> CameraDirs(string root, Guid? id)
    {
        if (!SafeDirectory(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (Managed(name) && SafeDirectory(dir) &&
                (id is null || name.Equals(id.Value.ToString(), StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("--" + id.Value.ToString("N"), StringComparison.OrdinalIgnoreCase)))
                yield return dir;
        }
    }

    public static IEnumerable<string> Files(string root, Guid? id = null, string? prefix = null)
    {
        foreach (var dir in CameraDirs(root, id))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.mp4").Where(SafeFile))
                yield return file;
            foreach (var day in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(day);
                if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) || !SafeDirectory(day) ||
                    (prefix is not null && date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) != prefix))
                    continue;
                foreach (var file in Directory.EnumerateFiles(day, "*.mp4").Where(SafeFile))
                    yield return file;
            }
        }
    }

    public static void CreateDay(string root, string directory)
    {
        if (!SafeDirectory(root)) throw new IOException("Pasta de gravacoes indisponivel.");
        var camera = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(camera);
        if (!SafeDirectory(camera)) throw new IOException("Pasta de camera invalida.");
        Directory.CreateDirectory(directory);
        if (!SafeDirectory(directory)) throw new IOException("Pasta de data invalida.");
    }
}
