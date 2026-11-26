using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NvrVideoLan;

public sealed record FoundOnvif(string Ip, string XAddr, string Scopes);
public sealed record OnvifStreamRequest(string OnvifUrl, string? Username, string? Password);
public sealed record OnvifProfile(string Name, string Token, int Width, int Height, string Url);
public sealed record OnvifStreamResult(string MainRtsp, string? SubRtsp, IReadOnlyList<OnvifProfile> Profiles);

public sealed class OnvifService
{
    private const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    private const string MediaNs = "http://www.onvif.org/ver10/media/wsdl";
    private const string PtzNs = "http://www.onvif.org/ver20/ptz/wsdl";

    private sealed record PtzEndpoint(Uri Address, string ProfileToken, string ConfigKey, DateTime ExpiresUtc);
    private readonly ConcurrentDictionary<Guid, PtzEndpoint> _ptzCache = new();

    public async Task<IReadOnlyList<FoundOnvif>> DiscoverAsync(CancellationToken ct)
    {
        string message = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
              xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
              xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
              xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
              <s:Header>
                <a:Action s:mustUnderstand="1">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>
                <a:MessageID>uuid:{Guid.NewGuid()}</a:MessageID>
                <a:To s:mustUnderstand="1">urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>
              </s:Header>
              <s:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></s:Body>
            </s:Envelope>
            """;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await udp.SendAsync(bytes,
            new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702), ct);

        var found = new Dictionary<string, FoundOnvif>(StringComparer.OrdinalIgnoreCase);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var reply = await udp.ReceiveAsync(deadline.Token);
                var xml = ParseXml(Encoding.UTF8.GetString(reply.Buffer));
                string? raw = xml.Descendants().FirstOrDefault(x =>
                    x.Name.LocalName == "XAddrs")?.Value;
                string scopes = xml.Descendants().FirstOrDefault(x =>
                    x.Name.LocalName == "Scopes")?.Value ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string? address = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(x => Uri.TryCreate(x, UriKind.Absolute, out var uri)
                        && (uri.Scheme == "http" || uri.Scheme == "https"));
                if (address is null) continue;
                string ip = reply.RemoteEndPoint.Address.ToString();
                found[ip] = new FoundOnvif(ip, address, scopes);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
            catch (XmlException) { /* Ignore malformed UDP discovery responses. */ }
            catch (SocketException) { break; }
        }
        return found.Values.OrderBy(x => x.Ip).ToArray();
    }

    public async Task<OnvifStreamResult> ResolveStreamsAsync(OnvifStreamRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OnvifUrl) ||
            !Uri.TryCreate(request.OnvifUrl, UriKind.Absolute, out var device) ||
            (device.Scheme != "http" && device.Scheme != "https") ||
            !IPAddress.TryParse(device.Host, out var lanIp) ||
            lanIp.AddressFamily != AddressFamily.InterNetwork ||
            !NetworkDiscovery.IsPrivateAddress(lanIp))
            throw new ArgumentException("Informe um endpoint ONVIF com endereço IPv4 privado.");

        string user = request.Username ?? "";
        string pass = request.Password ?? "";
        var capabilities = await SoapAsync(device, user, pass,
            $"<tds:GetCapabilities xmlns:tds=\"{DeviceNs}\"><tds:Category>All</tds:Category></tds:GetCapabilities>",
            DeviceNs + "/GetCapabilities", ct);
        Uri media = ServiceAddress(capabilities, "Media", device);
        var profilesReply = await SoapAsync(media, user, pass,
            $"<trt:GetProfiles xmlns:trt=\"{MediaNs}\"/>",
            MediaNs + "/GetProfiles", ct);

        var profiles = new List<OnvifProfile>();
        foreach (var profile in profilesReply.Descendants().Where(x => x.Name.LocalName == "Profiles"))
        {
            string? token = profile.Attributes().FirstOrDefault(x => x.Name.LocalName == "token")?.Value;
            if (string.IsNullOrWhiteSpace(token)) continue;

            string name = profile.Elements().FirstOrDefault(x => x.Name.LocalName == "Name")?.Value ?? token;
            var resolution = profile.Descendants().FirstOrDefault(x => x.Name.LocalName == "Resolution");
            int.TryParse(resolution?.Elements().FirstOrDefault(x => x.Name.LocalName == "Width")?.Value, out int width);
            int.TryParse(resolution?.Elements().FirstOrDefault(x => x.Name.LocalName == "Height")?.Value, out int height);
            string escaped = SecurityElement.Escape(token) ?? "";
            string body = $"""
                <trt:GetStreamUri xmlns:trt="{MediaNs}" xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream>
                    <tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport>
                  </trt:StreamSetup><trt:ProfileToken>{escaped}</trt:ProfileToken>
                </trt:GetStreamUri>
                """;
            try
            {
                var uriReply = await SoapAsync(media, user, pass, body, MediaNs + "/GetStreamUri", ct);
                var mediaUri = uriReply.Descendants().FirstOrDefault(x => x.Name.LocalName == "MediaUri");
                string? uriText = mediaUri?.Descendants().FirstOrDefault(x => x.Name.LocalName == "Uri")?.Value;
                if (!Uri.TryCreate(uriText, UriKind.Absolute, out var rtsp) ||
                    rtsp.Scheme is not ("rtsp" or "rtsps")) continue;

                var uriBuilder = new UriBuilder(rtsp);
                // Some devices advertise 0.0.0.0 as RTSP host.
                if (rtsp.Host == "0.0.0.0") uriBuilder.Host = device.Host;
                if (string.IsNullOrWhiteSpace(rtsp.UserInfo) && !string.IsNullOrWhiteSpace(user))
                {
                    uriBuilder.UserName = user;
                    uriBuilder.Password = pass;
                }
                profiles.Add(new OnvifProfile(name, token, width, height, uriBuilder.Uri.ToString()));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                or XmlException or TaskCanceledException)
            {
                // One unavailable profile should not prevent registration of others.
            }
        }

        if (profiles.Count == 0)
            throw new InvalidOperationException("Não foi possível obter URLs RTSP dos perfis ONVIF. Tente o cadastro manual.");

        var ordered = profiles.OrderByDescending(p => (long)p.Width * p.Height).ToArray();
        return new OnvifStreamResult(ordered[0].Url,
            ordered.Length > 1 ? ordered[^1].Url : null, ordered);
    }

    public async Task MoveAsync(Camera camera, string action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(camera.OnvifUrl))
            throw new InvalidOperationException("ONVIF não configurado.");

        (string user, string password) = Credentials(camera);
        var endpoint = await GetCachedPtzAsync(camera, user, password, ct);
        Uri ptz = endpoint.Address;
        string token = endpoint.ProfileToken;

        string t = SecurityElement.Escape(token) ?? "";
        string stopBody = $"<tptz:Stop xmlns:tptz=\"{PtzNs}\"><tptz:ProfileToken>{t}</tptz:ProfileToken><tptz:PanTilt>true</tptz:PanTilt><tptz:Zoom>true</tptz:Zoom></tptz:Stop>";
        if (action == "stop")
        {
            await SoapAsync(ptz, user, password, stopBody, PtzNs + "/Stop", ct);
            return;
        }

        (double x, double y, double z) = action switch
        {
            "left" => (-0.4d, 0d, 0d),
            "right" => (0.4d, 0d, 0d),
            "up" => (0d, 0.4d, 0d),
            "down" => (0d, -0.4d, 0d),
            "zoom_in" => (0d, 0d, 0.4d),
            "zoom_out" => (0d, 0d, -0.4d),
            _ => throw new InvalidOperationException("Comando PTZ desconhecido.")
        };
        string moveBody = System.FormattableString.Invariant($"""
            <tptz:ContinuousMove xmlns:tptz="{PtzNs}"
              xmlns:tt="http://www.onvif.org/ver10/schema">
              <tptz:ProfileToken>{t}</tptz:ProfileToken>
              <tptz:Velocity><tt:PanTilt x="{x}" y="{y}"/><tt:Zoom x="{z}"/></tptz:Velocity>
              <tptz:Timeout>PT0.25S</tptz:Timeout>
            </tptz:ContinuousMove>
            """);
        await SoapAsync(ptz, user, password, moveBody,
            PtzNs + "/ContinuousMove", ct);
        // Fail-safe: explicitly stop, even if the camera ignores Timeout.
        try { await Task.Delay(175, ct); }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await SoapAsync(ptz, user, password, stopBody, PtzNs + "/Stop", timeout.Token); }
            catch (Exception) { /* Camera may have disconnected. */ }
        }
    }

    private async Task<PtzEndpoint> GetCachedPtzAsync(Camera camera, string user, string password, CancellationToken ct)
    {
        string key = camera.OnvifUrl + "|" + user;
        if (_ptzCache.TryGetValue(camera.Id, out var saved) &&
            saved.ConfigKey == key && saved.ExpiresUtc > DateTime.UtcNow)
            return saved;

        var device = new Uri(camera.OnvifUrl!);
        var capabilities = await SoapAsync(device, user, password,
            $"<tds:GetCapabilities xmlns:tds=\"{DeviceNs}\"><tds:Category>All</tds:Category></tds:GetCapabilities>",
            DeviceNs + "/GetCapabilities", ct);
        Uri media = ServiceAddress(capabilities, "Media", device);
        Uri ptz = ServiceAddress(capabilities, "PTZ", device);

        var profiles = await SoapAsync(media, user, password,
            $"<trt:GetProfiles xmlns:trt=\"{MediaNs}\" />",
            MediaNs + "/GetProfiles", ct);
        string? token = profiles.Descendants()
            .Where(x => x.Name.LocalName == "Profiles")
            .FirstOrDefault(p => p.Elements().Any(x => x.Name.LocalName == "PTZConfiguration"))?
            .Attributes().FirstOrDefault(a => a.Name.LocalName == "token")?.Value;

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "A camera nao anunciou um perfil ONVIF com PTZConfiguration.");

        var resolved = new PtzEndpoint(ptz, token, key, DateTime.UtcNow.AddMinutes(10));
        _ptzCache[camera.Id] = resolved;
        return resolved;
    }

    public async Task<object> CheckPtzAsync(Camera camera, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(camera.OnvifUrl))
            throw new InvalidOperationException("Configure um endereco ONVIF para ativar o PTZ.");
        var device = new Uri(camera.OnvifUrl);
        (string user, string password) = Credentials(camera);
        var capabilities = await SoapAsync(device, user, password,
            $"<tds:GetCapabilities xmlns:tds=\"{DeviceNs}\"><tds:Category>All</tds:Category></tds:GetCapabilities>",
            DeviceNs + "/GetCapabilities", ct);
        Uri media = ServiceAddress(capabilities, "Media", device);
        Uri ptz = ServiceAddress(capabilities, "PTZ", device);
        var profiles = await SoapAsync(media, user, password,
            $"<trt:GetProfiles xmlns:trt=\"{MediaNs}\" />",
            MediaNs + "/GetProfiles", ct);
        int count = profiles.Descendants()
            .Count(x => x.Name.LocalName == "Profiles" &&
                x.Elements().Any(p => p.Name.LocalName == "PTZConfiguration"));
        if (count == 0)
            throw new InvalidOperationException(
                "O ONVIF respondeu, mas nao informou perfil com suporte a PTZ.");
        return new { available = true, profileCount = count, message =
            "Servico ONVIF PTZ anunciado com perfil compativel. Teste as setas para confirmar o movimento fisico." };
    }

    private static Uri ServiceAddress(XDocument doc, string name, Uri device)
    {
        string? value = doc.Descendants().FirstOrDefault(x =>
            x.Name.LocalName == name &&
            x.Elements().Any(child => child.Name.LocalName == "XAddr"))?
            .Elements().FirstOrDefault(child => child.Name.LocalName == "XAddr")?.Value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            !(endpoint.Scheme == "http" || endpoint.Scheme == "https"))
            throw new InvalidOperationException($"Servico ONVIF {name} nao foi anunciado pela camera.");
        if (endpoint.Host == "0.0.0.0" || endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = new UriBuilder(endpoint) { Host = device.Host };
            endpoint = normalized.Uri;
        }
        if (!string.Equals(endpoint.Host, device.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Servico ONVIF {name} aponta para outro host.");
        return endpoint;
    }

    private static (string user, string password) Credentials(Camera c)
    {
        if (!string.IsNullOrWhiteSpace(c.OnvifUser))
            return (c.OnvifUser, c.OnvifPassword ?? "");
        var userInfo = new Uri(c.MainRtsp).UserInfo.Split(':', 2);
        return (Uri.UnescapeDataString(userInfo[0]),
            userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "");
    }

    private static async Task<XDocument> SoapAsync(Uri url, string user, string password,
        string body, string action, CancellationToken ct)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            System.Globalization.CultureInfo.InvariantCulture);
        byte[] createdBytes = Encoding.UTF8.GetBytes(created);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] digestInput = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(nonce, 0, digestInput, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, digestInput, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, digestInput, nonce.Length + createdBytes.Length,
            passwordBytes.Length);
        string digest = Convert.ToBase64String(SHA1.HashData(digestInput));
        string username = SecurityElement.Escape(user) ?? "";
        string envelope = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope"
              xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
              xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
              <soap:Header><wsse:Security soap:mustUnderstand="1"><wsse:UsernameToken>
                <wsse:Username>{username}</wsse:Username>
                <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</wsse:Password>
                <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</wsse:Nonce>
                <wsu:Created>{created}</wsu:Created>
              </wsse:UsernameToken></wsse:Security></soap:Header>
              <soap:Body>{body}</soap:Body>
            </soap:Envelope>
            """;
        using var handler = new HttpClientHandler
        {
            UseProxy = false, Credentials = new NetworkCredential(user, password),
            PreAuthenticate = true
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", action);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string answer = await response.Content.ReadAsStringAsync(ct);
        var xml = ParseXml(answer);
        if (xml.Descendants().Any(x => x.Name.LocalName == "Fault"))
            throw new InvalidOperationException("Erro SOAP retornado pela câmera.");
        return xml;
    }

    private static XDocument ParseXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
        });
        return XDocument.Load(reader);
    }
}
