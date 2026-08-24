using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly List<ClientSession> _sessions = new();
    private readonly object _sessionsLock = new();

    private ComboBox _captureDeviceCombo = null!;
    private ComboBox _micOutputDeviceCombo = null!;
    private NumericUpDown _portInput = null!;
    private TextBox _adbPathInput = null!;
    private Button _startStopButton = null!;
    private Button _adbReverseButton = null!;
    private ListBox _sessionsList = null!;
    private TextBox _logBox = null!;
    private bool _running;

    public MainForm()
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
        _bt.ClientAccepted += OnBluetoothClientAccepted;

        FormClosing += (_, _) => StopAll();
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
        root.Controls.Add(devicesPanel, 0, 0);

        // Port + adb row
        var connPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        connPanel.Controls.Add(new Label { Text = "TCP порт (Wi-Fi + USB):", AutoSize = true, Margin = new Padding(0, 8, 5, 0) });
        _portInput = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = DefaultPort, Width = 80 };
        connPanel.Controls.Add(_portInput);
        connPanel.Controls.Add(new Label { Text = "adb.exe:", AutoSize = true, Margin = new Padding(15, 8, 5, 0) });
        _adbPathInput = new TextBox { Width = 260, Text = FindDefaultAdbPath() };
        connPanel.Controls.Add(_adbPathInput);
        _adbReverseButton = new Button { Text = "adb reverse (USB)", AutoSize = true, Margin = new Padding(10, 3, 0, 0) };
        _adbReverseButton.Click += (_, _) => RunAdbReverse();
        connPanel.Controls.Add(_adbReverseButton);
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

    private static string FindDefaultAdbPath()
    {
        var candidates = new[] { @"F:\platform-tools\adb.exe", "adb.exe", "adb" };
        foreach (var c in candidates)
        {
            if (c is "adb.exe" or "adb") continue; // rely on PATH as fallback, checked last
            if (System.IO.File.Exists(c)) return c;
        }
        return "adb";
    }

    private void PopulateDevices()
    {
        _captureDeviceCombo.Items.Add("(устройство вывода по умолчанию)");
        _micOutputDeviceCombo.Items.Add("(устройство вывода по умолчанию)");
        foreach (var d in AudioEngine.GetRenderDevices())
        {
            _captureDeviceCombo.Items.Add(d);
            _micOutputDeviceCombo.Items.Add(d);
        }
        _captureDeviceCombo.SelectedIndex = 0;
        _micOutputDeviceCombo.SelectedIndex = 0;
        _captureDeviceCombo.Format += (_, e) => e.Value = e.ListItem is MMDevice d ? d.FriendlyName : e.Value;
        _micOutputDeviceCombo.Format += (_, e) => e.Value = e.ListItem is MMDevice d ? d.FriendlyName : e.Value;
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
            var captureDevice = _captureDeviceCombo.SelectedItem as MMDevice;
            var micOutDevice = _micOutputDeviceCombo.SelectedItem as MMDevice;

            _engine.StartCapture(captureDevice);
            _engine.StartMicPlayback(micOutDevice);
            var port = (int)_portInput.Value;
            _tcp.Start(port);
            _bt.Start();
            _discovery.Start(port);

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
        // Wi-Fi and USB (adb reverse) both get here — lossless raw codec, native format.
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
            foreach (var s in _sessions) _sessionsList.Items.Add(s.Label);
        }
    }

    private void RunAdbReverse()
    {
        var port = (int)_portInput.Value;
        var adb = _adbPathInput.Text.Trim();
        try
        {
            var psi = new ProcessStartInfo(adb, $"reverse tcp:{port} tcp:{port}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            AppendLog($"adb reverse tcp:{port} tcp:{port} -> exit {proc.ExitCode}. {stdout} {stderr}".Trim());
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось выполнить adb reverse: {ex.Message}");
        }
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
