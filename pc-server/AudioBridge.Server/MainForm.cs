using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AudioBridge.Server.Core;
using AudioBridge.Server.Ui;
using NAudio.CoreAudioApi;

namespace AudioBridge.Server;

public sealed class MainForm : Form
{
    private const int DefaultPort = 45121;

    private readonly AudioEngine _engine = new();
    private readonly TcpServerListener _tcp = new();
    private readonly BluetoothServerListener _bt = new();
    private readonly BleServerListener _ble = new();
    private readonly DiscoveryResponder _discovery = new();
    private readonly AoaHost _aoa = new();
    private readonly VolumeSync _volume = new();
    private readonly List<ClientSession> _sessions = new();
    private readonly object _sessionsLock = new();

    private readonly ServerSettings _settings = ServerSettings.Load();
    private List<MMDevice> _renderDevices = new();
    private List<MMDevice> _captureEndpoints = new();

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

    // ---- элементы ----------------------------------------------------------------------------
    private SelectBox _captureDeviceCombo = null!;
    private SelectBox _micOutputDeviceCombo = null!;
    private TextLabel _deviceHint = null!;
    private TextLabel _micHint = null!;
    private Card _firewallCard = null!;
    private TextLabel _firewallWarning = null!;
    private ToggleSwitch _usbCheck = null!;
    private TextLabel _usbStatus = null!;
    private ActionButton _usbDkButton = null!;
    private NumberBox _portInput = null!;
    private PowerButton _power = null!;
    private TextLabel _heroTitle = null!;
    private TextLabel _heroSub = null!;
    private Chip _chipTcp = null!, _chipBle = null!, _chipBt = null!, _chipUsb = null!;
    private ClientList _clients = null!;
    private LogView _logBox = null!;
    private readonly List<Pill> _themePills = new();
    private bool _running;

