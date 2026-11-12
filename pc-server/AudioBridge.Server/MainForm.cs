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
    private ThemedLabel _deviceHint = null!;
    private ThemedLabel _micHint = null!;
    private Card _firewallCard = null!;
    private ThemedLabel _firewallWarning = null!;
    private ToggleSwitch _usbCheck = null!;
    private ThemedLabel _usbStatus = null!;
    private ActionButton _usbDkButton = null!;
    private NumberBox _portInput = null!;
    private PowerButton _power = null!;
    private ThemedLabel _heroTitle = null!;
    private ThemedLabel _heroSub = null!;
    private Chip _chipTcp = null!, _chipBle = null!, _chipBt = null!, _chipUsb = null!;
    private ClientList _clients = null!;
    private TextBox _logBox = null!;
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

        // The first layout passes (wrapping, auto-sizing) must not be visible: show the window only once it is settled.
        Opacity = 0;
        Shown += (_, _) => BeginInvoke(new Action(() =>
        {
            for (var i = 0; i < 4; i++) Application.DoEvents();
            Opacity = 1;
        }));

        FormClosing += (_, _) => StopAll();
        Shown += (_, _) => UpdateFirewallWarning();
        if (autoStart) Shown += (_, _) => StartAll(); // `AudioBridgeServer.exe --autostart`
        UpdateHero();
    }

    // ================================================================================== интерфейс

    private void BuildUi()
    {
        SuspendLayout();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty,
            Padding = new Padding(20, 0, 20, 20),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 396));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 1);

        body.Controls.Add(BuildLeft(), 0, 0);
        body.Controls.Add(BuildRight(), 1, 0);

        ResumeLayout(true);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 0) };

        var logo = new LogoMark { Location = new Point(20, 18), Size = new Size(40, 40) };
        header.Controls.Add(logo);

        var title = new ThemedLabel("Audio Bridge Server", LabelRole.Heading) { Location = new Point(72, 16) };
        var sub = new ThemedLabel("Звук с ПК на телефон — Wi-Fi, USB, Bluetooth", LabelRole.Muted) { Location = new Point(73, 40) };
        header.Controls.Add(title);
        header.Controls.Add(sub);

        // Тема: как в системе / светлая / тёмная — справа.
        var themes = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.Transparent, Margin = Padding.Empty,
        };
        foreach (var (mode, label) in new[] { (ThemeMode.Auto, "Как в системе"), (ThemeMode.Light, "Светлая"), (ThemeMode.Dark, "Тёмная") })
        {
            var pill = new Pill(label) { Tag = mode, Margin = new Padding(0, 0, 6, 0) };
            pill.Click += (_, _) => { _settings.Theme = mode; _settings.Save(); Skin.Apply(mode); };
            _themePills.Add(pill);
            themes.Controls.Add(pill);
        }
        header.Controls.Add(themes);
        header.Resize += (_, _) => themes.Location = new Point(header.Width - themes.Width - 20, 20);
        themes.SizeChanged += (_, _) => themes.Location = new Point(header.Width - themes.Width - 20, 20);
        return header;
    }

    private Control BuildLeft()
    {
        var host = ScrollColumn(out var column);
        column.Controls.Add(Wrap(BuildHeroCard()));
        column.Controls.Add(Wrap(BuildClientsCard()));
        return host;
    }

    private Control BuildRight()
    {
        var host = ScrollColumn(out var column);
        column.Controls.Add(Wrap(BuildSoundCard()));
        column.Controls.Add(Wrap(BuildConnectCard()));
        column.Controls.Add(Wrap(BuildLogCard()));
        return host;
    }

    /// <summary>Колонка карточек с прокруткой тонкой полоской (без системного скроллбара).</summary>
    private static ScrollHost ScrollColumn(out VStack column)
    {
        var host = new ScrollHost { Dock = DockStyle.Fill, Margin = Padding.Empty };
        column = new VStack { Dock = DockStyle.None };
        host.SetContent(column);
        return host;
    }

    private static Control Wrap(Card card)
    {
        card.Margin = new Padding(0, 0, 0, 14);
        card.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        return card;
    }

    /// <summary>Карточка с вертикальным содержимым: контролы укладываются сверху вниз, ширина по карточке.</summary>
    private static Card MakeCard(Padding? padding = null)
    {
        var card = new Card { Padding = padding ?? new Padding(18) };
        return card;
    }

    private static VStack Stack() => new();

    private static void Add(VStack stack, Control c, int top = 0)
    {
        c.Margin = new Padding(0, top, 0, 0);
        c.Dock = DockStyle.None;
        if (c.Anchor != AnchorStyles.None && (c.Anchor & AnchorStyles.Right) == 0 && c is not ActionButton && c is not ToggleSwitch)
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        stack.Controls.Add(c);
    }

    private static ThemedLabel Wrapped(string text, LabelRole role)
    {
        var l = new ThemedLabel(text, role) { AutoSize = true };
        return l;
    }

    private Card BuildHeroCard()
    {
        var card = MakeCard(new Padding(18, 26, 18, 22));
        var stack = Stack();
        card.Controls.Add(stack);

        _power = new PowerButton { Anchor = AnchorStyles.None };
        _power.Click += (_, _) => ToggleRunning();
        _power.Margin = new Padding(0, 0, 0, 14);
        stack.Controls.Add(_power);

        _heroTitle = new ThemedLabel("Сервер остановлен", LabelRole.Title) { Anchor = AnchorStyles.None };
        stack.Controls.Add(_heroTitle);

        _heroSub = new ThemedLabel("", LabelRole.Muted) { Anchor = AnchorStyles.None, Wrap = false, MaximumSize = new Size(320, 0) };
        _heroSub.Margin = new Padding(0, 4, 0, 14);
        stack.Controls.Add(_heroSub);

        var chips = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.None, BackColor = Color.Transparent, MaximumSize = new Size(340, 0),
        };
        _chipTcp = new Chip("Wi-Fi", Tone.Neutral, dot: true) { Margin = new Padding(3) };
        _chipBle = new Chip("BT LE", Tone.Neutral, dot: true) { Margin = new Padding(3) };
        _chipBt = new Chip("BT", Tone.Neutral, dot: true) { Margin = new Padding(3) };
        _chipUsb = new Chip("USB", Tone.Neutral, dot: true) { Margin = new Padding(3) };
        chips.Controls.AddRange(new Control[] { _chipTcp, _chipBle, _chipBt, _chipUsb });
        stack.Controls.Add(chips);
        return card;
    }

    private Card BuildClientsCard()
    {
        var card = MakeCard();
        var stack = Stack();
        card.Controls.Add(stack);
        Add(stack, new ThemedLabel("Подключённые телефоны", LabelRole.Heading));
        _clients = new ClientList { Dock = DockStyle.Fill };
        Add(stack, _clients, 12);
        return card;
    }

    private Card BuildSoundCard()
    {
        var card = MakeCard();
        var stack = Stack();
        card.Controls.Add(stack);

        Add(stack, new ThemedLabel("Звук ПК → телефон", LabelRole.Heading));
        Add(stack, new ThemedLabel("Устройство вывода Windows, с которого сервер берёт звук", LabelRole.Muted) { Wrap = true }, 2);
        _captureDeviceCombo = new SelectBox();
        Add(stack, _captureDeviceCombo, 10);
        _deviceHint = new ThemedLabel("", LabelRole.Muted) { Wrap = true };
        Add(stack, _deviceHint, 8);
        var sound = new ActionButton("Звук Windows…", BtnKind.Ghost, 32) { Anchor = AnchorStyles.Left };
        sound.Click += (_, _) => OpenWindowsSoundSettings();
        Add(stack, sound, 10);
        sound.Dock = DockStyle.None; sound.Anchor = AnchorStyles.Left;

        Add(stack, new Divider(), 16);
        Add(stack, new ThemedLabel("Микрофон телефона → ПК", LabelRole.Heading), 16);
        Add(stack, new ThemedLabel("Как использовать голос с телефона: как микрофон для Discord и других программ или просто слышать на ПК", LabelRole.Muted) { Wrap = true }, 2);
        _micOutputDeviceCombo = new SelectBox();
        Add(stack, _micOutputDeviceCombo, 10);
        _micHint = new ThemedLabel("", LabelRole.Muted) { Wrap = true };
        Add(stack, _micHint, 8);
        return card;
    }

    private Card BuildConnectCard()
    {
        var card = MakeCard();
        var stack = Stack();
        card.Controls.Add(stack);

        Add(stack, new ThemedLabel("Подключения", LabelRole.Heading));

        // Порт: подпись и пояснение слева, поле справа.
        var portText = Stack();
        Add(portText, new ThemedLabel("TCP-порт", LabelRole.Text));
        Add(portText, new ThemedLabel("Для Wi-Fi и локальной сети; телефон находит его сам", LabelRole.Muted) { Wrap = true }, 2);
        _portInput = new NumberBox { Value = DefaultPort };
        Add(stack, new SplitRow(portText, _portInput), 12);

        Add(stack, new Divider(), 14);

        // USB: подпись и статус слева, переключатель справа.
        var usbText = Stack();
        Add(usbText, new ThemedLabel("USB-кабель", LabelRole.Text));
        _usbStatus = new ThemedLabel("Подключать телефон напрямую, без модема и adb", LabelRole.Muted) { Wrap = true };
        Add(usbText, _usbStatus, 2);
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
        var usbRow = new SplitRow(usbText, _usbCheck);
        Add(stack, usbRow, 14);

        _usbDkButton = new ActionButton("Настроить USB: драйвер UsbDk (один раз, нужны права администратора)", BtnKind.Ghost, 32) { Anchor = AnchorStyles.Left };
        _usbDkButton.FitText();
        _usbDkButton.Click += (_, _) => SetupUsb();
        _usbDkButton.Visible = !AoaHost.IsUsbDkInstalled() || !AoaHost.AccessoryHideRulesInstalled();
        Add(stack, _usbDkButton, 10);
        _usbDkButton.Dock = DockStyle.None; _usbDkButton.Anchor = AnchorStyles.Left;

        // Брандмауэр.
        _firewallCard = new Card { Tone = CardTone.Danger, Padding = new Padding(14), Visible = false };
        var fw = Stack();
        _firewallCard.Controls.Add(fw);
        _firewallWarning = new ThemedLabel(
            "Брандмауэр Windows блокирует входящие подключения в «общедоступных» сетях — так Windows часто помечает " +
            "сеть раздачи Wi-Fi, поэтому телефон не сможет найти или открыть сервер (на USB-кабель это не влияет).", LabelRole.Danger)
        { Wrap = true };
        Add(fw, _firewallWarning);
        var fwButton = new ActionButton("Разрешить в брандмауэре (нужны права администратора)", BtnKind.Primary, 32) { Anchor = AnchorStyles.Left };
        fwButton.FitText();
        fwButton.Click += (_, _) =>
        {
            if (FirewallHelper.AllowThroughFirewallElevated()) AppendLog("Брандмауэр: правило для сервера создано.");
            else AppendLog("Брандмауэр: правило не создано (отказ в UAC или ошибка).");
            UpdateFirewallWarning();
        };
        Add(fw, fwButton, 10);
        fwButton.Dock = DockStyle.None; fwButton.Anchor = AnchorStyles.Left;
        Add(stack, _firewallCard, 14);
        return card;
    }

    private Card BuildLogCard()
    {
        var card = MakeCard();
        var stack = Stack();
        card.Controls.Add(stack);

        var head = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = Padding.Empty };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.Controls.Add(new ThemedLabel("Лог", LabelRole.Heading), 0, 0);
        var clear = new ActionButton("Очистить", BtnKind.Ghost, 28) { Anchor = AnchorStyles.Right };
        clear.FitText();
        clear.Click += (_, _) => _logBox.Clear();
        head.Controls.Add(clear, 1, 0);
        Add(stack, head);

        _logBox = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None,
            Height = 230, Font = Skin.Mono(9f), WordWrap = true,
        };
        Add(stack, _logBox, 10);
        _logBox.Dock = DockStyle.Fill;
        _logBox.HandleCreated += (_, _) => Skin.StyleScroll(_logBox);
        return card;
    }

    // ---- тема ----------------------------------------------------------------------------------

    private void ApplyTheme()
    {
        var p = Skin.P;
        BackColor = p.Bg;
        ForeColor = p.Text;
        if (_logBox != null)
        {
            _logBox.BackColor = p.Surface;
            _logBox.ForeColor = p.Text;
            if (_logBox.IsHandleCreated) Skin.StyleScroll(_logBox);
        }
        foreach (var pill in _themePills) pill.Selected = (ThemeMode)pill.Tag! == Skin.Mode;
        if (IsHandleCreated) Skin.StyleWindow(this);
        Invalidate(true);
    }

    /// <summary>Композитная отрисовка окна: дети рисуются во внеэкранный буфер целиком, без мерцания и «проявления» текста.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Skin.StyleWindow(this);
    }

    // ---- «герой»: состояние сервера -------------------------------------------------------------

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
        foreach (var c in new[] { _chipTcp, _chipBle, _chipBt, _chipUsb }) c.Parent?.PerformLayout();
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
                      "на телефон (бесплатный Voicemeeter подойдёт: сервер сам настроит его маршрут). Пока его нет — можно только слышать голос на ПК (выбери устройство вывода).";
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
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }

    // ---- снимок окна для проверки внешнего вида (--shot) ------------------------------------------

    /// <summary>Заполняет окно примерами и сохраняет картинку; сервер при этом не запускается.</summary>
    internal static int ShotHeight = 960; // taller than the real window by default, so every card is visible

    internal void RenderDemo(string path)
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
        ClientSize = new Size(1080, ShotHeight);
        PerformLayout();
        for (var i = 0; i < 4; i++) Application.DoEvents();
        Application.DoEvents();
        Invalidate(true);
        Application.DoEvents();
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}

/// <summary>Логотип: скруглённый квадрат акцентом и четыре полоски эквалайзера.</summary>
internal sealed class LogoMark : SkinControl
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Skin.P;
        using (var path = Skin.Round(new RectangleF(0, 0, Width - 1, Height - 1), 9))
        using (var b = new SolidBrush(p.Accent))
            g.FillPath(b, path);
        using var bar = new SolidBrush(Color.White);
        var cx = Width / 2f;
        var heights = new[] { 0.28f, 0.55f, 0.4f, 0.22f };
        var xs = new[] { -9f, -3f, 3f, 9f };
        for (var i = 0; i < 4; i++)
        {
            var h = Height * heights[i];
            using var rp = Skin.Round(new RectangleF(cx + xs[i] - 1.5f, (Height - h) / 2f, 3f, h), 1.5f);
            g.FillPath(bar, rp);
        }
    }
}

/// <summary>Тонкая линия между блоками карточки.</summary>
internal sealed class Divider : SkinControl
{
    public Divider() { Height = 1; }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(Skin.P.Line);
        e.Graphics.FillRectangle(b, 0, 0, Width, 1);
    }
}
