using System.Text.Json;

namespace NvrVideoLan;

public sealed record Camera(
    Guid Id, string Name, string MainRtsp, string? SubRtsp,
    string? OnvifUrl, string? OnvifUser, string? OnvifPassword)
{
    // Defaults also apply to cameras.json files saved by previous versions.
    public bool RecordEnabled { get; init; } = true;
    public bool PreviewEnabled { get; init; } = true;
    public string RecordingStream { get; init; } = "main";
    public string RecordingCodec { get; init; } = "copy";
    public bool RecordAudio { get; init; } = false;
    public bool PreviewAudio { get; init; } = false;
    public string PreviewStream { get; init; } = "sub";
    public string ScheduleMode { get; init; } = "always";
    public int[] ScheduleDays { get; init; } = new[] { 0, 1, 2, 3, 4, 5, 6 };
    public string ScheduleStart { get; init; } = "00:00";
    public string ScheduleEnd { get; init; } = "00:00";
}

public sealed record AddCamera(
    string Name, string MainRtsp, string? SubRtsp,
    string? OnvifUrl, string? OnvifUser, string? OnvifPassword)
{
    public bool RecordEnabled { get; init; } = true;
    public bool PreviewEnabled { get; init; } = true;
    public string RecordingStream { get; init; } = "main";
    public string RecordingCodec { get; init; } = "copy";
    public bool RecordAudio { get; init; } = false;
    public bool PreviewAudio { get; init; } = false;
    public string PreviewStream { get; init; } = "sub";
    public string ScheduleMode { get; init; } = "always";
    public int[] ScheduleDays { get; init; } = new[] { 0, 1, 2, 3, 4, 5, 6 };
    public string ScheduleStart { get; init; } = "00:00";
    public string ScheduleEnd { get; init; } = "00:00";
}

public sealed record LoginRequest(string Password);
public sealed record PtzRequest(string Action);

public sealed record NvrConfig(
    string DataRoot, string RecordingsRoot, string PreviewRoot,
    string FfmpegExecutable, long MaxRecordingBytes, long MinimumFreeBytes);

public sealed class CameraStore
{
    private readonly object _sync = new();
    private readonly string _file;
    private List<Camera> _cameras;

    public CameraStore(string file)
    {
        _file = file;
        _cameras = File.Exists(file)
            ? JsonSerializer.Deserialize<List<Camera>>(File.ReadAllText(file)) ?? new()
            : new();
    }

    public Camera[] GetAll()
    {
        lock (_sync) return _cameras.ToArray();
    }

    public Camera? Get(Guid id)
    {
        lock (_sync) return _cameras.FirstOrDefault(c => c.Id == id);
    }

    public Camera Add(AddCamera input)
    {
        var camera = Build(Guid.NewGuid(), input);
        lock (_sync)
        {
            _cameras.Add(camera);
            Save();
        }
        return camera;
    }

    public Camera? Update(Guid id, AddCamera input)
    {
        var updated = Build(id, input);
        lock (_sync)
        {
            int index = _cameras.FindIndex(c => c.Id == id);
            if (index < 0) return null;
            _cameras[index] = updated;
            Save();
            return updated;
        }
    }

    private static Camera Build(Guid id, AddCamera input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80)
            throw new ArgumentException("Informe um nome de até 80 caracteres.");
        CheckUrl(input.MainRtsp, "RTSP principal", "rtsp", "rtsps");
        if (!string.IsNullOrWhiteSpace(input.SubRtsp))
            CheckUrl(input.SubRtsp, "RTSP extra", "rtsp", "rtsps");
        if (!string.IsNullOrWhiteSpace(input.OnvifUrl))
            CheckUrl(input.OnvifUrl, "ONVIF", "http", "https");
        string recordingStream = ValidateStream(input.RecordingStream);
        string recordingCodec = input.RecordingCodec?.ToLowerInvariant() ?? "copy";
        if (recordingCodec is not ("copy" or "h264" or "h265"))
            throw new ArgumentException("Formato de gravacao deve ser Original, H.264 ou H.265.");
        string previewStream = ValidateStream(input.PreviewStream);
        string scheduleMode = input.ScheduleMode?.ToLowerInvariant() ?? "always";
        if (scheduleMode is not ("always" or "window"))
            throw new ArgumentException("Agenda deve ser continua ou por horarios.");
        int[] days = input.ScheduleDays ?? Array.Empty<int>();
        if (scheduleMode == "window" && (days.Length == 0 || days.Any(d => d < 0 || d > 6)))
            throw new ArgumentException("Selecione pelo menos um dia valido para a agenda.");
        if (!TimeSpan.TryParseExact(input.ScheduleStart, @"hh\:mm", null, out _) ||
            !TimeSpan.TryParseExact(input.ScheduleEnd, @"hh\:mm", null, out _))
            throw new ArgumentException("Horarios precisam estar no formato HH:mm.");
        if (input.RecordEnabled && recordingStream == "sub" && string.IsNullOrWhiteSpace(input.SubRtsp))
            throw new ArgumentException("Para gravar o fluxo extra, informe RTSP extra.");
        if (input.PreviewEnabled && previewStream == "sub" && string.IsNullOrWhiteSpace(input.SubRtsp))
            previewStream = "main"; // Backward compatibility with single-stream cameras.
        return new Camera(id, input.Name.Trim(), input.MainRtsp.Trim(), input.SubRtsp?.Trim(),
            input.OnvifUrl?.Trim(), input.OnvifUser, input.OnvifPassword)
        {
            RecordEnabled = input.RecordEnabled,
            PreviewEnabled = input.PreviewEnabled,
            RecordingStream = recordingStream,
            RecordingCodec = recordingCodec,
            RecordAudio = input.RecordAudio,
            PreviewAudio = input.PreviewAudio,
            PreviewStream = previewStream,
            ScheduleMode = scheduleMode,
            ScheduleDays = days.Distinct().OrderBy(d => d).ToArray(),
            ScheduleStart = input.ScheduleStart,
            ScheduleEnd = input.ScheduleEnd
        };
    }

    private static string ValidateStream(string? value)
    {
        string stream = string.IsNullOrWhiteSpace(value) ? "main" : value.ToLowerInvariant();
        if (stream is not ("main" or "sub"))
            throw new ArgumentException("Selecione fluxo principal ou extra.");
        return stream;
    }

    public bool Remove(Guid id)
    {
        lock (_sync)
        {
            bool removed = _cameras.RemoveAll(c => c.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private void Save()
    {
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_cameras,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, true);
    }

    private static void CheckUrl(string? text, string label, params string[] schemes)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) ||
            !schemes.Contains(url.Scheme, StringComparer.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(url.Host))
            throw new ArgumentException($"Endereço de {label} inválido.");
    }
}
