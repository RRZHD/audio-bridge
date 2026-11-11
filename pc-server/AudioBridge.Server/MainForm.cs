using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AudioBridge.Server.Core;
using NAudio.CoreAudioApi;

namespace AudioBridge.Server;

public sealed class MainForm : Form
{
    private const int DefaultPort = 57120;

    private readonly AudioEngine _engine = new();
    private readonly TcpServerListener _tcp = new();
    private readonly BluetoothServerListener _bt = new();
    private readonly DiscoveryResponder _discovery = new();
    private readonly AoaHost _aoa = new();
    private readonly List<ClientSession> _sessions = new();
    private readonly object _sessionsLock = new();

    private readonly ServerSettings _settings = ServerSettings.Load();
    private List<MMDevice> _renderDevices = new();

    /// <summary>Combo entry meaning "pick for me": a virtual cable if one is installed.</summary>
    private sealed class AutoChoice
    {
        public override string ToString() => "Авто (виртуальный выход, если установлен)";
    }

    private sealed class DefaultChoice
    {
        public override string ToString() => "(устройство вывода по умолчанию)";
    }

    /// <summary>The phone's microphone is received but not played anywhere — the default, so nobody
    /// is surprised by a live microphone in their headphones.</summary>
    private sealed class MicOffChoice
    {
        public override string ToString() => "(не воспроизводить)";
    }

    private const string MicOutputDefault = "default";

    private ComboBox _captureDeviceCombo = null!;
    private ComboBox _micOutputDeviceCombo = null!;
    private Label _deviceHint = null!;
    private Label _firewallWarning = null!;
    private Button _firewallButton = null!;
    private CheckBox _usbCheck = null!;
    private Label _usbStatus = null!;
    private Button _usbDkButton = null!;
    private NumericUpDown _portInput = null!;
    private Button _startStopButton = null!;
    private ListBox _sessionsList = null!;
    private TextBox _logBox = null!;
    private bool _running;

