using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
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

    internal static Dictionary<AddressFamily, IPAddress>? LocalAddresses(NetworkInterface? adapter) =>
        adapter?.GetIPProperties().UnicastAddresses
            .Select(address => address.Address)
            .Where(address => !address.IsIPv6LinkLocal && !IPAddress.IsLoopback(address))
            .GroupBy(address => address.AddressFamily)
            .ToDictionary(group => group.Key, group => group.First());

    public static async Task<Reading> Check(Profile profile, CancellationToken cancellationToken,
        NetworkInterface? adapter = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(profile.Host, token);
            var family = LocalAddresses(adapter);
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
        using var client = DohTransport.Open(address, local);
        try
        {
            var clock = Stopwatch.StartNew();
            var answer = await DohTransport.ResolveAsync(client, profile.Endpoint, "example.com", false, token);
            return (address, answer.Length > 0 ? clock.ElapsedMilliseconds : null);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return (address, null);
        }
    }

}
