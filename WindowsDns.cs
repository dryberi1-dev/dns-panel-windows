using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace DnsPanel;

internal static class WindowsDns
{
    private const string BackupKey = @"SOFTWARE\DnsPanelWindows";
    private const ulong Ipv6 = 1;
    private const ulong Nameserver = 2;
    private const ulong Doh = 0x1000;
    private const int DohPropertyType = 1;
    private const ulong DohEnabled = 2;

    private record DohServer(uint Index, string Template, ulong Flags);
    private record Family(bool IsV6, bool Automatic, string? Servers, DohServer[] Properties, string Registration);
    private record Backup(Family[] Original, Family[] Last, Family[]? Pending);

    public static bool HasBackup(Guid adapter)
    {
        using var key = Registry.LocalMachine.OpenSubKey(BackupKey);
        return key?.GetValue(adapter.ToString("D")) is string;
    }

    public static int Inspect(string path)
    {
        var snapshots = Program.Adapters().Select(adapter => new
        {
            adapter.Name,
            Settings = ReadFamilies(adapter)
        });
        File.WriteAllText(path, JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static int RunWorker(string[] args)
    {
        var applying = args.Length == 3 && args[0] == "--apply";
        var restoring = args.Length == 2 && args[0] == "--restore";
        if ((!applying && !restoring) || !Guid.TryParseExact(args[1], "D", out var id))
            return 2;

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return 3;

        try
        {
            var adapter = Program.Adapters().FirstOrDefault(item => Guid.Parse(item.Id) == id)
                ?? throw new InvalidOperationException("Сетевой адаптер исчез из Windows.");
            Reading? reading = null;
            if (applying)
            {
                if (!int.TryParse(args[2], out var index) || (uint)index >= DnsProfiles.All.Length)
                    return 2;
                reading = DnsProfiles.Check(DnsProfiles.All[index], CancellationToken.None, adapter).GetAwaiter().GetResult();
                if (!Program.Connected(adapter) || reading.Addresses.Length == 0)
                    throw new InvalidOperationException("Сервер не ответил на повторную проверку.");
            }

            using var gate = new Mutex(false, @"Global\DnsPanelWindowsSettings");
            try
            {
                if (!gate.WaitOne(TimeSpan.FromSeconds(15)))
                    throw new InvalidOperationException("Дождитесь завершения другого изменения DNS.");
            }
            catch (AbandonedMutexException)
            {
                // The backup contains both sides of the interrupted operation.
            }

            try
            {
                if (reading is null)
                    Restore(adapter);
                else
                    Apply(adapter, reading);
            }
            finally
            {
                gate.ReleaseMutex();
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "DNS-пульт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    private static void Apply(NetworkInterface adapter, Reading reading)
    {
        var id = Guid.Parse(adapter.Id);
        var previous = ReadFamilies(adapter);
        var backup = ReadBackup(id);
        if (backup?.Pending is not null)
            throw new InvalidOperationException("Сначала нажмите «Вернуть исходный DNS», чтобы завершить предыдущее изменение.");
        if (backup is not null && !Same(previous, backup.Last))
            throw new InvalidOperationException("После последнего переключения настройки адаптера были изменены в Windows.");

        if (!previous.Any(family => reading.Addresses.Any(address => IsV6(address) == family.IsV6)))
            throw new InvalidOperationException("Адреса сервера недоступны на этом адаптере.");

        var target = previous.Select((family, index) =>
        {
            var addresses = reading.Addresses
                .Where(address => IsV6(address) == family.IsV6).Take(4).ToArray();
            if (addresses.Length == 0)
                return backup?.Original[index] ?? family;
            return new Family(family.IsV6, false, string.Join(",", addresses.Select(address => address.ToString())),
                addresses.Select((_, index) => new DohServer((uint)index, reading.Profile.Endpoint.AbsoluteUri, DohEnabled)).ToArray(),
                family.Registration);
        }).ToArray();
        if (Same(previous, target))
            return;

        WriteBackup(id, new Backup(backup?.Original ?? previous, previous, target));
        Change(adapter, previous, target, () => WriteBackup(id, new Backup(backup?.Original ?? previous, target, null)),
            () => WriteBackup(id, backup));
    }

    private static void Restore(NetworkInterface adapter)
    {
        var id = Guid.Parse(adapter.Id);
        var backup = ReadBackup(id) ?? throw new InvalidOperationException("Резервная копия этого адаптера отсутствует.");
        var previous = ReadFamilies(adapter);
        if (previous.Length != backup.Original.Length || previous.Any(family =>
            !backup.Original.Concat(backup.Last).Concat(backup.Pending ?? []).Any(saved => Same(family, saved))))
            throw new InvalidOperationException("Настройки адаптера менялись вне DNS-пульта. Резервная копия сохранена.");

        Change(adapter, previous, backup.Original, () => WriteBackup(id, null), () => WriteBackup(id, backup));
    }

    private static void Change(NetworkInterface adapter, Family[] previous, Family[] target, Action commit, Action rolledBack)
    {
        var attempted = new List<Family>();
        try
        {
            for (var index = 0; index < target.Length; index++)
            {
                if (Same(previous[index], target[index]))
                    continue;
                attempted.Add(previous[index]);
                WriteFamily(adapter, target[index]);
            }
            if (!Same(ReadFamilies(adapter), target))
                throw new InvalidOperationException("Windows вернула настройки, отличные от выбранных.");
            commit();
        }
        catch (Exception error)
        {
            var failures = new List<string>();
            foreach (var original in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    WriteFamily(adapter, original);
                }
                catch (Exception rollbackError)
                {
                    failures.Add(rollbackError.Message);
                }
            }
            try
            {
                if (failures.Count == 0 && Same(ReadFamilies(adapter), previous))
                    rolledBack();
                else
                    failures.Add("Windows не подтвердила все прежние настройки.");
            }
            catch (Exception verificationError)
            {
                failures.Add(verificationError.Message);
            }
            var recovery = failures.Count == 0 ? "Прежние настройки сохранены." :
                $"Резервная копия сохранена. {string.Join(" ", failures)}";
            throw new InvalidOperationException($"{error.Message}\n\n{recovery}", error);
        }
    }

    private static Family[] ReadFamilies(NetworkInterface adapter)
    {
        var families = new List<Family>(2);
        if (adapter.Supports(NetworkInterfaceComponent.IPv4))
            families.Add(ReadFamily(adapter, false));
        if (adapter.Supports(NetworkInterfaceComponent.IPv6))
            families.Add(ReadFamily(adapter, true));
        return families.ToArray();
    }

    private static Family ReadFamily(NetworkInterface adapter, bool isV6)
    {
        var output = Netsh(adapter, isV6, "show");
        var automatic = output.Contains("configured through DHCP", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("настройкой через DHCP", StringComparison.OrdinalIgnoreCase);
        var manual = output.Contains("Statically Configured DNS Servers", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Статически настроенные DNS-серверы", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("DNS-серверы со статической настройкой", StringComparison.OrdinalIgnoreCase);
        var registrationLine = output.Split('\n').FirstOrDefault(line =>
                line.TrimStart().StartsWith("Register with which suffix", StringComparison.OrdinalIgnoreCase) ||
                line.TrimStart().StartsWith("Зарегистрировать с суффиксом", StringComparison.OrdinalIgnoreCase));
        var registration = registrationLine?.Split(':', 2).Last().Trim().ToUpperInvariant() switch
        {
            "NONE" or "НЕТ" => "none",
            "PRIMARY ONLY" or "ТОЛЬКО ОСНОВНОЙ" => "primary",
            "BOTH" or "ОБА" => "both",
            _ => null
        };
        if (automatic == manual || registration is null)
            throw new InvalidOperationException($"Не удалось определить текущий режим IPv{(isV6 ? 6 : 4)} DNS. Поддерживаются русская и английская Windows 11.");

        var settings = new Settings { Version = 3, Flags = isV6 ? Ipv6 : 0 };
        Check(GetInterfaceDnsSettings(Guid.Parse(adapter.Id), ref settings));
        try
        {
            var addresses = Marshal.PtrToStringUni(settings.NameServer);
            var properties = new List<DohServer>();
            for (var index = 0; index < settings.ServerPropertyCount; index++)
            {
                var property = Marshal.PtrToStructure<ServerProperty>(
                    IntPtr.Add(settings.ServerProperties, index * Marshal.SizeOf<ServerProperty>()));
                if (property.Version != 1 || property.Type != DohPropertyType || property.Settings == IntPtr.Zero)
                    throw new InvalidOperationException("Windows использует формат настроек DNS, который эта версия не может восстановить.");
                var value = Marshal.PtrToStructure<DohSettings>(property.Settings);
                properties.Add(new DohServer(property.ServerIndex, Marshal.PtrToStringUni(value.Template) ?? "", value.Flags));
            }
            return new Family(isV6, automatic, addresses, properties.OrderBy(property => property.Index).ToArray(), registration);
        }
        finally
        {
            FreeInterfaceDnsSettings(ref settings);
        }
    }

    private static void WriteFamily(NetworkInterface adapter, Family family)
    {
        if (family.Automatic || string.IsNullOrEmpty(family.Servers))
        {
            var registration = $"register={family.Registration}";
            if (family.Automatic)
                Netsh(adapter, family.IsV6, "set", "source=dhcp", registration, "validate=no");
            else
                Netsh(adapter, family.IsV6, "set", "source=static", "address=none", registration, "validate=no");
            return;
        }

        var allocations = new List<IntPtr>();
        IntPtr Allocate(IntPtr pointer)
        {
            allocations.Add(pointer);
            return pointer;
        }

        try
        {
            var size = Marshal.SizeOf<ServerProperty>();
            var list = family.Properties.Length == 0 ? IntPtr.Zero :
                Allocate(Marshal.AllocHGlobal(size * family.Properties.Length));
            for (var index = 0; index < family.Properties.Length; index++)
            {
                var property = family.Properties[index];
                var template = property.Template.Length == 0 ? IntPtr.Zero :
                    Allocate(Marshal.StringToHGlobalUni(property.Template));
                var data = Allocate(Marshal.AllocHGlobal(Marshal.SizeOf<DohSettings>()));
                Marshal.StructureToPtr(new DohSettings { Template = template, Flags = property.Flags }, data, false);
                Marshal.StructureToPtr(new ServerProperty
                {
                    Version = 1, ServerIndex = property.Index, Type = DohPropertyType, Settings = data
                }, IntPtr.Add(list, index * size), false);
            }

            var settings = new Settings
            {
                Version = 3, Flags = Nameserver | Doh | (family.IsV6 ? Ipv6 : 0),
                NameServer = Allocate(Marshal.StringToHGlobalUni(family.Servers)),
                ServerPropertyCount = (uint)family.Properties.Length, ServerProperties = list
            };
            Check(SetInterfaceDnsSettings(Guid.Parse(adapter.Id), ref settings));
        }
        finally
        {
            foreach (var pointer in allocations)
                Marshal.FreeHGlobal(pointer);
        }
    }

    private static string Netsh(NetworkInterface adapter, bool isV6, string verb, params string[] arguments)
    {
        var properties = adapter.GetIPProperties();
        var index = isV6 ? properties.GetIPv6Properties().Index : properties.GetIPv4Properties().Index;
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "interface", isV6 ? "ipv6" : "ipv4", verb, "dnsservers", $"name={index}" }.Concat(arguments))
            command.ArgumentList.Add(argument);
        using var worker = Process.Start(command)!;
        using var bytes = new MemoryStream();
        var output = worker.StandardOutput.BaseStream.CopyToAsync(bytes);
        var errors = worker.StandardError.ReadToEndAsync();
        worker.WaitForExit();
        output.GetAwaiter().GetResult();
        var data = bytes.ToArray();
        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            decoded = Encoding.GetEncoding((int)GetOEMCP()).GetString(data);
        }
        if (worker.ExitCode != 0)
            throw new InvalidOperationException($"netsh ({worker.ExitCode}) {decoded.Trim()} {errors.Result.Trim()}");
        return decoded;
    }

    private static bool Same(Family[] first, Family[] second) =>
        first.Length == second.Length && first.Zip(second).All(pair => Same(pair.First, pair.Second));

    private static bool IsV6(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6;

    private static bool Same(Family first, Family second) =>
        first.IsV6 == second.IsV6 && first.Automatic == second.Automatic && first.Registration == second.Registration &&
        string.Equals(first.Servers?.Replace(" ", "") ?? "", second.Servers?.Replace(" ", "") ?? "", StringComparison.OrdinalIgnoreCase) &&
        first.Properties.SequenceEqual(second.Properties);

    private static Backup? ReadBackup(Guid id)
    {
        using var key = Registry.LocalMachine.OpenSubKey(BackupKey);
        return key?.GetValue(id.ToString("D")) is string json ? JsonSerializer.Deserialize<Backup>(json) : null;
    }

    private static void WriteBackup(Guid id, Backup? backup)
    {
        using var key = Registry.LocalMachine.CreateSubKey(BackupKey);
        if (backup is null)
            key.DeleteValue(id.ToString("D"), false);
        else
            key.SetValue(id.ToString("D"), JsonSerializer.Serialize(backup), RegistryValueKind.String);
        key.Flush();
    }

    private static void Check(uint status)
    {
        if (status != 0)
            throw new Win32Exception((int)status);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetInterfaceDnsSettings(Guid interfaceId, ref Settings settings);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint SetInterfaceDnsSettings(Guid interfaceId, ref Settings settings);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeInterfaceDnsSettings(ref Settings settings);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetOEMCP();

    [StructLayout(LayoutKind.Sequential)]
    private struct Settings
    {
        public uint Version;
        public ulong Flags;
        public IntPtr Domain;
        public IntPtr NameServer;
        public IntPtr SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLlmnr;
        public uint QueryAdapterName;
        public IntPtr ProfileNameServer;
        public uint DisableUnconstrainedQueries;
        public IntPtr SupplementalSearchList;
        public uint ServerPropertyCount;
        public IntPtr ServerProperties;
        public uint ProfileServerPropertyCount;
        public IntPtr ProfileServerProperties;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServerProperty
    {
        public uint Version;
        public uint ServerIndex;
        public int Type;
        public IntPtr Settings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DohSettings
    {
        public IntPtr Template;
        public ulong Flags;
    }
}
