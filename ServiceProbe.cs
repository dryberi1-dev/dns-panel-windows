using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DnsPanel;

internal enum ServiceState { Waiting, Reachable, Restricted, Unreachable }

internal record WebService(string Name, Uri Address);
internal record ServiceResult(ServiceState State, string Status, string Detail);

internal static class ServiceProbe
{
    public static readonly WebService ChatGpt = new("ChatGPT", new Uri("https://chatgpt.com/"));
    public static readonly WebService Gemini = new("Gemini", new Uri("https://gemini.google.com/"));

    public static async Task<ServiceResult> CheckAsync(WebService service, Reading reading,
        NetworkInterface? adapter, CancellationToken cancellationToken)
    {
        if (reading.Addresses.Length == 0)
            return new(ServiceState.Unreachable, "Нет ответа DNS", "Выбранный DNS не ответил на проверку.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(9));
        var token = deadline.Token;
        var local = DnsProfiles.LocalAddresses(adapter);
        var server = reading.Addresses[0];
        using var dns = DohTransport.Open(server, local?.GetValueOrDefault(server.AddressFamily));
        var v4 = local is null || local.ContainsKey(AddressFamily.InterNetwork)
            ? ResolveAsync(dns, service, reading.Profile, false, token) : Task.FromResult<IPAddress[]>([]);
        var v6 = local is null || local.ContainsKey(AddressFamily.InterNetworkV6)
            ? ResolveAsync(dns, service, reading.Profile, true, token) : Task.FromResult<IPAddress[]>([]);
        var answers = await Task.WhenAll(v4, v6).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var targets = answers.SelectMany(set => set.Where(PublicAddress).Take(2)).ToArray();
        if (targets.Length == 0)
            return new(ServiceState.Unreachable, "Нет адреса сайта", $"DNS не вернул доступный IP для {service.Address.Host}.");

        var checks = targets.Select(address => ProbeAsync(service, address, local?.GetValueOrDefault(address.AddressFamily),
            token)).ToList();
        var result = new ServiceResult(ServiceState.Unreachable, "Нет HTTPS-ответа", "Соединение с сайтом не установилось.");
        while (checks.Count > 0)
        {
            var completed = await Task.WhenAny(checks).ConfigureAwait(false);
            checks.Remove(completed);
            var candidate = await completed.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.State == ServiceState.Reachable)
            {
                deadline.Cancel();
                return candidate;
            }
            if (candidate.State == ServiceState.Restricted ||
                result.State != ServiceState.Restricted && candidate.Detail.StartsWith("HTTP ", StringComparison.Ordinal))
                result = candidate;
        }
        return result;
    }

    private static async Task<IPAddress[]> ResolveAsync(HttpClient client, WebService service, Profile profile,
        bool ipv6, CancellationToken token)
    {
        try
        {
            return await DohTransport.ResolveAsync(client, profile.Endpoint, service.Address.Host, ipv6, token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<ServiceResult> ProbeAsync(WebService service, IPAddress remote, IPAddress? local,
        CancellationToken token)
    {
        try
        {
            using var http = DohTransport.Open(remote, local);
            var target = service.Address;
            for (var step = 0; step < 4; step++)
            {
                using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (response.Headers.TryGetValues("cf-mitigated", out var values) &&
                    values.Contains("challenge", StringComparer.OrdinalIgnoreCase))
                    return new(ServiceState.Restricted, "Проверка браузера", $"HTTP {code} · сайт запросил проверку.");
                if (response.IsSuccessStatusCode)
                    return new(ServiceState.Reachable, "Сайт отвечает", $"HTTP {code} · HTTPS-соединение установлено.");
                if (code is >= 300 and < 400 && response.Headers.Location is { } location &&
                    Uri.TryCreate(target, location, out var next))
                {
                    if (next.AbsolutePath.Contains("unsupported-country", StringComparison.OrdinalIgnoreCase) ||
                        next.AbsolutePath.Contains("not-available", StringComparison.OrdinalIgnoreCase))
                        return new(ServiceState.Unreachable, "Сайт сообщил об ограничении", $"HTTP {code} · переадресация на страницу ограничения.");
                    if (next.Scheme == "https" && next.IsDefaultPort && next.IdnHost == service.Address.IdnHost)
                    {
                        target = next;
                        continue;
                    }
                    return new(ServiceState.Restricted, "Переадресация", $"HTTP {code} · сайт направляет на {next.IdnHost}.");
                }
                if (code is 401 or 403 or 429 or 451)
                    return new(ServiceState.Restricted, "Сайт ограничил доступ", $"HTTP {code} · получен ответ сайта.");
                return new(ServiceState.Unreachable, "Сайт вернул ошибку", $"HTTP {code}.");
            }
            return new(ServiceState.Restricted, "Переадресация", "Сайт продолжает перенаправлять запрос.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return new(ServiceState.Unreachable, "Нет HTTPS-ответа", "Соединение с сайтом не установилось.");
        }
    }

    private static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xe0) == 0x20;
        return bytes[0] is > 0 and < 224 and not 10 and not 127 &&
            !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}