    public MainForm(bool autoStart = false)
    {
        Text = "Audio Bridge Server";
        Width = 760;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        PopulateDevices();

        _engine.Log += AppendLog;
        _tcp.Log += AppendLog;
        _bt.Log += AppendLog;
        _discovery.Log += AppendLog;
        _tcp.ClientAccepted += OnClientAccepted;
        _aoa.Log += AppendLog;
        _aoa.ClientAccepted += OnClientAccepted;
        _aoa.StatusChanged += s => { if (IsHandleCreated) BeginInvoke(() => _usbStatus.Text = s); };
        _bt.ClientAccepted += OnBluetoothClientAccepted;

        FormClosing += (_, _) => StopAll();
        Shown += (_, _) => UpdateFirewallWarning();
        if (autoStart) Shown += (_, _) => StartAll(); // `AudioBridgeServer.exe --autostart`
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        Controls.Add(root);

        // Devices row
        var devicesPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        devicesPanel.Controls.Add(new Label { Text = "Захват звука с устройства:", AutoSize = true, Margin = new Padding(0, 8, 5, 0) });
        _captureDeviceCombo = new ComboBox { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
        devicesPanel.Controls.Add(_captureDeviceCombo);
        devicesPanel.Controls.Add(new Label { Text = "Выход для микрофона телефона:", AutoSize = true, Margin = new Padding(15, 8, 5, 0) });
        _micOutputDeviceCombo = new ComboBox { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
        devicesPanel.Controls.Add(_micOutputDeviceCombo);
        devicesPanel.SetFlowBreak(_micOutputDeviceCombo, true);

        _deviceHint = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(700, 0), Margin = new Padding(0, 8, 0, 0) };
        devicesPanel.Controls.Add(_deviceHint);
        var soundSettings = new Button { Text = "Звук Windows…", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        soundSettings.Click += (_, _) => OpenWindowsSoundSettings();
        devicesPanel.Controls.Add(soundSettings);
        root.Controls.Add(devicesPanel, 0, 0);

        // Port row
        var connPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        connPanel.Controls.Add(new Label { Text = "TCP порт (Wi-Fi, LAN):", AutoSize = true, Margin = new Padding(0, 8, 5, 0) });
        _portInput = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = DefaultPort, Width = 80 };
        connPanel.Controls.Add(_portInput);
        connPanel.SetFlowBreak(_portInput, true);

        _firewallWarning = new Label
        {
            AutoSize = true,
            MaximumSize = new System.Drawing.Size(700, 0),
            ForeColor = System.Drawing.Color.Firebrick,
            Margin = new Padding(0, 8, 0, 0),
            Text = "Брандмауэр Windows блокирует входящие подключения в «общедоступных» сетях — так Windows часто помечает " +
                   "сеть раздачи Wi-Fi, поэтому телефон не сможет найти или открыть сервер (на USB-кабель это не влияет).",
            Visible = false,
        };
        connPanel.Controls.Add(_firewallWarning);
        _firewallButton = new Button { Text = "Разрешить в брандмауэре (нужны права администратора)", AutoSize = true, Visible = false, Margin = new Padding(0, 4, 0, 0) };
        _firewallButton.Click += (_, _) =>
        {
            if (FirewallHelper.AllowThroughFirewallElevated()) AppendLog("Брандмауэр: правило для сервера создано.");
            else AppendLog("Брандмауэр: правило не создано (отказ в UAC или ошибка).");
            UpdateFirewallWarning();
        };
        connPanel.Controls.Add(_firewallButton);

        _usbCheck = new CheckBox
        {
            Text = "USB-кабель: подключать телефон напрямую (без модема и adb)",
            AutoSize = true,
            Checked = _settings.UsbEnabled,
            Margin = new Padding(0, 8, 0, 0),
        };
        _usbCheck.CheckedChanged += (_, _) =>
        {
            _settings.UsbEnabled = _usbCheck.Checked;
            _settings.Save();
            if (_running)
            {
                if (_usbCheck.Checked) _aoa.Start();
                else _aoa.Stop();
            }
            if (!_usbCheck.Checked) _usbStatus.Text = "";
        };
        connPanel.Controls.Add(_usbCheck);
        _usbStatus = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(700, 0), Margin = new Padding(12, 9, 0, 0) };
        connPanel.Controls.Add(_usbStatus);
        _usbDkButton = new Button { Text = "Настроить USB: драйвер UsbDk (один раз, права администратора)", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        _usbDkButton.Click += (_, _) => SetupUsb();
        connPanel.Controls.Add(_usbDkButton);
        _usbDkButton.Visible = !AoaHost.IsUsbDkInstalled() || !AoaHost.AccessoryHideRulesInstalled();
        root.Controls.Add(connPanel, 0, 1);

        // Start/stop row
        var actionPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        _startStopButton = new Button { Text = "Старт", Width = 120, Height = 30 };
        _startStopButton.Click += (_, _) => ToggleRunning();
        actionPanel.Controls.Add(_startStopButton);
        root.Controls.Add(actionPanel, 0, 2);

        // Sessions
        var sessionsGroup = new GroupBox { Text = "Подключённые клиенты", Dock = DockStyle.Fill };
        _sessionsList = new ListBox { Dock = DockStyle.Fill };
        sessionsGroup.Controls.Add(_sessionsList);
        root.Controls.Add(sessionsGroup, 0, 3);

        // Log
        var logGroup = new GroupBox { Text = "Лог", Dock = DockStyle.Fill };
        _logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        logGroup.Controls.Add(_logBox);
        root.Controls.Add(logGroup, 0, 4);
    }


    private void PopulateDevices()
    {
        _renderDevices = AudioEngine.GetRenderDevices();

        _captureDeviceCombo.Items.Add(new AutoChoice());
        _captureDeviceCombo.Items.Add(new DefaultChoice());
        _micOutputDeviceCombo.Items.Add(new MicOffChoice());
        _micOutputDeviceCombo.Items.Add(new DefaultChoice());
        foreach (var d in _renderDevices)
        {
            _captureDeviceCombo.Items.Add(d);
            _micOutputDeviceCombo.Items.Add(d);
        }

        // Restore the last choice (by endpoint ID); otherwise capture = Auto, mic output = default.
        _captureDeviceCombo.SelectedItem = _renderDevices.FirstOrDefault(d => d.ID == _settings.CaptureDeviceId) is { } cap
            ? cap
            : _captureDeviceCombo.Items[0];
        // Mic output: off unless the user chose something ("default" = Windows default output).
        _micOutputDeviceCombo.SelectedItem =
            _settings.MicOutputDeviceId == MicOutputDefault ? _micOutputDeviceCombo.Items[1]
            : _renderDevices.FirstOrDefault(d => d.ID == _settings.MicOutputDeviceId) is { } mic ? mic
            : _micOutputDeviceCombo.Items[0];
        if (_settings.Port is >= 1024 and <= 65535) _portInput.Value = _settings.Port.Value;

        // Virtual outputs get a star so they stand out in the list.
        void FormatDevice(object? _, ListControlConvertEventArgs e)
        {
            if (e.ListItem is MMDevice d) e.Value = (VirtualAudio.IsVirtual(d) ? "★ " : "") + d.FriendlyName;
        }
        _captureDeviceCombo.Format += FormatDevice;
        _micOutputDeviceCombo.Format += FormatDevice;
        _captureDeviceCombo.SelectedIndexChanged += (_, _) => UpdateDeviceHint();
        _micOutputDeviceCombo.SelectedIndexChanged += (_, _) => UpdateDeviceHint();
        UpdateDeviceHint();
    }

    /// <summary>The output that will actually be captured for the current combo selection.</summary>
    private MMDevice? ResolveCaptureDevice() => _captureDeviceCombo.SelectedItem switch
    {
        MMDevice d => d,
        AutoChoice => VirtualAudio.FindBestForAuto(_renderDevices), // null -> Windows default output
        _ => null,
    };

    private void UpdateDeviceHint()
    {
        var device = ResolveCaptureDevice();
        string phones;
        lock (_sessionsLock)
        {
            var names = _sessions.Select(s => s.PhoneName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            phones = names.Count > 0 ? $" Подключён: {string.Join(", ", names)}." : "";
        }

        if (device != null && VirtualAudio.IsVirtual(device))
        {
            _deviceHint.Text =
                $"Звук берётся с виртуального выхода «{device.FriendlyName}». Выбери его выходом в Windows " +
                "(Win+Ctrl+V или Параметры → Звук) — тогда звук пойдёт только на телефон. Чтобы выход назывался " +
                "как телефон, переименуй его: «Звук Windows…» → устройство → Свойства → имя." + phones + MicNote();
        }
        else
        {
            var name = device?.FriendlyName ?? "устройство вывода по умолчанию";
            _deviceHint.Text =
                $"Виртуального выхода нет — копируется звук с «{name}» (он продолжает звучать и на ПК). Чтобы телефон был " +
                "отдельным выходом Windows, установи любой подписанный виртуальный аудиокабель (например, VB-CABLE) — " +
                "сервер подхватит его сам." + phones + MicNote();
        }
    }

    private string MicNote() =>
        _micOutputDeviceCombo.SelectedItem is MicOffChoice
            ? " Микрофон телефона на ПК не воспроизводится. Чтобы использовать его как микрофон в программах: включи " +
              "«Передавать микрофон» в приложении, а здесь выбери виртуальный кабель (CABLE Input) — в программе микрофоном " +
              "будет CABLE Output."
            : "";

    private void UpdateFirewallWarning()
    {
        var blocked = !FirewallHelper.IsPublicInboundAllowed();
        _firewallWarning.Visible = blocked;
        _firewallButton.Visible = blocked;
    }

    /// <summary>
    /// One-time USB setup (needs administrator rights): the UsbDk driver — bundled MSI from the
    /// installer; a loose build falls back to the release page — and its hide rules for the phone's
    /// accessory device.
    /// </summary>
    private void SetupUsb()
    {
        var needDriver = !AoaHost.IsUsbDkInstalled();
        var msi = Directory.EnumerateFiles(AppContext.BaseDirectory, "UsbDk*.msi").FirstOrDefault();
        if (needDriver && msi == null)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/daynix/UsbDk/releases/latest") { UseShellExecute = true }); } catch { }
            AppendLog("UsbDk: скачай UsbDk_*_x64.msi со страницы релизов, установи, затем нажми эту кнопку ещё раз.");
            return;
        }
        AppendLog("USB: настройка (подтверди запрос администратора)…");
        if (AoaHost.SetupElevated(needDriver ? msi : null, AppendLog))
        {
            AppendLog(needDriver
                ? "USB: драйвер UsbDk установлен. Перезагрузи Windows (пункт «Перезагрузка»), затем запусти сервер."
                : "USB: правила UsbDk добавлены. Перезапусти сервер (Стоп → Старт).");
        }
        UpdateUsbSetupButton();
    }

    private void UpdateUsbSetupButton() =>
        _usbDkButton.Visible = !AoaHost.IsUsbDkInstalled() || !AoaHost.AccessoryHideRulesInstalled();

    private static void OpenWindowsSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch
        {
            // Not worth surfacing — the user can open Sound settings themselves.
        }
    }

