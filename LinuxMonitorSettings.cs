using System.Text.Json;
namespace NvrVideoLan;
public sealed record LinuxMonitorRequest(int Rows, int Columns, Guid[] CameraIds);
public sealed class LinuxMonitorSettings
{
    private readonly string _file;
    private readonly object _gate = new();
    private LinuxMonitorRequest? _current;
    public LinuxMonitorSettings(string data)
    {
        _file = Path.Combine(data, "linux-monitor.json");
        try { if (File.Exists(_file)) _current = JsonSerializer.Deserialize<LinuxMonitorRequest>(File.ReadAllText(_file)); }
        catch (JsonException) { }
        if (_current is not null && !Valid(_current)) _current = null;
    }
    private static bool Valid(LinuxMonitorRequest r) => r.Rows is >= 1 and <= 16 &&
        r.Columns is >= 1 and <= 16 && r.Rows * r.Columns <= 16 &&
        r.CameraIds is not null && r.CameraIds.Length > 0 && r.CameraIds.Length <= r.Rows * r.Columns &&
        r.CameraIds.Distinct().Count() == r.CameraIds.Length;
    public LinuxMonitorRequest Current(CameraStore cameras)
    {
        lock (_gate) return _current ?? new(3, 4, cameras.GetAll().Where(c => c.PreviewEnabled).Take(12).Select(c => c.Id).ToArray());
    }
    public void Save(LinuxMonitorRequest r, CameraStore cameras)
    {
        if (!Valid(r) || r.CameraIds.Any(id => cameras.Get(id) is not { PreviewEnabled: true }))
            throw new ArgumentException("Escolha cameras com previa ativa e ate 16 posicoes.");
        lock (_gate)
        {
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(r));
            File.Move(temp, _file, true);
            _current = r;
        }
    }
}
