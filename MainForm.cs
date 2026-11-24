using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DnsPanel;

internal partial class MainForm : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly ObservableCollection<ProfileRow> _rows;
    private readonly ICollectionView _profileView;
    private readonly (ContentControl Card, WebService Service, ToggleButton Filter)[] _serviceCards;
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _serviceCheck;
    private CancellationTokenSource? _filterCheck;
    private ServiceScan? _scan;
    private bool _ready;
    private bool _working;
    private bool _loadingAdapters;
    private bool _changingFilter;
    private ThemeChoice _theme;
    private bool? _dark;
    private HwndSource? _windowSource;

    private static readonly ThemeOption[] Themes =
    [
        new(ThemeChoice.System, "Как в системе"),
        new(ThemeChoice.Light, "Светлая"),
        new(ThemeChoice.Dark, "Тёмная")
    ];

    public MainForm()
    {
        InitializeComponent();
        _theme = Appearance.ReadChoice();
        ThemePicker.ItemsSource = Themes;
        ThemePicker.SelectedItem = Themes.Single(option => option.Choice == _theme);
        UpdateTheme();
        _rows = new ObservableCollection<ProfileRow>(DnsProfiles.All.Select(profile => new ProfileRow(profile)));
        _profileView = CollectionViewSource.GetDefaultView(_rows);
        ProfileList.ItemsSource = _profileView;
        ProfileList.SelectedItem = _rows[1];
        _serviceCards =
        [
            (ChatCard, ServiceProbe.ChatGpt, ChatFilter),
            (GeminiCard, ServiceProbe.Gemini, GeminiFilter),
            (ClaudeCard, ServiceProbe.Claude, ClaudeFilter)
        ];
        foreach (var (card, service, _) in _serviceCards)
            ShowService(card, service, Waiting("Ожидание", "Проверка начнётся после запуска."));
        SourceInitialized += (_, _) =>
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(ThemeMessage);
            UpdateCaption();
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
            if (_theme == ThemeChoice.System)
                UpdateTheme();
            if (!_working && _refresh is null && _filterCheck is null)
                await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_windowSource is { IsDisposed: false })
                _windowSource.RemoveHook(ThemeMessage);
            _lifetime.Cancel();
            _scan?.Dispose();
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
        _scan?.Dispose();
        _scan = connected ? new ServiceScan(adapter!, _lifetime.Token) : null;
        UpdateControls(adapter);

        try
        {
            foreach (var row in _rows)
                row.Set(connected ? "Проверка…" : "Нет сети", ServiceState.Waiting);
            var reorder = _scan is { } scan ? RefreshOrderAsync(scan.Readings, refresh.Token) : Task.CompletedTask;
            await Task.WhenAll(reorder, InspectServicesAsync(), RefreshFiltersAsync());
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

    private async Task RefreshOrderAsync(IReadOnlyDictionary<Profile, Task<Reading>> readings, CancellationToken token)
    {
        await Task.WhenAll(_rows.Select(row => PaintAsync(readings[row.Profile], row, token)));
        token.ThrowIfCancellationRequested();
        var sorted = _rows.OrderBy(row => !row.Milliseconds.HasValue).ThenBy(row => row.Milliseconds).ToArray();
        for (var index = 0; index < sorted.Length; index++)
        {
            var oldIndex = _rows.IndexOf(sorted[index]);
            if (oldIndex != index)
                _rows.Move(oldIndex, index);
        }
    }

    private async Task PaintAsync(Task<Reading> pending, ProfileRow row, CancellationToken token)
    {
        var reading = await pending;
        token.ThrowIfCancellationRequested();
        if (reading.Milliseconds is long milliseconds)
            row.Set($"{milliseconds} мс", ServiceState.Reachable, milliseconds);
        else
            row.Set(reading.Status, ServiceState.Unreachable);
    }

    private async Task RefreshFiltersAsync()
    {
        _filterCheck?.Cancel();
        var services = _serviceCards.Where(item => item.Filter.IsChecked == true).Select(item => item.Service).ToArray();
        if (services.Length == 0)
        {
            _filterCheck = null;
            FilterStatus.Visibility = Visibility.Collapsed;
            var changed = ShowMatches(null);
            UpdateControls(SelectedAdapter);
            if (changed)
                await InspectServicesAsync();
            return;
        }

        using var filter = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _filterCheck = filter;
        FilterStatus.Visibility = Visibility.Visible;
        FilterStatus.Text = "Проверяем выбранные сервисы…";
        UpdateControls(SelectedAdapter);

        var selectionChanged = false;
        try
        {
            var matches = new HashSet<Profile>();
            if (_scan is { } scan)
            {
                var checks = _rows.Select(async row =>
                {
                    var results = await Task.WhenAll(services.Select(service => scan.CheckAsync(row.Profile, service)));
                    return (row.Profile, Matches: results.All(result => result.State == ServiceState.Reachable));
                }).ToArray();
                var results = await Task.WhenAll(checks).WaitAsync(filter.Token);
                filter.Token.ThrowIfCancellationRequested();
                matches.UnionWith(results.Where(result => result.Matches).Select(result => result.Profile));
                FilterStatus.Text = matches.Count == 0
                    ? "Ни один DNS не прошёл все проверки."
                    : $"Подходит {matches.Count} из {_rows.Count}";
            }
            else
            {
                FilterStatus.Text = "Подключитесь к сети для проверки.";
            }
            selectionChanged = ShowMatches(matches);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_filterCheck, filter))
            {
                _filterCheck = null;
                if (!_lifetime.IsCancellationRequested)
                    UpdateControls(SelectedAdapter);
            }
        }

        if (selectionChanged && !_lifetime.IsCancellationRequested)
            await InspectServicesAsync();
    }

    private bool ShowMatches(HashSet<Profile>? matches)
    {
        if (matches is null && _profileView.Filter is null)
            return false;

        var previous = ProfileList.SelectedItem as ProfileRow;
        _changingFilter = true;
        try
        {
            _profileView.Filter = matches is null ? null : item => item is ProfileRow row && matches.Contains(row.Profile);
            ProfileList.SelectedItem = previous is not null && _profileView.Contains(previous)
                ? previous : _profileView.Cast<ProfileRow>().FirstOrDefault();
        }
        finally
        {
            _changingFilter = false;
        }
        return !ReferenceEquals(previous, ProfileList.SelectedItem);
    }

    private async Task InspectServicesAsync()
    {
        _serviceCheck?.Cancel();
        var row = ProfileList.SelectedItem as ProfileRow;
        ServiceCards.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.Visibility = ServiceCards.Visibility;
        if (row is null)
        {
            ProfileTitle.Text = "Нет подходящих DNS";
            ProfileHost.Text = "Снимите один из фильтров или повторите проверку.";
            return;
        }

        ProfileTitle.Text = row.Name;
        ProfileHost.Text = row.Host;
        using var check = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _serviceCheck = check;
        var scan = _scan;
        var status = scan is null
            ? Waiting("Нет сети", "Подключите сетевой адаптер для проверки.")
            : Waiting("Проверка…", $"Проверяем сайт через {row.Name}.");
        foreach (var (card, service, _) in _serviceCards)
            ShowService(card, service, status);

        try
        {
            if (scan is not null)
            {
                await Task.Delay(120, check.Token);
                await Task.WhenAll(_serviceCards.Select(item =>
                    CheckCardAsync(item.Card, item.Service, scan, row.Profile, check.Token)));
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

    private async Task CheckCardAsync(ContentControl card, WebService service, ServiceScan scan, Profile profile, CancellationToken token)
    {
        var result = await scan.CheckAsync(profile, service).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        ShowService(card, service, result);
    }

    private void ShowService(ContentControl card, WebService service, ServiceResult result)
    {
        var glyph = service.Name switch
        {
            "ChatGPT" => "M2,3 H14 V12 H7 L3,15 V12 H2 Z M5,7 H11",
            "Gemini" => "M8,1 C8,5 11,8 15,8 C11,8 8,11 8,15 C8,11 5,8 1,8 C5,8 8,5 8,1 Z",
            _ => "M8,1 V15 M1,8 H15 M3,3 L13,13 M3,13 L13,3"
        };
        card.Content = new ServiceCardData(service.Name, Geometry.Parse(glyph), result.Status, result.Detail, result.State);
    }

    private async Task ChangeAsync(Profile? profile)
    {
        var adapter = SelectedAdapter;
        if (adapter is null || _working)
            return;
        var restore = profile is null;
        _working = true;
        SetAction(restore ? "Восстанавливаем настройки…" : "Проверяем DNS перед применением…");
        UpdateControls(adapter);
        try
        {
            if (profile is not null)
            {
                var reading = await DnsProfiles.Check(profile, _lifetime.Token, adapter);
                if (!reading.Milliseconds.HasValue)
                {
                    SetAction("DNS не ответил. Выберите другой профиль или проверьте подключение.", "Negative");
                    return;
                }
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
        foreach (var (_, _, filter) in _serviceCards)
            filter.IsEnabled = !_working;
        ApplyButton.IsEnabled = !_working && _filterCheck is null && ProfileList.SelectedItem is ProfileRow &&
            adapter is not null && Program.Connected(adapter);
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
        ActionStatus.SetResourceReference(TextBlock.ForegroundProperty, color);
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is ProfileRow row)
            await ChangeAsync(row.Profile);
    }
    private async void RestoreClicked(object sender, RoutedEventArgs e) => await ChangeAsync(null);
    private async void ServiceFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_ready)
            await RefreshFiltersAsync();
    }
    private async void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_changingFilter)
        {
            UpdateControls(SelectedAdapter);
            await InspectServicesAsync();
        }
    }
    private async void AdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_loadingAdapters)
        {
            SetAction("");
            await RefreshAsync();
        }
    }

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not ThemeOption option || option.Choice == _theme)
            return;
        _theme = option.Choice;
        UpdateTheme();
        if (!Appearance.TrySave(_theme))
            SetAction("Тема применена. Windows не разрешила сохранить выбор.", "Negative");
    }

    private void UpdateTheme()
    {
        var dark = Appearance.IsDark(_theme);
        if (_dark == dark)
            return;
        _dark = dark;
        Appearance.SetColors(Resources, dark);
        UpdateCaption();
    }

    private IntPtr ThemeMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int settingsChanged = 0x001A;
        const int themeChanged = 0x031A;
        if (_theme == ThemeChoice.System && message is settingsChanged or themeChanged)
            UpdateTheme();
        return IntPtr.Zero;
    }

    private void UpdateCaption()
    {
        if (_windowSource is not { IsDisposed: false } source)
            return;
        var dark = _dark == true ? 1 : 0;
        var caption = ToColorRef(((SolidColorBrush)Resources["Canvas"]).Color);
        var ink = ToColorRef(((SolidColorBrush)Resources["Ink"]).Color);
        DwmSetWindowAttribute(source.Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(source.Handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(source.Handle, 36, ref ink, sizeof(int));
    }

    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    private static ServiceResult Waiting(string status, string detail) => new(ServiceState.Waiting, status, detail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    private sealed record ThemeOption(ThemeChoice Choice, string Name);

    private sealed record ServiceCardData(string Name, Geometry Glyph, string Status, string Detail, ServiceState State);

    private sealed class ProfileRow(Profile profile) : INotifyPropertyChanged
    {
        public Profile Profile { get; } = profile;
        public string Name => Profile.Name;
        public string Host => Profile.Host;
        public string Status { get; private set; } = "Ожидание";
        public ServiceState State { get; private set; } = ServiceState.Waiting;
        public long? Milliseconds { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Set(string status, ServiceState state, long? milliseconds = null)
        {
            Status = status;
            State = state;
            Milliseconds = milliseconds;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
            PropertyChanged?.Invoke(this, new(nameof(State)));
        }
    }
}