    private void ToggleRunning()
    {
        if (_running) StopAll();
        else StartAll();
    }

    private void StartAll()
    {
        try
        {
            var captureDevice = ResolveCaptureDevice();
            var micChoice = _micOutputDeviceCombo.SelectedItem;
            var micOutDevice = micChoice as MMDevice;
            var playMic = micChoice is not MicOffChoice;
            var port = (int)_portInput.Value;

            _settings.CaptureDeviceId = (_captureDeviceCombo.SelectedItem as MMDevice)?.ID; // null = Auto/default
            _settings.MicOutputDeviceId = micChoice switch
            {
                MMDevice d => d.ID,
                DefaultChoice => MicOutputDefault,
                _ => null, // off
            };
            _settings.Port = port;
            _settings.Save();

            AppendLog($"Захват звука: {captureDevice?.FriendlyName ?? "устройство вывода по умолчанию"}");

            // Capture/TCP are core — a failure here really should stop the whole start attempt.
            _engine.StartCapture(captureDevice);
            if (playMic) _engine.StartMicPlayback(micOutDevice);
            _tcp.Start(port);

            // Bluetooth and discovery are optional extras — Wi-Fi/USB still work without them, so
            // don't let either one's failure (no Bluetooth radio, a reserved discovery port, etc.)
            // take down a server that's otherwise fine.
            try { _bt.Start(); }
            catch (Exception ex) { AppendLog($"Bluetooth недоступен: {ex.Message}"); }
            _discovery.Start(port); // never throws — see DiscoveryResponder.Start
            if (_usbCheck.Checked) _aoa.Start();

            _running = true;
            _startStopButton.Text = "Стоп";
            _captureDeviceCombo.Enabled = false;
            _micOutputDeviceCombo.Enabled = false;
            _portInput.Enabled = false;
            AppendLog("Сервер запущен.");
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка запуска: {ex.Message}");
            StopAll();
        }
    }

