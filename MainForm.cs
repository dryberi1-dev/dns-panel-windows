using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DnsPanel;

internal partial class MainForm : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly ProfileRow[] _rows;
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _serviceCheck;
    private Task<Reading>[]? _readings;
    private bool _ready;
    private bool _working;
    private bool _loadingAdapters;

    public MainForm()
    {
        InitializeComponent();
        _rows = DnsProfiles.All.Select(profile => new ProfileRow(profile, Brush("Waiting"))).ToArray();
        ProfileList.ItemsSource = _rows;
        ProfileList.SelectedIndex = 1;
        ShowService(ChatCard, ServiceProbe.ChatGpt, Waiting("Ожидание", "Проверка начнётся после запуска."));
        ShowService(GeminiCard, ServiceProbe.Gemini, Waiting("Ожидание", "Проверка начнётся после запуска."));
        SourceInitialized += (_, _) =>
        {
            var caption = 0x00F2F5F0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 35, ref caption, sizeof(int));
        };
        Loaded += async (_, _) =>
        {
            _ready = true;
            await RefreshAsync();
            if (!_lifetime.IsCancellationRequested)
                _timer.Start();
        };
        _timer.Tick += async (_, _) =>
        {
            if (!_working && _refresh is null)
                await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _lifetime.Cancel();
        };
    }

    private NetworkInterface? SelectedAdapter => AdapterPicker.SelectedItem is NetworkInterface selected
        ? Program.Adapters().FirstOrDefault(adapter => adapter.Id == selected.Id) : null;

    private void LoadAdapters()
    {
        var adapters = Program.Adapters().Where(adapter =>
            Program.Connected(adapter) || WindowsDns.HasBackup(Guid.Parse(adapter.Id))).ToArray();
        if (AdapterPicker.ItemsSource is NetworkInterface[] previous &&
            adapters.Select(adapter => adapter.Id).SequenceEqual(previous.Select(adapter => adapter.Id)))
            return;

        var id = (AdapterPicker.SelectedItem as NetworkInterface)?.Id;
        _loadingAdapters = true;
        try
        {
            AdapterPicker.ItemsSource = adapters;
            AdapterPicker.SelectedIndex = adapters.Length == 0 ? -1 : Math.Max(0, Array.FindIndex(adapters, item => item.Id == id));
        }
        finally
        {
            _loadingAdapters = false;
        }
    }

    private async Task RefreshAsync()
    {
        _refresh?.Cancel();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refresh = refresh;
        LoadAdapters();
        var adapter = SelectedAdapter;
        var connected = adapter is not null && Program.Connected(adapter);
        UpdateControls(adapter);

        try
        {
            foreach (var row in _rows)
                row.Set(connected ? "Проверка…" : "Нет сети", Brush("Waiting"));
            _readings = connected
                ? DnsProfiles.All.Select(profile => DnsProfiles.Check(profile, refresh.Token, adapter)).ToArray()
                : null;
            if (_readings is null)
            {
                await InspectServicesAsync();
                return;
            }
            var paint = _readings.Select((reading, index) => PaintAsync(reading, _rows[index], refresh.Token));
            await Task.WhenAll(paint.Append(InspectServicesAsync()));
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_refresh, refresh))
            {
                _refresh = null;
                if (!_lifetime.IsCancellationRequested)
                    UpdateControls(SelectedAdapter);
            }
        }
    }

    private async Task PaintAsync(Task<Reading> pending, ProfileRow row, CancellationToken token)
    {
        var reading = await pending;
        token.ThrowIfCancellationRequested();
        if (reading.Milliseconds is long milliseconds)
            row.Set($"{milliseconds} мс", Brush("Positive"));
        else
            row.Set(reading.Status, Brush("Negative"));
    }

    private async Task InspectServicesAsync()
    {
        _serviceCheck?.Cancel();
        if (ProfileList.SelectedItem is not ProfileRow row)
            return;

        ProfileTitle.Text = row.Name;
        ProfileHost.Text = row.Host;
        using var check = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _serviceCheck = check;
        var adapter = SelectedAdapter;
        var index = ProfileList.SelectedIndex;
        var pending = _readings?[index];
        var status = pending is null
            ? Waiting("Нет сети", "Подключите сетевой адаптер для проверки.")
            : Waiting("Проверка…", $"Проверяем сайт через {row.Name}.");
        ShowService(ChatCard, ServiceProbe.ChatGpt, status);
        ShowService(GeminiCard, ServiceProbe.Gemini, status);

        try
        {
            if (pending is not null)
            {
                await Task.Delay(120, check.Token);
                var reading = await pending.WaitAsync(check.Token);
                await Task.WhenAll(
                    CheckCardAsync(ChatCard, ServiceProbe.ChatGpt, reading, adapter, check.Token),
                    CheckCardAsync(GeminiCard, ServiceProbe.Gemini, reading, adapter, check.Token));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_serviceCheck, check))
                _serviceCheck = null;
        }
    }

    private async Task CheckCardAsync(ContentControl card, WebService service, Reading reading, NetworkInterface? adapter,
        CancellationToken token)
    {
        var result = await ServiceProbe.CheckAsync(service, reading, adapter, token);
        token.ThrowIfCancellationRequested();
        ShowService(card, service, result);
    }

    private void ShowService(ContentControl card, WebService service, ServiceResult result)
    {
        var color = result.State switch
        {
            ServiceState.Reachable => "Positive",
            ServiceState.Restricted => "Caution",
            ServiceState.Unreachable => "Negative",
            _ => "Waiting"
        };
        var glyph = service == ServiceProbe.ChatGpt
            ? "M2,3 H14 V12 H7 L3,15 V12 H2 Z M5,7 H11"
            : "M8,1 C8,5 11,8 15,8 C11,8 8,11 8,15 C8,11 5,8 1,8 C5,8 8,5 8,1 Z";
        card.Content = new ServiceCardData(service.Name, Geometry.Parse(glyph), result.Status, result.Detail,
            Brush(color), Brush(color + "Fill"));
    }

    private async Task ChangeAsync(bool restore, Profile? profile = null)
    {
        var adapter = SelectedAdapter;
        if (adapter is null || _working)
            return;
        _working = true;
        SetAction(restore ? "Восстанавливаем настройки…" : "Проверяем DNS перед применением…");
        UpdateControls(adapter);
        try
        {
            if (!restore)
            {
                var candidates = profile is null ? DnsProfiles.All : [profile];
                var readings = await Task.WhenAll(candidates.Select(item => DnsProfiles.Check(item, _lifetime.Token, adapter)));
                var selected = readings.Where(reading => reading.Milliseconds.HasValue).MinBy(reading => reading.Milliseconds);
                if (selected is null)
                {
                    SetAction("DNS не ответил. Выберите другой профиль или проверьте подключение.", "Negative");
                    return;
                }
                profile = selected.Profile;
                ProfileList.SelectedIndex = Array.IndexOf(DnsProfiles.All, profile);
                SetAction($"{profile.Name} · подтвердите изменение DNS в окне Windows.");
            }
            else
            {
                SetAction("Подтвердите восстановление DNS в окне Windows.");
            }

            var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            launch.ArgumentList.Add(restore ? "--restore" : "--apply");
            launch.ArgumentList.Add(Guid.Parse(adapter.Id).ToString("D"));
            if (profile is not null)
                launch.ArgumentList.Add(Array.IndexOf(DnsProfiles.All, profile).ToString(CultureInfo.InvariantCulture));

            using var worker = Process.Start(launch) ?? throw new InvalidOperationException("Не удалось открыть окно подтверждения.");
            await worker.WaitForExitAsync(_lifetime.Token);
            if (worker.ExitCode == 0)
                SetAction(restore ? "Исходные настройки DNS восстановлены." : $"Применён профиль {profile!.Name}.", "Positive");
            else
                SetAction("Windows не подтвердила изменение. Причина указана в сообщении DNS-пульта.", "Negative");
            await RefreshAsync();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            SetAction("Изменение отменено в окне Windows.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!_lifetime.IsCancellationRequested)
                SetAction(error.Message, "Negative");
        }
        finally
        {
            _working = false;
            if (!_lifetime.IsCancellationRequested)
                UpdateControls(SelectedAdapter);
        }
    }

    private void UpdateControls(NetworkInterface? adapter)
    {
        AdapterPicker.IsEnabled = !_working && AdapterPicker.Items.Count > 0;
        ProfileList.IsEnabled = !_working;
        ApplyButton.IsEnabled = !_working && adapter is not null && Program.Connected(adapter);
        AutoButton.IsEnabled = ApplyButton.IsEnabled;
        RestoreButton.IsEnabled = !_working && adapter is not null && WindowsDns.HasBackup(Guid.Parse(adapter.Id));
        RefreshButton.IsEnabled = !_working && _refresh is null;
        var addresses = adapter?.GetIPProperties().DnsAddresses;
        SystemDns.Text = addresses is { Count: > 0 } ? string.Join(", ", addresses) : "Нет настроенных адресов";
        if (adapter is null)
            SetAction("Подключите Ethernet или Wi-Fi и нажмите кнопку проверки.");
        else if (ActionStatus.Text.StartsWith("Подключите Ethernet", StringComparison.Ordinal))
            SetAction("");
    }

    private void SetAction(string text, string color = "Muted")
    {
        ActionStatus.Text = text;
        ActionStatus.Foreground = Brush(color);
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void ApplyClicked(object sender, RoutedEventArgs e) => await ChangeAsync(false, (ProfileList.SelectedItem as ProfileRow)?.Profile);
    private async void AutoClicked(object sender, RoutedEventArgs e) => await ChangeAsync(false);
    private async void RestoreClicked(object sender, RoutedEventArgs e) => await ChangeAsync(true);
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            await InspectServicesAsync();
    }
    private async void AdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_loadingAdapters)
        {
            SetAction("");
            await RefreshAsync();
        }
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private static ServiceResult Waiting(string status, string detail) => new(ServiceState.Waiting, status, detail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    private sealed record ServiceCardData(string Name, Geometry Glyph, string Status, string Detail, Brush Tone, Brush Fill);

    private sealed class ProfileRow(Profile profile, Brush tone) : INotifyPropertyChanged
    {
        public Profile Profile { get; } = profile;
        public string Name => Profile.Name;
        public string Host => Profile.Host;
        public string Status { get; private set; } = "Ожидание";
        public Brush Tone { get; private set; } = tone;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Set(string status, Brush brush)
        {
            Status = status;
            Tone = brush;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
            PropertyChanged?.Invoke(this, new(nameof(Tone)));
        }
    }
}
