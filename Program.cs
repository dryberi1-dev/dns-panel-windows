using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using Microsoft.Win32;

namespace DnsPanel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--check")
        {
            File.WriteAllLines(args[1], Task.WhenAll(DnsProfiles.All.Select(profile =>
                DnsProfiles.Check(profile, CancellationToken.None))).GetAwaiter().GetResult()
                .Select(result => $"{result.Profile.Name}\t{result.Status}\t{result.Milliseconds}\t{string.Join<IPAddress>(", ", result.Addresses)}"));
            return 0;
        }

        if (args.Length == 2 && args[0] == "--inspect")
            return WindowsDns.Inspect(args[1]);

        if (args.Length > 0)
            return WindowsDns.RunWorker(args);

        return new Application { ShutdownMode = ShutdownMode.OnMainWindowClose }.Run(new MainForm());
    }

    internal static NetworkInterface[] Adapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            Guid.TryParse(adapter.Id, out _) && IsHardware(adapter.Id))
        .OrderBy(adapter => adapter.GetIPProperties().GetIPv4Properties()?.Index ?? int.MaxValue)
        .ToArray();

    internal static bool Connected(NetworkInterface adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up && adapter.GetIPProperties().GatewayAddresses.Any();

    private static bool IsHardware(string adapter)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Control\Network\{{4D36E972-E325-11CE-BFC1-08002BE10318}}\{adapter}\Connection");
        var identity = key?.GetValue("PnpInstanceID") as string;
        return identity?.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) == true ||
            identity?.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) == true ||
            identity?.StartsWith(@"VMBUS\", StringComparison.OrdinalIgnoreCase) == true;
    }
}
