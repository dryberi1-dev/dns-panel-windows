using System.Net.NetworkInformation;

namespace DnsPanel;

internal sealed class ServiceScan : IDisposable
{
    private readonly NetworkInterface _adapter;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _slots = new(8);
    private readonly Dictionary<(Profile, WebService), Task<ServiceResult>> _services = new();

    public ServiceScan(NetworkInterface adapter, CancellationToken lifetime)
    {
        _adapter = adapter;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _lifetime.Token;
        Readings = DnsProfiles.All.ToDictionary(profile => profile, profile => DnsProfiles.Check(profile, _token, adapter));
    }

    public IReadOnlyDictionary<Profile, Task<Reading>> Readings { get; }

    public Task<ServiceResult> CheckAsync(Profile profile, WebService service)
    {
        var key = (profile, service);
        if (!_services.TryGetValue(key, out var check))
        {
            check = ProbeAsync(profile, service);
            _services.Add(key, check);
        }
        return check;
    }

    private async Task<ServiceResult> ProbeAsync(Profile profile, WebService service)
    {
        var reading = await Readings[profile].ConfigureAwait(false);
        if (reading.Addresses.Length == 0)
            return await ServiceProbe.CheckAsync(service, reading, _adapter, _token).ConfigureAwait(false);

        await _slots.WaitAsync(_token).ConfigureAwait(false);
        try
        {
            return await ServiceProbe.CheckAsync(service, reading, _adapter, _token).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
