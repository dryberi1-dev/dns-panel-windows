using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DnsPanel;

internal record Profile(string Name, string Host)
{
    public Uri Endpoint => new($"https://{Host}/dns-query");
}

internal record Reading(Profile Profile, IPAddress[] Addresses, long? Milliseconds, string Status);

internal static class DnsProfiles
{
    public static readonly Profile[] All =
    [
        new("DNS-AI", "dns.dns-ai.ru"),
        new("MagicDNS", "dns.magicdns.ru"),
        new("Dagami", "dns.dagami.ru"),
        new("KeyOneDay", "dns.keyoneday.ru"),
        new("GeoHide EU", "eu.geohide.ru"),
        new("GeoHide US", "us.geohide.ru"),
        new("Wlrus", "dns.wlrus.cfd"),
        new("Безмежа", "dns.bezmezhau.com")
    ];

    private static readonly byte[] Query = Convert.FromHexString(
        "000001000001000000000000076578616D706C6503636F6D0000010001");

    public static async Task<Reading> Check(Profile profile, CancellationToken cancellationToken,
        NetworkInterface? adapter = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(profile.Host, token);
            var family = adapter?.GetIPProperties().UnicastAddresses
                .Select(address => address.Address)
                .Where(address => !address.IsIPv6LinkLocal && !IPAddress.IsLoopback(address))
                .GroupBy(address => address.AddressFamily)
                .ToDictionary(group => group.Key, group => group.First());
            var results = await Task.WhenAll(addresses.Distinct().Take(12)
                .Where(address => family is null || family.ContainsKey(address.AddressFamily))
                .Select(address => Measure(profile, address, family?.GetValueOrDefault(address.AddressFamily), token)));
            var answers = results.Where(result => result.Milliseconds.HasValue)
                .OrderBy(result => result.Milliseconds).ToArray();
            if (answers.Length > 0)
                return new(profile, answers.Select(answer => answer.Address).ToArray(),
                    answers[0].Milliseconds, "Работает");
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(profile, [], null, "Нет ответа");
    }

    private static async Task<(IPAddress Address, long? Milliseconds)> Measure(
        Profile profile, IPAddress address, IPAddress? local, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    if (local is not null)
                        socket.Bind(new IPEndPoint(local, 0));
                    await socket.ConnectAsync(address, 443, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 65_535 };
        var dns = Convert.ToBase64String(Query).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{profile.Endpoint}?dns={dns}");
        request.Headers.Accept.ParseAdd("application/dns-message");
        try
        {
            var clock = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentType?.MediaType != "application/dns-message")
                return (address, null);

            var packet = await response.Content.ReadAsByteArrayAsync(token);
            return (address, ValidAnswer(packet) ? clock.ElapsedMilliseconds : null);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return (address, null);
        }
    }

    private static bool ValidAnswer(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Query.Length + 12 || packet[0] != 0 || packet[1] != 0 ||
            (BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) & 0xfa0f) != 0x8000 ||
            BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) == 0 || !packet[12..Query.Length].SequenceEqual(Query.AsSpan(12)))
            return false;

        var answer = Query.Length;
        while (answer < packet.Length)
        {
            var length = packet[answer++];
            if ((length & 0xc0) == 0xc0)
            {
                if (answer == packet.Length || ((length & 0x3f) << 8 | packet[answer]) >= answer - 1)
                    return false;
                answer++;
                break;
            }
            if (length == 0)
                break;
            if (length > 63)
                return false;
            answer += length;
        }
        return answer + 10 <= packet.Length &&
            answer + 10 + BinaryPrimitives.ReadUInt16BigEndian(packet[(answer + 8)..]) <= packet.Length;
    }
}
