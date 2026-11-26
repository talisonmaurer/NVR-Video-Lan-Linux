using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace NvrVideoLan;

/// <summary>
/// Finds a user-configured FFmpeg binary or extracts the build-time embedded
/// FFmpeg ZIP to a writable data directory. Nothing is written to the EXE's
/// directory, so starting the app from a read-only SMB share is supported.
/// </summary>
public static class FfmpegProvisioner
{
    private const string Resource = "NvrVideoLan.EmbeddedFFmpeg";

    public static string Resolve(string? overridePath, string dataRoot)
    {
        // Keep a valid user override. If an old NVR_FFMPEG setting points to
        // a missing file, fall back to the embedded executable automatically.
        string? selected = overridePath?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected))
            return selected;

        using Stream? bundled = typeof(FfmpegProvisioner).Assembly
            .GetManifestResourceStream(Resource);
        if (bundled is null)
            return selected ?? "ffmpeg"; // Development builds may use PATH.

        if (!string.IsNullOrWhiteSpace(selected))
            Console.WriteLine("FFmpeg externo nao encontrado; usando a copia integrada.");

        // Different bundles get different local directories. That avoids
        // replacing an executable while an FFmpeg process is still running.
        string bundleId = Convert.ToHexString(SHA256.HashData(bundled))[..16];
        string toolsDir = Path.Combine(dataRoot, "tools", "ffmpeg-" + bundleId);
        string ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");

        if (File.Exists(ffmpegPath) && new FileInfo(ffmpegPath).Length > 1_000_000)
            return ffmpegPath;

        Directory.CreateDirectory(toolsDir);
        bundled.Position = 0;
        using var archive = new ZipArchive(bundled, ZipArchiveMode.Read);
        var ffmpegEntry = archive.GetEntry("ffmpeg.exe")
            ?? throw new InvalidOperationException(
                "O pacote FFmpeg interno nao contem ffmpeg.exe.");

        string temp = ffmpegPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var input = ffmpegEntry.Open())
                input.CopyTo(output);

            if (new FileInfo(temp).Length <= 1_000_000)
                throw new InvalidDataException("FFmpeg empacotado parece incompleto.");

            // A concurrent instance may already have extracted it.
            if (!File.Exists(ffmpegPath))
                File.Move(temp, ffmpegPath);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        foreach (string notice in new[] { "COPYING.GPLv3.txt", "FFMPEG-NOTICE.txt" })
        {
            var entry = archive.GetEntry(notice);
            if (entry is null) continue;
            string destination = Path.Combine(toolsDir, notice);
            if (File.Exists(destination)) continue;
            try
            {
                using var source = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew);
                source.CopyTo(output);
            }
            catch (IOException) when (File.Exists(destination)) { }
        }

        return ffmpegPath;
    }
}
