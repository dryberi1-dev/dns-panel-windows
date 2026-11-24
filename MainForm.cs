using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace DnsPanel;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(14, 19, 30);
    private static readonly Color PanelColor = Color.FromArgb(23, 31, 46);
    private static readonly Color TextColor = Color.FromArgb(238, 242, 249);
    private static readonly Color Muted = Color.FromArgb(178, 190, 210);
    private static readonly Color Accent = Color.FromArgb(190, 241, 118);
    private readonly ComboBox _adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 255 };
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 255 };
    private readonly ListView _servers = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, HideSelection = false
    };
    private readonly Button _apply = ButtonFor("Применить", true);
    private readonly Button _auto = ButtonFor("Авто");
    private readonly Button _restore = ButtonFor("Вернуть исходный DNS");
    private readonly Button _refresh = ButtonFor("Проверить");
    private readonly Label _status = LabelFor("", 10, Muted);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private bool _working;
    private bool _checking;
    private int _adapterVersion;

    public MainForm()
    {
        Text = "DNS-пульт для Windows";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(698, 524);
        MinimumSize = new Size(640, 520);
        Font = new Font("Segoe UI", 10F);
        BackColor = Background;
        ForeColor = TextColor;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(22, 16, 22, 15) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 57));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 69));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(layout);
        layout.Controls.Add(LabelFor("DNS-пульт", 25, TextColor), 0, 0);

        foreach (var box in new[] { _adapter, _profile })
        {
            box.BackColor = PanelColor;
            box.ForeColor = TextColor;
            box.FlatStyle = FlatStyle.Flat;
            box.Margin = new Padding(0);
        }

        var selectors = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        selectors.Controls.Add(Picker("Сетевой адаптер", _adapter));
        selectors.Controls.Add(Picker("DNS-профиль", _profile));
        layout.Controls.Add(selectors, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.AddRange([_apply, _auto, _restore]);
        layout.Controls.Add(actions, 0, 2);

        _servers.BackColor = PanelColor;
        _servers.ForeColor = TextColor;
        _servers.Columns.Add("Профиль", 132);
        _servers.Columns.Add("Сервер DNS-over-HTTPS", 249);
        _servers.Columns.Add("Статус", 145);
        _servers.Columns.Add("Отклик", 84);
        _servers.Margin = Padding.Empty;
        layout.Controls.Add(_servers, 0, 3);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 10, 0, 0) };
        footer.Controls.Add(_refresh);
        footer.Controls.Add(LabelFor("Отклик включает соединение TLS и ответ DNS.", 9, Muted));
        layout.Controls.Add(footer, 0, 4);
        _status.Dock = DockStyle.Fill;
        _status.AutoSize = false;
        layout.Controls.Add(_status, 0, 5);

        foreach (var profile in DnsProfiles.All)
        {
            _profile.Items.Add(profile.Name);
            _servers.Items.Add(new ListViewItem([profile.Name, profile.Host, "Ожидание", "—"]));
        }
        _profile.SelectedIndex = 1;

        _servers.SelectedIndexChanged += (_, _) =>
        {
            if (_servers.SelectedIndices.Count > 0)
                _profile.SelectedIndex = _servers.SelectedIndices[0];
        };
        _adapter.SelectedIndexChanged += async (_, _) =>
        {
            _adapterVersion++;
            UpdateControls();
            await CheckAsync();
        };
        _refresh.Click += async (_, _) =>
        {
            LoadAdapters();
            await CheckAsync();
        };
        _timer.Tick += async (_, _) =>
        {
            if (!_working)
            {
                LoadAdapters();
                await CheckAsync();
            }
        };
        _apply.Click += async (_, _) => await ChangeAsync("apply", DnsProfiles.All[_profile.SelectedIndex]);
        _auto.Click += async (_, _) => await ChangeAsync("auto");
        _restore.Click += async (_, _) => await ChangeAsync("restore");
        Shown += async (_, _) =>
        {
            LoadAdapters();
            UpdateControls();
            await CheckAsync();
            _timer.Start();
        };
        FormClosed += (_, _) =>
        {
            _timer.Dispose();
            _lifetime.Cancel();
        };
    }

    private NetworkInterface? SelectedAdapter => (_adapter.SelectedItem as AdapterItem)?.Value;

    private void LoadAdapters()
    {
        var adapters = Program.Adapters().Where(adapter =>
            Program.Connected(adapter) || WindowsDns.HasBackup(Guid.Parse(adapter.Id))).ToArray();
        if (adapters.Select(adapter => adapter.Id)
            .SequenceEqual(_adapter.Items.Cast<AdapterItem>().Select(item => item.Value.Id)))
            return;

        var selected = SelectedAdapter?.Id;
        _adapter.Items.Clear();
        _adapter.Items.AddRange(adapters.Select(adapter => new AdapterItem(adapter)).ToArray());
        if (adapters.Length > 0)
            _adapter.SelectedIndex = Math.Max(0, Array.FindIndex(adapters, item => item.Id == selected));
        UpdateControls();
    }

    private async Task CheckAsync()
    {
        if (_checking)
            return;
        _checking = true;
        _refresh.Enabled = false;
        var adapterVersion = _adapterVersion;
        var adapter = FreshAdapter();
        if (adapter is not null && !Program.Connected(adapter))
            adapter = null;
        try
        {
            foreach (ListViewItem item in _servers.Items)
            {
                item.SubItems[2].Text = "Проверка…";
                item.SubItems[3].Text = "—";
                item.SubItems[2].ForeColor = Muted;
            }

            var jobs = DnsProfiles.All.Select((profile, index) => PaintReading(index, profile, adapter, adapterVersion));
            await Task.WhenAll(jobs);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _checking = false;
            if (!_lifetime.IsCancellationRequested)
            {
                UpdateControls();
                if (adapterVersion != _adapterVersion)
                    await CheckAsync();
            }
        }
    }

    private async Task PaintReading(int index, Profile profile, NetworkInterface? adapter, int adapterVersion)
    {
        var result = adapter is null
            ? new Reading(profile, [], null, "Нет сети")
            : await DnsProfiles.Check(profile, _lifetime.Token, adapter);
        if (_lifetime.IsCancellationRequested || adapterVersion != _adapterVersion)
            return;
        var item = _servers.Items[index];
        item.UseItemStyleForSubItems = false;
        item.SubItems[2].Text = result.Status;
        item.SubItems[2].ForeColor = result.Milliseconds.HasValue ? Accent : Muted;
        item.SubItems[3].Text = result.Milliseconds is long value ? $"{value} мс" : "—";
    }

    private NetworkInterface? FreshAdapter() =>
        Program.Adapters().FirstOrDefault(adapter => adapter.Id == SelectedAdapter?.Id);

    private async Task ChangeAsync(string action, Profile? profile = null)
    {
        var adapter = FreshAdapter();
        if (adapter is null || _working)
            return;

        _working = true;
        _status.Text = action == "restore" ? "Восстанавливаем настройки…" : "Проверяем выбранный DNS…";
        UpdateControls();
        try
        {
            if (action != "restore")
            {
                var candidates = profile is null ? DnsProfiles.All : [profile];
                var readings = await Task.WhenAll(candidates.Select(item => DnsProfiles.Check(item, _lifetime.Token, adapter)));
                var selected = readings.Where(reading => reading.Milliseconds.HasValue)
                    .MinBy(reading => reading.Milliseconds);
                if (selected is null)
                {
                    _status.Text = "Нет ответа на проверочный DNS-запрос. Настройки не изменились.";
                    return;
                }
                profile = selected.Profile;
                _profile.SelectedIndex = Array.IndexOf(DnsProfiles.All, profile);
                _status.Text = $"{profile.Name} · подтвердите изменение DNS в окне Windows.";
            }

            var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            launch.ArgumentList.Add(action == "restore" ? "--restore" : "--apply");
            launch.ArgumentList.Add(Guid.Parse(adapter.Id).ToString("D"));
            if (profile is not null)
                launch.ArgumentList.Add(Array.IndexOf(DnsProfiles.All, profile).ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var worker = Process.Start(launch) ?? throw new InvalidOperationException("Не удалось открыть окно подтверждения.");
            await worker.WaitForExitAsync(_lifetime.Token);
            _status.Text = worker.ExitCode == 0
                ? action == "restore" ? "Исходные настройки DNS восстановлены." : $"Применён профиль {profile!.Name}."
                : "Windows не подтвердила изменение. Подробности в сообщении DNS-пульта.";
            await CheckAsync();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            _status.Text = "Изменение отменено в окне Windows.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!_lifetime.IsCancellationRequested)
                _status.Text = error.Message;
        }
        finally
        {
            _working = false;
            if (!_lifetime.IsCancellationRequested)
                UpdateControls();
        }
    }

    private void UpdateControls()
    {
        var adapter = SelectedAdapter;
        _adapter.Enabled = !_working;
        _apply.Enabled = !_working && FreshAdapter() is { } current && Program.Connected(current);
        _profile.Enabled = _apply.Enabled;
        _auto.Enabled = _apply.Enabled;
        _restore.Enabled = !_working && adapter is not null && WindowsDns.HasBackup(Guid.Parse(adapter.Id));
        _refresh.Enabled = !_working && !_checking;
        if (!_working && adapter is null)
            _status.Text = "Подключите Ethernet или Wi-Fi и нажмите «Проверить».";
        else if (!_working && _status.Text.StartsWith("Подключите", StringComparison.Ordinal))
            _status.Text = "";
    }

    private static Control Picker(string label, ComboBox box)
    {
        var container = new FlowLayoutPanel
        {
            Width = 270, Height = 66, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Margin = Padding.Empty
        };
        container.Controls.Add(LabelFor(label, 9, Muted));
        container.Controls.Add(box);
        return container;
    }

    private static Label LabelFor(string text, float size, Color color) => new()
    {
        AutoSize = true, Text = text, ForeColor = color,
        Font = new Font("Segoe UI", size, size > 20 ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 3, 0, 6)
    };

    private static Button ButtonFor(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(83, 34),
            Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : PanelColor,
            ForeColor = primary ? Background : TextColor
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private sealed record AdapterItem(NetworkInterface Value)
    {
        public override string ToString() => Value.Name;
    }
}
