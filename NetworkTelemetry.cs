using System.Net.NetworkInformation;

namespace NvrVideoLan;

/// <summary>Samples Windows network-interface byte counters every 2 seconds.</summary>
public sealed record NetworkTrafficPoint(string Time, double DownloadMbps,
    double UploadMbps);

public sealed class NetworkTelemetry : BackgroundService
{
    private readonly object _gate = new();
    private readonly Queue<NetworkTrafficPoint> _history = new();
    private const int MaxSamples = 90;

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                downloadMbps = _history.LastOrDefault()?.DownloadMbps ?? 0,
                uploadMbps = _history.LastOrDefault()?.UploadMbps ?? 0,
                sampleIntervalSeconds = 2,
                points = _history.ToArray()
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var previous = new Dictionary<string, (long Rx, long Tx)>();
        DateTime lastSample = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                double elapsed = (now - lastSample).TotalSeconds;
                var current = new Dictionary<string, (long Rx, long Tx)>();
                double received = 0, transmitted = 0;

                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback
                            or NetworkInterfaceType.Tunnel)
                        continue;
                    try
                    {
                        var count = adapter.GetIPv4Statistics();
                        current[adapter.Id] = (count.BytesReceived, count.BytesSent);
                        if (elapsed > 0 && previous.TryGetValue(adapter.Id, out var old))
                        {
                            // Counters can reset or wrap. Never report a negative spike.
                            received += Math.Max(0, count.BytesReceived - old.Rx);
                            transmitted += Math.Max(0, count.BytesSent - old.Tx);
                        }
                    }
                    catch (NetworkInformationException) { /* adapter went offline */ }
                }

                if (previous.Count > 0 && elapsed > 0.1)
                {
                    var point = new NetworkTrafficPoint(
                        DateTime.Now.ToString("HH:mm:ss"),
                        Math.Round(received * 8 / elapsed / 1_000_000, 3),
                        Math.Round(transmitted * 8 / elapsed / 1_000_000, 3));
                    lock (_gate)
                    {
                        _history.Enqueue(point);
                        while (_history.Count > MaxSamples) _history.Dequeue();
                    }
                }
                previous = current;
                lastSample = now;
            }
            catch (Exception ex) when (ex is NetworkInformationException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                // Monitoring must never stop the NVR's recording worker.
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