    private void StopAll()
    {
        _tcp.Stop();
        _bt.Stop();
        _discovery.Stop();
        _aoa.Stop();
        lock (_sessionsLock)
        {
            foreach (var s in _sessions.ToArray()) s.Disconnect();
            _sessions.Clear();
        }
        _engine.StopCapture();
        _engine.StopMicPlayback();

        _running = false;
        _startStopButton.Text = "Старт";
        _captureDeviceCombo.Enabled = true;
        _micOutputDeviceCombo.Enabled = true;
        _portInput.Enabled = true;
        RefreshSessionsList();
        AppendLog("Сервер остановлен.");
    }

    private void OnClientAccepted(System.IO.Stream stream, string label)
    {
        // Wi-Fi, LAN and USB-modem links all stay lossless raw (capped by the phone) at the native
        // format — USB is meant to be the highest-quality wired option, not a fallback. (The
        // stutter once blamed on adb bandwidth was actually a phone-side bug: raw decode returned
        // the wrong sample count, so every chunk was followed by a block of zeros.)
        var pcCodec = new RawFloat32PcCodec();
        var micCodec = new RawPcm16MicCodec();
        AddSession(new ClientSession(stream, label, _engine, pcCodec, micCodec,
            _engine.NativeSampleRate, _engine.NativeChannels, useNativeFormat: true));
    }

    private void OnBluetoothClientAccepted(System.IO.Stream stream, string label)
    {
        // Bluetooth RFCOMM — Opus at a fixed 48kHz/stereo (independently resampled by AudioEngine),
        // since raw stereo PCM would not reliably fit and Opus only accepts specific sample rates.
        var pcCodec = new OpusPcCodec(AudioEngine.BtSampleRate, AudioEngine.BtChannels, 320_000);
        var micCodec = new OpusMicCodec(AudioEngine.MicSampleRate, 32_000);
        AddSession(new ClientSession(stream, label, _engine, pcCodec, micCodec,
            AudioEngine.BtSampleRate, AudioEngine.BtChannels, useNativeFormat: false));
    }

    private void AddSession(ClientSession session)
    {
        lock (_sessionsLock) { _sessions.Add(session); }
        session.Log += AppendLog;
        session.Changed += _ => BeginInvoke(RefreshSessionsList); // the phone's name arrives after connect
        session.Disconnected += s =>
        {
            lock (_sessionsLock) { _sessions.Remove(s); }
            BeginInvoke(RefreshSessionsList);
            AppendLog($"Отключён: {s.Label}");
        };
        session.Start();
        BeginInvoke(RefreshSessionsList);
    }

    private void RefreshSessionsList()
    {
        _sessionsList.Items.Clear();
        lock (_sessionsLock)
        {
            foreach (var s in _sessions)
            {
                _sessionsList.Items.Add(string.IsNullOrWhiteSpace(s.PhoneName) ? s.Label : $"{s.PhoneName} — {s.Label}");
            }
        }
        UpdateDeviceHint();
    }


    private void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }
}
