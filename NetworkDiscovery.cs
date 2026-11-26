using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NvrVideoLan;

public sealed record NetworkSubnet(string InterfaceName, string Address, string Cidr);
public sealed record RtspDevice(string Ip, int Port, string Protocol);

public sealed record RtspScanRequest(string Cidr, string? Mode = null);

public static class NetworkDiscovery
{
    public static IReadOnlyList<NetworkSubnet> LocalSubnets()
    {
        var result = new List<NetworkSubnet>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;
            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork ||
                    !IsPrivateAddress(ip.Address)) continue;
                var mask = ip.IPv4Mask?.GetAddressBytes();
                if (mask is null) continue;
                // Keep scans bounded to a /24 even when the local interface uses a larger network.
                byte[] host = ip.Address.GetAddressBytes();
                string subnet = $"{host[0]}.{host[1]}.{host[2]}.0/24";
                result.Add(new NetworkSubnet(nic.Name, ip.Address.ToString(), subnet));
            }
        }
        return result.DistinctBy(n => n.Cidr).ToArray();
    }

    public static async Task<IReadOnlyList<RtspDevice>> ScanRtspAsync(
        string cidr, string? mode, CancellationToken ct)
    {
        mode ??= "all";
        if (mode is not ("all" or "onvif" or "rtsp"))
            throw new ArgumentException("Modo de pesquisa inválido.");
        int[] ports = mode switch
        {
            "onvif" => new[] { 8899 },
            "rtsp" => new[] { 554, 8554 },
            _ => new[] { 554, 8554, 8899 }
        };
        string[] parts = cidr.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) ||
            baseIp.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out int prefix) || prefix is < 24 or > 30)
            throw new ArgumentException("Informe uma sub-rede IPv4 entre /24 e /30.");

        byte[] octets = baseIp.GetAddressBytes();
        if (!IsPrivateAddress(baseIp))
            throw new ArgumentException("Somente faixas de IP privado são permitidas.");

        uint raw = ((uint)octets[0] << 24) | ((uint)octets[1] << 16)
            | ((uint)octets[2] << 8) | octets[3];
        uint mask = uint.MaxValue << (32 - prefix);
        uint network = raw & mask;
        int size = 1 << (32 - prefix);
        var found = new ConcurrentBag<RtspDevice>();
        using var semaphore = new SemaphoreSlim(32);

        var tasks = Enumerable.Range(1, size - 2).Select(async offset =>
        {
            uint ipNumber = network + (uint)offset;
            byte[] candidate = {
                (byte)(ipNumber >> 24), (byte)(ipNumber >> 16),
                (byte)(ipNumber >> 8), (byte)ipNumber
            };
            var ip = new IPAddress(candidate);
            // Restrict each destination to RFC1918 addresses.
            if (!IsPrivateAddress(ip)) return;
            await semaphore.WaitAsync(ct);
            try
            {
                foreach (int port in ports)
                {
                    using var client = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(650));
                    try
                    {
                        await client.ConnectAsync(ip, port, timeout.Token);
                        found.Add(new RtspDevice(ip.ToString(), port,
                            port == 8899 ? "ONVIF_CANDIDATE" : "RTSP"));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    catch (SocketException) { }
                }
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
        return found.OrderBy(x => x.Ip, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsPrivateAddress(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10 ||
               b[0] == 172 && b[1] is >= 16 and <= 31 ||
               b[0] == 192 && b[1] == 168;
    }
}
