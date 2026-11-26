using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace NvrVideoLan;

// Recording and live-preview processes are intentionally independent.
// Each camera can pause recording on a schedule without interrupting the mosaic.
public sealed class RecorderService(
    CameraStore store, NvrConfig config, StorageSettings storage,
    ILogger<RecorderService> logger) : BackgroundService
{
    private sealed record Worker(string StreamUrl, string Directory, Process Process,
        string Codec = "copy", bool AudioEnabled = false, int SegmentSeconds = 60, string? RecordingRoot = null);
    private readonly ConcurrentDictionary<Guid, Worker> _recorders = new();
    private readonly ConcurrentDictionary<Guid, Worker> _previews = new();
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public int ActiveRecorders => _recorders.Values.Count(p => !p.Process.HasExited);
    public int ActivePreviews => _previews.Values.Count(p => !p.Process.HasExited);
    public Guid[] ActiveRecordingIds => _recorders
        .Where(entry => !entry.Value.Process.HasExited).Select(entry => entry.Key).ToArray();
    public Guid[] ActivePreviewIds => _previews
        .Where(entry => !entry.Value.Process.HasExited).Select(entry => entry.Key).ToArray();

    public static bool IsRecordingScheduledNow(Camera camera, DateTime now)
    {
        if (!camera.RecordEnabled) return false;
        if (camera.ScheduleMode != "window") return true;

        var days = camera.ScheduleDays ?? Array.Empty<int>();
        if (days.Length == 0) return false;
        if (!TimeSpan.TryParseExact(camera.ScheduleStart, @"hh\:mm", CultureInfo.InvariantCulture, out var start) ||
            !TimeSpan.TryParseExact(camera.ScheduleEnd, @"hh\:mm", CultureInfo.InvariantCulture, out var end))
            return false;

        int today = (int)now.DayOfWeek;
        int yesterday = (today + 6) % 7;
        TimeSpan clock = now.TimeOfDay;
        if (start == end) return days.Contains(today); // Entire selected day
        if (start < end) return days.Contains(today) && clock >= start && clock < end;
        // Window crosses midnight, e.g. Monday 22:00 -> Tuesday 06:00.
        return (days.Contains(today) && clock >= start) ||
               (days.Contains(yesterday) && clock < end);
    }

    private static string StreamUrl(Camera camera, string preference) =>
        preference == "sub" && !string.IsNullOrWhiteSpace(camera.SubRtsp)
            ? camera.SubRtsp : camera.MainRtsp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Camera[] cameras = store.GetAll();
                var byId = cameras.ToDictionary(c => c.Id);
                DateTime now = DateTime.Now;
                int segmentSeconds = storage.SegmentSeconds;
                var roots = storage.AvailableRoots;
                bool recordingAvailable = roots.Length > 0;

                foreach (var entry in _recorders.ToArray())
                {
                    bool current = recordingAvailable &&
                        byId.TryGetValue(entry.Key, out var camera) &&
                        IsRecordingScheduledNow(camera!, now) &&
                        entry.Value.StreamUrl == StreamUrl(camera!, camera!.RecordingStream) &&
                        entry.Value.Codec == camera!.RecordingCodec &&
                        entry.Value.SegmentSeconds == segmentSeconds &&
                        entry.Value.AudioEnabled == camera.RecordAudio &&
                        roots.Any(root => entry.Value.Directory == RecordingFolders.ForDay(root, camera!, now)) &&
                        !entry.Value.Process.HasExited;
                    if (!current && _recorders.TryRemove(entry.Key, out var old))
                        await StopProcessAsync(old.Process);
                }

                foreach (var entry in _previews.ToArray())
                {
                    bool current = byId.TryGetValue(entry.Key, out var camera) &&
                        camera!.PreviewEnabled &&
                        entry.Value.StreamUrl == StreamUrl(camera, camera.PreviewStream) &&
                        entry.Value.AudioEnabled == camera.PreviewAudio &&
                        !entry.Value.Process.HasExited;
                    if (!current && _previews.TryRemove(entry.Key, out var old))
                        await StopProcessAsync(old.Process);
                }

                foreach (var camera in cameras)
                {
                    if (recordingAvailable &&
                        IsRecordingScheduledNow(camera, now) && !_recorders.ContainsKey(camera.Id))
                    {
                        try
                        {
                            var recordingRoot = roots
                                .OrderBy(root => _recorders.Values.Count(w => w.RecordingRoot == root))
                                .ThenByDescending(root => storage.RecordingDrive(root)?.AvailableFreeSpace ?? 0)
                                .First();
                            var worker = StartRecording(camera, recordingRoot, segmentSeconds, now);
                            if (!_recorders.TryAdd(camera.Id, worker))
                                await StopProcessAsync(worker.Process);
                            else logger.LogInformation("Gravacao iniciada para {Camera}.", camera.Name);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning("Gravacao nao iniciou para {Camera}: {Reason}",
                                camera.Name, ex.GetType().Name);
                        }
                    }

                    if (camera.PreviewEnabled && !_previews.ContainsKey(camera.Id))
                    {
                        try
                        {
                            var worker = StartPreview(camera);
                            if (!_previews.TryAdd(camera.Id, worker))
                                await StopProcessAsync(worker.Process);
                            else logger.LogInformation("Previa iniciada para {Camera}.", camera.Name);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning("Visualizacao nao iniciou para {Camera}: {Reason}",
                                camera.Name, ex.GetType().Name);
                        }
                    }
                }

                if (recordingAvailable &&
                    DateTime.UtcNow - _lastCleanupUtc > TimeSpan.FromSeconds(30))
                {
                    foreach (var root in roots) CleanupDisk(root);
                    _lastCleanupUtc = DateTime.UtcNow;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var entry in _previews.ToArray())
                if (_previews.TryRemove(entry.Key, out var worker))
                    await StopProcessAsync(worker.Process);
            foreach (var entry in _recorders.ToArray())
                if (_recorders.TryRemove(entry.Key, out var worker))
                    await StopProcessAsync(worker.Process);
        }
    }

    private Worker StartRecording(Camera camera, string root, int segmentSeconds, DateTime now)
    {
        if (!storage.IsAvailable(root)) throw new IOException("Disco de gravacoes indisponivel.");
        string directory = RecordingFolders.ForDay(root, camera, now);
        RecordingFolders.CreateDay(root, directory);
        string source = StreamUrl(camera, camera.RecordingStream);
        // Default: stream copy (no CPU-intensive conversion).
        // H.264/H.265 choices explicitly transcode and may tax older CPUs.
        var args = new List<string> {
            "-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp",
            "-i", source, "-map", "0:v:0"
        };
        if (camera.RecordAudio)
        {
            // Optional audio map: cameras without a microphone still record video.
            // AAC is compatible with MP4 playback on most browsers.
            args.AddRange(new[] { "-map", "0:a:0?", "-c:a", "aac", "-b:a", "96k" });
        }
        else args.Add("-an");

        switch (camera.RecordingCodec)
        {
            case "h264":
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast",
                    "-crf", "23", "-pix_fmt", "yuv420p" });
                break;
            case "h265":
                args.AddRange(new[] { "-c:v", "libx265", "-preset", "ultrafast",
                    "-crf", "28", "-pix_fmt", "yuv420p", "-tag:v", "hvc1" });
                break;
            default:
                args.AddRange(new[] { "-c:v", "copy" });
                break;
        }
        args.AddRange(new[] {
            "-f", "segment", "-segment_time", segmentSeconds.ToString(CultureInfo.InvariantCulture), "-segment_atclocktime", "1",
            "-reset_timestamps", "1", "-strftime", "1",
            "-segment_format", "mp4",
            "-segment_format_options", "movflags=+faststart",
            Path.Combine(directory, "%Y%m%d_%H%M%S.mp4")
        });
        var process = Spawn(args.ToArray());
        return new Worker(source, directory, process, camera.RecordingCodec, camera.RecordAudio, segmentSeconds, root);
    }

    private Worker StartPreview(Camera camera)
    {
        string directory = Path.Combine(config.PreviewRoot, camera.Id.ToString());
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                try { File.Delete(file); } catch (IOException) { }
        }

        string source = StreamUrl(camera, camera.PreviewStream);
        var args = new List<string> {
            "-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp",
            "-i", source, "-map", "0:v:0", "-c:v", "copy"
        };
        if (camera.PreviewAudio)
        {
            // HLS uses AAC for sound; optional mapping preserves video-only cameras.
            args.AddRange(new[] { "-map", "0:a:0?", "-c:a", "aac", "-b:a", "96k" });
        }
        else args.Add("-an");

        args.AddRange(new[] {
            "-f", "hls", "-hls_time", "2", "-hls_list_size", "6",
            "-hls_flags", "delete_segments+omit_endlist+temp_file",
            "-hls_segment_filename", Path.Combine(directory, "seg_%06d.ts"),
            "-y", Path.Combine(directory, "index.m3u8")
        });
        var process = Spawn(args.ToArray());
        return new Worker(source, directory, process, AudioEnabled: camera.PreviewAudio);
    }

    private Process Spawn(params string[] arguments)
    {
        var info = new ProcessStartInfo(config.FfmpegExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    // Consume stderr without leaking RTSP credentials into logs.
                    while (await process.StandardError.ReadLineAsync() is not null) { }
                }
                catch (Exception) { }
            });
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("q"); } catch { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private void CleanupDisk(string recordingRoot)
    {
        try
        {
            var drive = storage.RecordingDrive(recordingRoot);
            if (drive is null || !Directory.Exists(recordingRoot)) return;
            var all = RecordingFolders.Files(recordingRoot)
                .Select(path => new FileInfo(path))
                .ToList();
            long used = all.Sum(f => f.Length);
            long free = drive.AvailableFreeSpace;

            foreach (var file in all
                .Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddSeconds(-Math.Max(180, storage.SegmentSeconds + 120)))
                .OrderBy(f => f.LastWriteTimeUtc))
            {
                if (used <= storage.MaxBytes && free >= storage.MinFreeBytes) break;
                try
                {
                    if (!storage.IsAvailable(recordingRoot)) return;
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    long bytes = file.Length;
                    file.Delete();
                    used -= bytes;
                    free += bytes;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (used > storage.MaxBytes || free < storage.MinFreeBytes)
                logger.LogWarning("O disco de gravacoes esta proximo do limite.");
        }
        catch (Exception ex)
        {
            logger.LogWarning("Verificacao de espaco falhou: {Type}", ex.GetType().Name);
        }
    }
}