    public MainForm(bool autoStart = false)
    {
        Text = "Audio Bridge Server";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* the default icon is fine */ }
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1080, 740);
        MinimumSize = new Size(940, 640);
        Font = Skin.Font(9.5f);

        Skin.Apply(_settings.Theme);
        BuildUi();
        PopulateDevices();
        ApplyTheme();
        Skin.Changed += ApplyTheme;

        _engine.Log += AppendLog;
        _tcp.Log += AppendLog;
        _bt.Log += AppendLog;
        _ble.Log += AppendLog;
        _discovery.Log += AppendLog;
        _tcp.ClientAccepted += OnClientAccepted;
        _aoa.Log += AppendLog;
        _aoa.ClientAccepted += OnClientAccepted;
        _aoa.StatusChanged += s => { if (IsHandleCreated) BeginInvoke(() => _usbStatus.Text = s); };
        _volume.Changed += (level, muted) =>
        {
            lock (_sessionsLock) { foreach (var s in _sessions) s.SendVolume(level, muted); }
        };
        _bt.ClientAccepted += OnBluetoothClientAccepted;
        _ble.ClientAccepted += OnBluetoothClientAccepted; // LE rides the same Opus path as classic Bluetooth

        FormClosing += (_, _) => StopAll();
        Shown += (_, _) => UpdateFirewallWarning();
        if (autoStart) Shown += (_, _) => StartAll(); // `AudioBridgeServer.exe --autostart`
        UpdateHero();
    }

    // ================================================================================== интерфейс

    private D2DHost _host = null!;
    private MainRoot _root = null!;

    private void BuildUi()
    {
        _root = new MainRoot();
        _host = new D2DHost(_root);
        Controls.Add(_host);

        BuildHeader();
        BuildLeft();
        BuildRight();
    }

    private void BuildHeader()
    {
        // Тема: как в системе / светлая / тёмная — справа.
        foreach (var (mode, label) in new[] { (ThemeMode.Auto, "Как в системе"), (ThemeMode.Light, "Светлая"), (ThemeMode.Dark, "Тёмная") })
        {
            var pill = new Pill(label) { Tag = mode };
            pill.Click += (_, _) => { _settings.Theme = mode; _settings.Save(); Skin.Apply(mode); };
            _themePills.Add(pill);
            _root.Header.AddPill(pill);
        }
    }

    private void BuildLeft()
    {
        var col = _root.Left.Content;
        col.Add(Wrap(BuildHeroCard()));
        col.Add(Wrap(BuildClientsCard()));
    }

    private void BuildRight()
    {
        var col = _root.Right.Content;
        col.Add(Wrap(BuildSoundCard()));
        col.Add(Wrap(BuildConnectCard()));
        col.Add(Wrap(BuildLogCard()));
    }

    private static Card Wrap(Card card)
    {
        card.Margin = new Thickness(0, 0, 0, 14);
        return card;
    }

    private static Card MakeCard(Thickness? padding = null) => new() { Padding = padding ?? new Thickness(18) };

    /// <summary>Добавляет элемент в стопку с отступом сверху.</summary>
    private static T Put<T>(VStack stack, T node, float top = 0) where T : Node
    {
        node.Margin = new Thickness(0, top, 0, 0);
        stack.Add(node);
        return node;
    }

    private Card BuildHeroCard()
    {
        var card = MakeCard(new Thickness(18, 26, 18, 22));
        var stack = card.Add(new VStack());

        _power = Put(stack, new PowerButton(), 0);
        _power.Click += (_, _) => ToggleRunning();
        _power.Margin = new Thickness(0, 0, 0, 14);

        _heroTitle = Put(stack, new TextLabel("Сервер остановлен", LabelRole.Title) { Align = HAlign.Center });

        _heroSub = Put(stack, new TextLabel("", LabelRole.Muted) { Align = HAlign.Center, MaxTextWidth = 320 }, 4);
        _heroSub.Margin = new Thickness(0, 4, 0, 14);

        var chips = Put(stack, new Flow { Align = HAlign.Center });
        _chipTcp = chips.Add(new Chip("Wi-Fi", Tone.Neutral, dot: true));
        _chipBle = chips.Add(new Chip("BT LE", Tone.Neutral, dot: true));
        _chipBt = chips.Add(new Chip("BT", Tone.Neutral, dot: true));
        _chipUsb = chips.Add(new Chip("USB", Tone.Neutral, dot: true));
        return card;
    }

    private Card BuildClientsCard()
    {
        var card = MakeCard();
        var stack = card.Add(new VStack());
        Put(stack, new TextLabel("Подключённые телефоны", LabelRole.Heading));
        _clients = Put(stack, new ClientList(), 12);
        return card;
    }

    private Card BuildSoundCard()
    {
        var card = MakeCard();
        var stack = card.Add(new VStack());

        Put(stack, new TextLabel("Звук ПК → телефон", LabelRole.Heading));
        Put(stack, new TextLabel("Устройство вывода Windows, с которого сервер берёт звук", LabelRole.Muted) { Wrap = true }, 2);
        _captureDeviceCombo = Put(stack, new SelectBox(), 10);
        _deviceHint = Put(stack, new TextLabel("", LabelRole.Muted) { Wrap = true }, 8);
        var sound = Put(stack, new ActionButton("Звук Windows…", BtnKind.Ghost, 32), 10);
        sound.Click += (_, _) => OpenWindowsSoundSettings();

        Put(stack, new Divider(), 16);
        Put(stack, new TextLabel("Микрофон телефона → ПК", LabelRole.Heading), 16);
        Put(stack, new TextLabel("Как использовать голос с телефона: как микрофон для Discord и других программ или просто слышать на ПК", LabelRole.Muted) { Wrap = true }, 2);
        _micOutputDeviceCombo = Put(stack, new SelectBox(), 10);
        _micHint = Put(stack, new TextLabel("", LabelRole.Muted) { Wrap = true }, 8);
        return card;
    }

    private Card BuildConnectCard()
    {
        var card = MakeCard();
        var stack = card.Add(new VStack());

        Put(stack, new TextLabel("Подключения", LabelRole.Heading));

        // Порт: подпись и пояснение слева, поле справа.
        var portText = new VStack();
        Put(portText, new TextLabel("TCP-порт", LabelRole.Text));
        Put(portText, new TextLabel("Для Wi-Fi и локальной сети; телефон находит его сам", LabelRole.Muted) { Wrap = true }, 2);
        _portInput = new NumberBox { Value = DefaultPort };
        Put(stack, new SplitRow(portText, _portInput), 12);

        Put(stack, new Divider(), 14);

        // USB: подпись и статус слева, переключатель справа.
        var usbText = new VStack();
        Put(usbText, new TextLabel("USB-кабель", LabelRole.Text));
        _usbStatus = Put(usbText, new TextLabel("Подключать телефон напрямую, без модема и adb", LabelRole.Muted) { Wrap = true }, 2);
        _usbCheck = new ToggleSwitch { Checked = _settings.UsbEnabled };
        _usbCheck.CheckedChanged += (_, _) =>
        {
            _settings.UsbEnabled = _usbCheck.Checked;
            _settings.Save();
            if (_running)
            {
                if (_usbCheck.Checked) _aoa.Start();
                else _aoa.Stop();
            }
            if (!_usbCheck.Checked) _usbStatus.Text = "Подключать телефон напрямую, без модема и adb";
            UpdateHero();
        };
        Put(stack, new SplitRow(usbText, _usbCheck), 14);

        _usbDkButton = Put(stack, new ActionButton("Настроить USB: драйвер UsbDk (один раз, нужны права администратора)", BtnKind.Ghost, 32), 10);
        _usbDkButton.Click += (_, _) => SetupUsb();
        _usbDkButton.Visible = !AoaHost.IsUsbDkInstalled() || !AoaHost.AccessoryHideRulesInstalled();

        // Брандмауэр.
        _firewallCard = new Card { Tone = CardTone.Danger, Padding = new Thickness(14), Visible = false };
        var fw = _firewallCard.Add(new VStack());
        _firewallWarning = Put(fw, new TextLabel(
            "Брандмауэр Windows блокирует входящие подключения в «общедоступных» сетях — так Windows часто помечает " +
            "сеть раздачи Wi-Fi, поэтому телефон не сможет найти или открыть сервер (на USB-кабель это не влияет).", LabelRole.Danger)
        { Wrap = true });
        var fwButton = Put(fw, new ActionButton("Разрешить в брандмауэре (нужны права администратора)", BtnKind.Primary, 32), 10);
        fwButton.Click += (_, _) =>
        {
            if (FirewallHelper.AllowThroughFirewallElevated()) AppendLog("Брандмауэр: правило для сервера создано.");
            else AppendLog("Брандмауэр: правило не создано (отказ в UAC или ошибка).");
            UpdateFirewallWarning();
        };
        Put(stack, _firewallCard, 14);
        return card;
    }

    private Card BuildLogCard()
    {
        var card = MakeCard();
        var stack = card.Add(new VStack());

        var buttons = new HRow();
        var copy = buttons.Add(new ActionButton("Копировать", BtnKind.Ghost, 28));
        copy.Click += (_, _) =>
        {
            try { if (_logBox.AllText.Length > 0) Clipboard.SetText(_logBox.AllText); } catch { /* буфер занят */ }
        };
        var clear = buttons.Add(new ActionButton("Очистить", BtnKind.Ghost, 28));
        clear.Click += (_, _) => _logBox.Clear();
        Put(stack, new SplitRow(new TextLabel("Лог", LabelRole.Heading), buttons));

        _logBox = Put(stack, new LogView(230), 10);
        return card;
    }

    // ---- тема ----------------------------------------------------------------------------------

    private void ApplyTheme()
    {
        var p = Skin.P;
        BackColor = p.Bg;
        ForeColor = p.Text;
        foreach (var pill in _themePills) pill.Selected = (ThemeMode)pill.Tag! == Skin.Mode;
        if (IsHandleCreated) Skin.StyleWindow(this);
        _host?.Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Skin.StyleWindow(this);
    }

    private void UpdateHero()
    {
        int count;
        string phones;
        lock (_sessionsLock)
        {
            count = _sessions.Count;
            phones = string.Join(", ", _sessions.Select(s => string.IsNullOrWhiteSpace(s.PhoneName) ? s.Label : s.PhoneName));
        }

        if (!_running)
        {
            _power.State = PowerMode.Off;
            _heroTitle.Text = "Сервер остановлен";
            _heroSub.Text = "Нажми кнопку, чтобы принимать звук с ПК на телефоне";
        }
        else if (count == 0)
        {
            _power.State = PowerMode.Busy;
            _heroTitle.Text = "Жду телефон";
            _heroSub.Text = "Подключи телефон: Wi-Fi, USB-кабель, Bluetooth LE или Bluetooth";
        }
        else
        {
            _power.State = PowerMode.On;
            _heroTitle.Text = "Звук идёт на телефон";
            _heroSub.Text = phones;
        }

        var on = _running;
        _chipTcp.Text = on ? $"Wi-Fi :{_tcp.Port}" : "Wi-Fi";
        _chipTcp.Tone = on ? Tone.Success : Tone.Neutral;
        _chipBle.Tone = on ? Tone.Success : Tone.Neutral;
        _chipBt.Tone = on ? Tone.Success : Tone.Neutral;
        _chipUsb.Tone = on && _usbCheck.Checked ? Tone.Success : Tone.Neutral;
    }

    private void RefreshClientList()
    {
        List<(string, string)> rows;
        lock (_sessionsLock)
        {
            rows = _sessions.Select(s => (string.IsNullOrWhiteSpace(s.PhoneName) ? s.Label : s.PhoneName!, s.Label)).ToList();
        }
        _clients.SetClients(rows);
    }

    // ================================================================================== логика

    private void PopulateDevices()
    {
        // Voicemeeter registers ~15 endpoints; only its main input is useful here.
        _renderDevices = AudioEngine.GetRenderDevices().Where(d => !VoicemeeterRemote.IsClutter(d.FriendlyName)).ToList();

        // Virtual outputs get a star so they stand out in the list.
        string FormatDevice(object o) => o is MMDevice d ? (VirtualAudio.IsVirtual(d) ? "★ " : "") + d.FriendlyName : o.ToString() ?? "";
        _captureDeviceCombo.Format = FormatDevice;
        _captureEndpoints = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        _micOutputDeviceCombo.Format = FormatMicChoice;

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
        // 57120 was the old default; some reboots put it inside a range Windows reserves, so it is retired.
        if (_settings.Port is >= 1024 and <= 65535 and not 57120) _portInput.Value = _settings.Port.Value;

        _captureDeviceCombo.SelectedIndexChanged += (_, _) => UpdateDeviceHint();
        // The microphone output can be switched at any time, also while the server is running.
        _micOutputDeviceCombo.SelectedIndexChanged += (_, _) => { ApplyMicOutput(); UpdateDeviceHint(); };
        var micTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        var micWasActive = false;
        micTimer.Tick += (_, _) =>
        {
            _micHint.Text = MicNote();
            bool active;
            lock (_sessionsLock) { active = _sessions.Any(s => s.MicActive); }
            if (active != micWasActive)
            {
                micWasActive = active;
                AppendLog(active ? "Микрофон телефона: принимается." : "Микрофон телефона: не приходит.");
            }
        };
        micTimer.Start();
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
        _captureDeviceCombo.InvalidateText();
        _micOutputDeviceCombo.InvalidateText();
        var device = ResolveCaptureDevice();
        if (device != null && VirtualAudio.IsVirtual(device))
        {
            _deviceHint.Text =
                $"Звук берётся с виртуального выхода «{device.FriendlyName}». Выбери его выходом в Windows " +
                "(Win+Ctrl+V или Параметры → Звук) — тогда звук пойдёт только на телефон. Чтобы выход назывался " +
                "как телефон, переименуй его: «Звук Windows…» → устройство → Свойства → имя.";
        }
        else
        {
            var name = device?.FriendlyName ?? "устройство вывода по умолчанию";
            _deviceHint.Text =
                $"Виртуального выхода нет — копируется звук с «{name}» (он продолжает звучать и на ПК). Чтобы телефон был " +
                "отдельным выходом Windows, установи любой подписанный виртуальный аудиокабель (например, VB-CABLE) — " +
                "сервер подхватит его сам.";
        }
        _micHint.Text = MicNote();
    }

    /// <summary>The microphone (as a program such as Discord sees it) that a virtual output turns into, if any.</summary>
    private string? MicNameFor(MMDevice render) =>
        VirtualAudio.IsCable(render) ? VirtualAudio.FindPartnerCapture(render, _captureEndpoints)?.FriendlyName : null;

    /// <summary>List entries are described by what the user gets: a microphone for programs, or sound on a device.</summary>
    private string FormatMicChoice(object o)
    {
        switch (o)
        {
            case MicOffChoice:
                return "Не использовать микрофон телефона";
            case DefaultChoice:
                return "Слышать на ПК: устройство вывода по умолчанию";
            case MMDevice d when VirtualAudio.IsCable(d):
                var busy = ResolveCaptureDevice()?.ID == d.ID ? " (занят звуком для телефона)" : "";
                return $"Микрофон для программ: «{MicNameFor(d) ?? d.FriendlyName}»{busy}";
            case MMDevice d:
                return $"Слышать на ПК: {d.FriendlyName}";
            default:
                return o.ToString() ?? "";
        }
    }

    /// <summary>If the voice goes into Voicemeeter, set its routing (and start it) in the background.</summary>
    private void PrepareMicRoute(object? choice)
    {
        if (choice is not MMDevice d || !VoicemeeterRemote.IsMainInput(d.FriendlyName)) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            var msg = VoicemeeterRemote.ConfigureMicRoute();
            if (msg != null) BeginInvoke(() => AppendLog(msg));
        });
    }

    /// <summary>Applies the chosen microphone output right away: restarts playback if the server is running.</summary>
    private void ApplyMicOutput()
    {
        var choice = _micOutputDeviceCombo.SelectedItem;
        _settings.MicOutputDeviceId = choice switch
        {
            MMDevice d => d.ID,
            DefaultChoice => MicOutputDefault,
            _ => null, // off
        };
        _settings.Save();
        if (!_running) return;
        PrepareMicRoute(choice);
        try
        {
            _engine.StopMicPlayback();
            if (choice is not MicOffChoice) _engine.StartMicPlayback(choice as MMDevice);
            AppendLog(choice is MicOffChoice
                ? "Микрофон телефона: воспроизведение выключено."
                : $"Микрофон телефона → {(choice as MMDevice)?.FriendlyName ?? "устройство вывода по умолчанию"}");
        }
        catch (Exception ex)
        {
            AppendLog($"Микрофон: не удалось открыть выход — {ex.Message}");
        }
    }

    /// <summary>What the user needs to know about the microphone right now — in terms of the microphone they pick in Discord.</summary>
    private string MicNote()
    {
        bool receiving;
        lock (_sessionsLock) { receiving = _sessions.Any(s => s.MicActive); }
        var choice = _micOutputDeviceCombo.SelectedItem;
        var capture = ResolveCaptureDevice();
        var phone = receiving ? "Телефон присылает голос. " : "Включи «Микрофон телефона → ПК» в приложении на телефоне. ";

        if (choice is MicOffChoice)
        {
            var hasSecondCable = _renderDevices.Any(d => VirtualAudio.IsCable(d) && d.ID != capture?.ID);
            return receiving
                ? "Телефон присылает микрофон, но он никуда не идёт: выбери выше, как его использовать."
                : hasSecondCable
                    ? "Выбери выше «Микрофон для программ» — и в Discord появится микрофон с голосом телефона."
                    : "Чтобы телефон был микрофоном в Discord, нужен ещё один виртуальный кабель, отдельный от того, что играет звук " +
                      "на телефон (подойдёт «Steam Streaming Microphone», он ставится вместе со Steam). Пока его нет — можно только слышать голос на ПК (выбери устройство вывода).";
        }

        if (choice is MMDevice d && VirtualAudio.IsCable(d))
        {
            if (capture?.ID == d.ID)
                return "Этот кабель уже занят: по нему звук идёт на телефон. Голос вернулся бы на телефон. Нужен второй виртуальный кабель.";
            var mic = MicNameFor(d) ?? d.FriendlyName;
            return $"{phone}В Discord: Настройки → Голос и видео → Устройство ввода → выбери «{mic}». " +
                   "В других программах так же — выбери этот микрофон.";
        }

        var target = (choice as MMDevice)?.FriendlyName ?? "устройство вывода по умолчанию";
        return $"{phone}Голос играет на «{target}» — это для прослушивания, не микрофон. Для Discord выбери «Микрофон для программ».";
    }

    private void UpdateFirewallWarning()
    {
        _firewallCard.Visible = !FirewallHelper.IsPublicInboundAllowed();
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
            var port = _portInput.Value;

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
            // Voicemeeter (if running) may hold the cable as its own output; free it before capturing.
            if (captureDevice != null && VirtualAudio.IsVirtual(captureDevice) && VoicemeeterRemote.ConfigureMicRoute(routeMic: false) is { } vmNote)
                AppendLog(vmNote);
            _engine.StartCapture(captureDevice);
            _volume.Attach(captureDevice ?? new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console));
            if (playMic) { PrepareMicRoute(micChoice); _engine.StartMicPlayback(micOutDevice); }
            port = StartTcpWithFallback(port);
            _portInput.Value = port;
            _settings.Port = port;
            _settings.Save();

            // Bluetooth and discovery are optional extras — Wi-Fi/USB still work without them, so
            // don't let either one's failure (no Bluetooth radio, a reserved discovery port, etc.)
            // take down a server that's otherwise fine.
            try { _bt.Start(); }
            catch (Exception ex) { AppendLog($"Bluetooth недоступен: {ex.Message}"); }
            try { _ble.Start(); }
            catch (Exception ex) { AppendLog($"Bluetooth LE недоступен: {ex.Message}"); }
            _discovery.Start(port); // never throws — see DiscoveryResponder.Start
            if (_usbCheck.Checked) _aoa.Start();

            _running = true;
            _captureDeviceCombo.Enabled = false;
            _portInput.Enabled = false;
            UpdateHero();
            AppendLog("Сервер запущен.");
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка запуска: {ex.Message}");
            StopAll();
        }
    }

    /// <summary>
    /// Starts the TCP listener; if the chosen port is reserved by Windows (Hyper-V/WSL exclusion
    /// ranges give "access to a socket was denied") or taken, falls through 45121–45140. The port in
    /// use is announced by the UDP discovery, so phones find it either way.
    /// </summary>
    private int StartTcpWithFallback(int port)
    {
        System.Net.Sockets.SocketException? last = null;
        foreach (var candidate in new[] { port }.Concat(Enumerable.Range(DefaultPort, 20)).Distinct())
        {
            try
            {
                _tcp.Start(candidate);
                if (candidate != port) AppendLog($"Порт {port} недоступен ({last?.Message}) — использую {candidate}. Телефон найдёт его через автопоиск.");
                return candidate;
            }
            catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode is System.Net.Sockets.SocketError.AccessDenied or System.Net.Sockets.SocketError.AddressAlreadyInUse)
            {
                last = ex;
            }
        }
        if (last != null) throw last;
        throw new InvalidOperationException("нет свободного порта");
    }

    private void StopAll()
    {
        _tcp.Stop();
        _bt.Stop();
        _ble.Stop();
        _discovery.Stop();
        _aoa.Stop();
        lock (_sessionsLock)
        {
            foreach (var s in _sessions.ToArray()) s.Disconnect();
            _sessions.Clear();
        }
        _volume.Detach();
        _engine.StopCapture();
        _engine.StopMicPlayback();

        _running = false;
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
        session.VolumeReceived += _volume.SetFromPhone;
        session.MediaCommand += cmd =>
        {
            var what = MediaKeys.Press(cmd);
            if (what != null) AppendLog($"Кнопка на наушниках телефона: {what}");
        };
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
        RefreshClientList();
        UpdateHero();
        UpdateDeviceHint();
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }
        _logBox.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    }

    // ---- снимок окна для проверки внешнего вида (--shot) ------------------------------------------

    /// <summary>Заполняет окно примерами и сохраняет картинку; сервер при этом не запускается.</summary>
    internal static int ShotHeight = 960; // taller than the real window by default, so every card is visible

    /// <summary>`--scrolltest <file.txt>`: scrolls every column programmatically and writes the time per step.</summary>
    internal void ScrollTest(string path)
    {
        FillDemo();
        ClientSize = new Size(1080, 640);
        for (var i = 0; i < 6; i++) Application.DoEvents();
        _host.Immediate = true;
        var sb = new System.Text.StringBuilder();
        foreach (var col in new[] { _root.Left, _root.Right })
        {
            sb.AppendLine($"column {col.W:F0}x{col.H:F0}");
            var times = new List<double>();
            for (var i = 0; i < 60; i++)
            {
                var sw = Stopwatch.StartNew();
                col.OnWheel(i < 30 ? -120 : 120);   // как шаг колеса мыши
                for (var f = 0; f < 6; f++) _host.Render(); // кадры анимации инерции
                times.Add(sw.Elapsed.TotalMilliseconds / 6);
            }
            sb.AppendLine($"  frame ms: avg={times.Average():F2} max={times.Max():F2}");
        }
        File.WriteAllText(path, sb.ToString());
    }

    internal void RenderDemo(string path)
    {
        FillDemo();
        var popup = Environment.GetEnvironmentVariable("AB_SHOT") ?? "";
        ClientSize = new Size(1080, ShotHeight);
        for (var i = 0; i < 6; i++) Application.DoEvents();
        _host.Render();
        if (popup.Contains("mic")) _micOutputDeviceCombo.OnClick();
        if (popup.Contains("port")) _host.SetFocus(_portInput, true);
        if (popup.Contains("scroll"))
        {
            _root.Right.OnWheel(-3000);
            for (var i = 0; i < 80; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(16); }
        }
        Application.DoEvents();
        using var bmp = _host.Snapshot();
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void FillDemo()
    {
        _running = true;
        _clients.SetClients(new List<(string, string)> { ("Ruslan Xperia", "Bluetooth LE (Ruslan Xperia)") });
        _heroSub.Text = "Ruslan Xperia";
        _power.State = PowerMode.On;
        _heroTitle.Text = "Звук идёт на телефон";
        foreach (var c in new[] { _chipTcp, _chipBle, _chipBt, _chipUsb }) c.Tone = Tone.Success;
        _logBox.Text =
            "[09:41:02] Захват звука: Ruslan Xperia (VB-Audio Virtual Cable)\r\n" +
            "[09:41:02] TCP listening on 0.0.0.0:45121 (Wi-Fi, LAN)\r\n" +
            "[09:41:03] Bluetooth LE: ищу телефон (приложение → режим «BT LE» → «Подключиться»)\r\n" +
            "[09:41:11] Bluetooth LE: подключён к «Ruslan Xperia», MTU 517\r\n";
        _firewallCard.Visible = true;
    }
}

