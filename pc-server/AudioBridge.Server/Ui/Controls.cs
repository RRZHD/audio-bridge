using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace AudioBridge.Server.Ui;

/// <summary>Основа элементов: двойная буферизация, перерисовка при смене темы, сглаживание.</summary>
public abstract class SkinControl : Control
{
    protected SkinControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Skin.Changed += OnSkinChanged;
    }

    protected virtual void OnSkinChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Skin.Changed -= OnSkinChanged;
        base.Dispose(disposing);
    }

    protected float S => Skin.Scale(this);

    protected static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }
}

public enum LabelRole { Text, Muted, Accent, Title, Heading, Small, Danger }

/// <summary>Надпись, берущая цвет и шрифт из темы.</summary>
public sealed class ThemedLabel : Label
{
    private LabelRole _role;

    public ThemedLabel(string text = "", LabelRole role = LabelRole.Text)
    {
        AutoSize = true;
        BackColor = Color.Transparent;
        Text = text;
        _role = role;
        Skin.Changed += Restyle;
        Restyle();
    }

    public LabelRole Role
    {
        get => _role;
        set { _role = value; Restyle(); }
    }

    /// <summary>Переносить текст по ширине, которую дал родитель (подсказки в карточках).</summary>
    public bool Wrap
    {
        get => _wrap;
        // AutoSize stays on: TableLayoutPanel only asks GetPreferredSize of auto-sized controls.
        // VStack gives the width and asks GetPreferredSize for the height; AutoSize would pin the width to 100 px.
        set { _wrap = value; AutoSize = !value; Parent?.PerformLayout(); }
    }

    private bool _wrap;

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!_wrap) return base.GetPreferredSize(proposedSize);
        var w = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue / 2 ? proposedSize.Width : Math.Max(Width, 100);
        if (string.IsNullOrEmpty(Text)) return new Size(w, 0);
        var size = TextRenderer.MeasureText(Text, Font, new Size(w, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
        return new Size(w, size.Height + 2);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (_wrap) { for (var p = Parent; p != null; p = p.Parent) p.PerformLayout(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_wrap) { base.OnPaint(e); return; }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
    }

    private void Restyle()
    {
        var p = Skin.P;
        switch (_role)
        {
            case LabelRole.Title: ForeColor = p.Text; Font = Skin.Font(15f, FontStyle.Bold); break;
            case LabelRole.Heading: ForeColor = p.Text; Font = Skin.Font(11.5f, FontStyle.Bold); break;
            case LabelRole.Muted: ForeColor = p.Muted; Font = Skin.Font(9f); break;
            case LabelRole.Small: ForeColor = p.Muted; Font = Skin.Font(8.5f); break;
            case LabelRole.Accent: ForeColor = p.Accent; Font = Skin.Font(9.5f, FontStyle.Bold); break;
            case LabelRole.Danger: ForeColor = p.Ink(p.Danger); Font = Skin.Font(9.5f); break;
            default: ForeColor = p.Text; Font = Skin.Font(9.5f); break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Skin.Changed -= Restyle;
        base.Dispose(disposing);
    }
}

public enum CardTone { Neutral, Danger, Accent }

/// <summary>Карточка консоли: поверхность, рамка 1 px, скругление 12.</summary>
public sealed class Card : Panel
{
    public CardTone Tone { get; set; } = CardTone.Neutral;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Padding = new Padding(16);
        Skin.Changed += Invalidate;
    }

    private void Invalidate() => base.Invalidate(true);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var w = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue / 2 ? proposedSize.Width : Math.Max(Width, 200);
        var child = Controls.Count > 0 ? Controls[0] : null;
        if (child == null) return new Size(w, Padding.Vertical);
        return new Size(w, child.GetPreferredSize(new Size(w - Padding.Horizontal, 0)).Height + Padding.Vertical);
    }

    /// <summary>Высота карточки — по содержимому при текущей ширине (сама ширина задаётся колонкой).</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var child = Controls.Count > 0 ? Controls[0] : null;
        if (child == null || Width < 40) return;
        var inner = Width - Padding.Horizontal;
        var h = child.GetPreferredSize(new Size(inner, 0)).Height + Padding.Vertical;
        if (h != Height) Height = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Skin.P;
        var (fill, border) = Tone switch
        {
            CardTone.Danger => (p.Soft(p.Danger), Skin.Blend(p.Danger, p.Surface, 0.45f)),
            CardTone.Accent => (p.AccentSoft, p.Accent),
            _ => (p.Surface, p.Border),
        };
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Skin.Round(r, Skin.Radius * Skin.Scale(this));
        using var b = new SolidBrush(fill);
        using var pen = new Pen(border, 1f);
        g.FillPath(b, path);
        g.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Skin.Changed -= Invalidate;
        base.Dispose(disposing);
    }
}

public enum BtnKind { Primary, Ghost, Danger }

public sealed class ActionButton : SkinControl
{
    private bool _hover, _down;

    public BtnKind Kind { get; set; } = BtnKind.Primary;

    public ActionButton(string text, BtnKind kind = BtnKind.Primary, int height = 36)
    {
        Text = text;
        Kind = kind;
        Height = height;
        Cursor = Cursors.Hand;
        Font = Skin.Font(9.5f, FontStyle.Bold);
        Width = TextRenderer.MeasureText(text, Font).Width + 36;
    }

    public void FitText()
    {
        Width = (int)(TextRenderer.MeasureText(Text, Font).Width + 36 * S);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Smooth(g);
        var p = Skin.P;
        Color bg, fg, border;
        switch (Kind)
        {
            case BtnKind.Primary: bg = _hover ? p.AccentHover : p.Accent; fg = Color.White; border = bg; break;
            case BtnKind.Danger: bg = p.Danger; fg = Color.White; border = bg; break;
            default: bg = _hover ? p.AccentSoft : p.Surface; fg = _hover ? p.Accent : p.Text; border = _hover ? p.Accent : p.Border; break;
        }
        if (!Enabled) { bg = Skin.Blend(bg, p.Bg, 0.5f); fg = Skin.Blend(fg, p.Bg, 0.5f); }
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Skin.Round(r, Skin.RadiusSm * S);
        using var b = new SolidBrush(bg);
        using var pen = new Pen(border, 1f);
        g.FillPath(b, path);
        g.DrawPath(pen, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>Пилюля-фильтр (mgr-pill): выбранная — подсветка и рамка акцентом.</summary>
public sealed class Pill : SkinControl
{
    private bool _hover;
    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public Pill(string text)
    {
        Text = text;
        Height = 32;
        Cursor = Cursors.Hand;
        Font = Skin.Font(9.5f);
        Width = TextRenderer.MeasureText(text, Font).Width + 30;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Smooth(g);
        var p = Skin.P;
        var bg = _selected ? p.AccentSoft : p.Surface;
        var fg = _selected ? p.Accent : p.Text;
        var border = _selected ? p.Accent : (_hover ? Skin.Blend(p.Accent, p.Border, 0.55f) : p.Border);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Skin.Round(r, Height / 2f);
        using var b = new SolidBrush(bg);
        using var pen = new Pen(border, 1f);
        g.FillPath(b, path);
        g.DrawPath(pen, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>Переключатель консоли: 38×22, трек — акцент, бегунок — поверхность.</summary>
public sealed class ToggleSwitch : SkinControl
{
    private bool _checked;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size = new Size(40, 24);
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        if (Enabled) Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Smooth(g);
        var p = Skin.P;
        var track = _checked ? p.Accent : p.Border;
        if (!Enabled) track = Skin.Blend(track, p.Bg, 0.5f);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using (var path = Skin.Round(r, r.Height / 2))
        using (var b = new SolidBrush(track))
            g.FillPath(b, path);
        var d = r.Height - 6;
        var x = _checked ? r.Right - d - 3 : r.X + 3;
        using var thumb = new SolidBrush(p.Surface);
        g.FillEllipse(thumb, x, r.Y + 3, d, d);
    }
}

public enum Tone { Neutral, Success, Warning, Danger, Info, Accent }

/// <summary>Плашка состояния (mgr-chip): капсула с мягкой подложкой тона и, по желанию, точкой.</summary>
public sealed class Chip : SkinControl
{
    private Tone _tone;
    private string _text = "";

    public bool Dot { get; set; }

    public Tone Tone
    {
        get => _tone;
        set { _tone = value; Invalidate(); }
    }

    public new string Text
    {
        get => _text;
        set { _text = value; Fit(); Invalidate(); }
    }

    public Chip(string text = "", Tone tone = Tone.Neutral, bool dot = false)
    {
        Font = Skin.Font(8.5f);
        Height = 22;
        Dot = dot;
        _tone = tone;
        Text = text;
    }

    private void Fit()
    {
        var w = TextRenderer.MeasureText(_text, Font).Width + 18 + (Dot ? 12 : 0);
        Width = Math.Max(24, w);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Smooth(g);
        var p = Skin.P;
        var (bg, fg, dot) = _tone switch
        {
            Tone.Success => (p.Soft(p.Success), p.Ink(p.Success), p.Success),
            Tone.Warning => (p.Soft(p.Warning), p.Ink(p.Warning), p.Warning),
            Tone.Danger => (p.Soft(p.Danger), p.Ink(p.Danger), p.Danger),
            Tone.Info => (p.Soft(p.Info), p.Ink(p.Info), p.Info),
            Tone.Accent => (p.AccentSoft, p.Accent, p.Accent),
            _ => (p.ChipBg, p.Muted, p.Muted),
        };
        using (var path = Skin.Round(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, path);
        var textRect = ClientRectangle;
        if (Dot)
        {
            using var db = new SolidBrush(dot);
            g.FillEllipse(db, 9, Height / 2f - 3.5f, 7, 7);
            textRect = new Rectangle(14, 0, Width - 14, Height);
        }
        TextRenderer.DrawText(g, _text, Font, textRect, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

public enum PowerMode { Off, Busy, On }

/// <summary>Большая круглая кнопка «старт/стоп»: пульсирует, пока сервер ждёт телефон, и светится, когда звук идёт.</summary>
public sealed class PowerButton : SkinControl
{
    private PowerMode _state;
    private bool _hover;
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 40 };
    private float _phase;

    public PowerMode State
    {
        get => _state;
        set
        {
            _state = value;
            if (value == PowerMode.Busy) _pulse.Start(); else { _pulse.Stop(); _phase = 0; }
            Invalidate();
        }
    }

    public PowerButton()
    {
        Size = new Size(150, 150);
        Cursor = Cursors.Hand;
        _pulse.Tick += (_, _) => { _phase = (_phase + 0.03f) % 1f; Invalidate(); };
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pulse.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Smooth(g);
        var p = Skin.P;
        var cx = Width / 2f;
        var cy = Height / 2f;
        var inner = Math.Min(Width, Height) * 0.72f;

        var fill = _state switch { PowerMode.On => p.Accent, PowerMode.Busy => p.AccentSoft, _ => p.Bg };
        var ring = _state switch
        {
            PowerMode.On => Skin.Blend(p.Accent, p.Surface, 0.25f),
            PowerMode.Busy => Skin.Blend(p.Accent, p.Surface, 0.55f),
            _ => _hover ? Skin.Blend(p.Accent, p.Border, 0.5f) : p.Border,
        };
        var icon = _state switch { PowerMode.On => Color.White, PowerMode.Busy => p.Accent, _ => _hover ? p.Accent : p.Muted };

        if (_state == PowerMode.Busy)
        {
            var rr = inner / 2 * (1f + _phase * 0.3f);
            using var pulse = new Pen(Color.FromArgb((int)(255 * (1 - _phase)), p.Accent), 2f);
            g.DrawEllipse(pulse, cx - rr, cy - rr, rr * 2, rr * 2);
        }
        if (_state == PowerMode.On)
        {
            foreach (var (k, a) in new[] { (1.28f, 0.08f), (1.14f, 0.12f) })
            {
                var rr = inner / 2 * k;
                using var halo = new SolidBrush(Color.FromArgb((int)(255 * a), p.Accent));
                g.FillEllipse(halo, cx - rr, cy - rr, rr * 2, rr * 2);
            }
        }
        using (var b = new SolidBrush(fill))
            g.FillEllipse(b, cx - inner / 2, cy - inner / 2, inner, inner);
        using (var pen = new Pen(ring, 2f))
            g.DrawEllipse(pen, cx - inner / 2, cy - inner / 2, inner, inner);

        // Значок питания: дуга и вертикальная черта.
        var ir = inner * 0.2f;
        using var ip = new Pen(icon, Math.Max(3f, inner * 0.045f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(ip, cx - ir, cy - ir + 2, ir * 2, ir * 2, -55, 290);
        g.DrawLine(ip, cx, cy - ir - 4, cx, cy - 2);
    }
}

/// <summary>Выпадающий выбор в стиле консоли: поле 40 px со стрелкой и тематическим списком.</summary>
public sealed class SelectBox : SkinControl
{
    private readonly List<object> _items = new();
    private int _selected = -1;
    private bool _hover;

    public IList<object> Items => _items;
    public Func<object, string> Format { get; set; } = o => o.ToString() ?? "";

    public event EventHandler? SelectedIndexChanged;

    public SelectBox()
    {
        Height = 40;
        Cursor = Cursors.Hand;
        Font = Skin.Font(9.5f);
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem
    {
        get => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;
        set => SelectedIndex = value == null ? -1 : _items.IndexOf(value);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled || _items.Count == 0) return;
        var menu = new ContextMenuStrip
        {
            Renderer = new SkinRenderer(),
            BackColor = Skin.P.Surface,
            ForeColor = Skin.P.Text,
            Font = Font,
            ShowImageMargin = false,
            MinimumSize = new Size(Width, 0),
        };
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem((i == _selected ? "✓  " : "     ") + Format(_items[i]))
            {
                BackColor = Skin.P.Surface,
                ForeColor = i == _selected ? Skin.P.Accent : Skin.P.Text,
                Padding = new Padding(4, 6, 4, 6),
            };
            item.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(item);
        }
        menu.Show(this, new Point(0, Height + 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Smooth(g);
        var p = Skin.P;
        var border = _hover && Enabled ? Skin.Blend(p.Accent, p.Border, 0.55f) : p.Border;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Skin.Round(r, Skin.RadiusSm * S))
        using (var b = new SolidBrush(p.Surface))
        using (var pen = new Pen(border, 1f))
        {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
        var fg = Enabled ? p.Text : Skin.Blend(p.Text, p.Surface, 0.5f);
        var text = SelectedItem == null ? "" : Format(SelectedItem);
        TextRenderer.DrawText(g, text, Font, new Rectangle(12, 0, Width - 40, Height), fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        // Стрелка.
        using var ap = new Pen(p.Muted, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var ax = Width - 22f;
        var ay = Height / 2f;
        g.DrawLines(ap, new[] { new PointF(ax - 4, ay - 2), new PointF(ax, ay + 2), new PointF(ax + 4, ay - 2) });
    }
}

/// <summary>Вид выпадающего списка: тема, подсветка строки акцентом.</summary>
internal sealed class SkinRenderer : ToolStripProfessionalRenderer
{
    public SkinRenderer() : base(new SkinColors()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Skin.P.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Skin.P.Border);
        e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1));
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var b = new SolidBrush(e.Item.Selected ? Skin.P.AccentSoft : Skin.P.Surface);
        e.Graphics.FillRectangle(b, rect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? Skin.P.Accent : e.Item.ForeColor;
        base.OnRenderItemText(e);
    }
}

internal sealed class SkinColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Skin.P.Surface;
    public override Color MenuBorder => Skin.P.Border;
    public override Color ImageMarginGradientBegin => Skin.P.Surface;
    public override Color ImageMarginGradientMiddle => Skin.P.Surface;
    public override Color ImageMarginGradientEnd => Skin.P.Surface;
}

/// <summary>Числовое поле (порт): рамка консоли с кольцом фокуса, внутри обычный TextBox без рамки.</summary>
public sealed class NumberBox : SkinControl
{
    private readonly TextBox _box = new() { BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Left };
    private bool _focused;

    public int Minimum { get; set; } = 1024;
    public int Maximum { get; set; } = 65535;

    public int Value
    {
        get => int.TryParse(_box.Text, out var v) ? Math.Clamp(v, Minimum, Maximum) : Minimum;
        set => _box.Text = Math.Clamp(value, Minimum, Maximum).ToString();
    }

    public NumberBox()
    {
        Height = 40;
        Width = 110;
        Controls.Add(_box);
        _box.Font = Skin.Font(10f);
        _box.MaxLength = 5;
        _box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        _box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _box.LostFocus += (_, _) => { _focused = false; Value = Value; Invalidate(); };
        Restyle();
    }

    protected override void OnSkinChanged() { Restyle(); base.OnSkinChanged(); }

    private void Restyle()
    {
        _box.BackColor = Skin.P.Surface;
        _box.ForeColor = Skin.P.Text;
        PositionBox();
    }

    private void PositionBox()
    {
        _box.Location = new Point(12, (Height - _box.Height) / 2);
        _box.Width = Math.Max(10, Width - 24);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); PositionBox(); }
    protected override void OnEnabledChanged(EventArgs e) { _box.Enabled = Enabled; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Smooth(g);
        var p = Skin.P;
        var r = new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f);
        if (_focused)
        {
            using var ring = new Pen(Color.FromArgb(46, p.Accent), 3f);
            using var rp = Skin.Round(new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f), Skin.RadiusSm * S + 1);
            g.DrawPath(ring, rp);
        }
        using var path = Skin.Round(r, Skin.RadiusSm * S);
        using var b = new SolidBrush(p.Surface);
        using var pen = new Pen(_focused ? p.Accent : p.Border, 1f);
        g.FillPath(b, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>Список подключённых телефонов: строки с точкой, именем и плашкой способа связи.</summary>
public sealed class ClientList : Panel
{
    private readonly ThemedLabel _empty = new("Телефон пока не подключён", LabelRole.Muted);

    public ClientList()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.Transparent;
        Controls.Add(_empty);
        _empty.Location = new Point(0, 4);
    }

    public void SetClients(IReadOnlyList<(string Name, string Link)> clients)
    {
        SuspendLayout();
        foreach (var c in Controls.OfType<ClientRow>().ToList()) { Controls.Remove(c); c.Dispose(); }
        _empty.Visible = clients.Count == 0;
        var y = 0;
        foreach (var (name, link) in clients)
        {
            var shortLink = link.Contains(" (") ? link[..link.IndexOf(" (", StringComparison.Ordinal)] : link;
            var row = new ClientRow(name, shortLink) { Location = new Point(0, y), Width = Math.Max(120, Width) };
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(row);
            y += row.Height + 6;
        }
        Height = Math.Max(y, 28);
        ResumeLayout();
    }

    private sealed class ClientRow : SkinControl
    {
        private readonly string _name;
        private readonly Chip _chip;

        public ClientRow(string name, string link)
        {
            _name = name;
            Height = 46;
            _chip = new Chip(link, Tone.Neutral);
            Controls.Add(_chip);
            Resize += (_, _) => _chip.Location = new Point(Width - _chip.Width - 12, (Height - _chip.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Smooth(g);
            var p = Skin.P;
            using (var path = Skin.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Skin.RadiusSm * S))
            using (var b = new SolidBrush(p.AccentSoft))
            using (var pen = new Pen(Skin.Blend(p.Accent, p.Surface, 0.5f), 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
            using (var dot = new SolidBrush(p.Success))
                g.FillEllipse(dot, 14, Height / 2f - 4, 8, 8);
            TextRenderer.DrawText(g, _name, Skin.Font(10f, FontStyle.Bold), new Rectangle(32, 0, Width - 40 - _chip.Width, Height), p.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Строка «текст слева, элемент справа»: высота — по тексту при реальной ширине, элемент по центру.</summary>
public sealed class SplitRow : Panel
{
    private readonly Control _left;
    private readonly Control _right;
    private const int Gap = 14;

    public SplitRow(Control left, Control right)
    {
        _left = left;
        _right = right;
        AutoSize = true;
        BackColor = Color.Transparent;
        Margin = Padding.Empty;
        Controls.Add(left);
        Controls.Add(right);
        left.Anchor = AnchorStyles.None;
        right.Anchor = AnchorStyles.None;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PerformLayout();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var w = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue / 2 ? proposedSize.Width : Math.Max(Width, 200);
        var leftW = Math.Max(60, w - _right.Width - Gap);
        var leftH = _left.GetPreferredSize(new Size(leftW, 0)).Height;
        return new Size(w, Math.Max(leftH, _right.Height));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var leftW = Math.Max(60, Width - _right.Width - Gap);
        var leftH = _left.GetPreferredSize(new Size(leftW, 0)).Height;
        var h = Math.Max(leftH, _right.Height);
        _left.SetBounds(0, (h - leftH) / 2, leftW, leftH);
        _right.SetBounds(Width - _right.Width, (h - _right.Height) / 2, _right.Width, _right.Height);
        if (Height != h && Width > 0) Height = h;
    }
}

/// <summary>
/// Вертикальная стопка: дети укладываются сверху вниз во всю ширину, высота считается по реальной ширине
/// (так переносятся подсказки). Свои поля ребёнка — отступы; ребёнок с Anchor.None центрируется.
/// </summary>
public sealed class VStack : Panel
{
    public VStack()
    {
        BackColor = Color.Transparent;
        Dock = DockStyle.Top;
    }

    private static int HeightFor(Control c, int width)
    {
        if (c is ThemedLabel or Panel or FlowLayoutPanel or TableLayoutPanel)
        {
            var h = c.GetPreferredSize(new Size(width, 0)).Height;
            if (h > 0 || c is ThemedLabel) return h;
        }
        return c.Height;
    }

    private int Measure(int width)
    {
        var y = 0;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            y += c.Margin.Top + HeightFor(c, Math.Max(20, width - c.Margin.Horizontal)) + c.Margin.Bottom;
        }
        return y;
    }

    private int _lastWidth;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width == _lastWidth) return;
        _lastWidth = Width;
        // Re-wrap the hints at the new width, after the parent finished its own layout pass (laying out from
        // inside it left children at the old, wider size).
        if (IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) PerformLayout(); }));
        else PerformLayout();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var w = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue / 2 ? proposedSize.Width : Math.Max(Width, 100);
        return new Size(w, Measure(w));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var y = 0;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            var avail = Math.Max(20, Width - c.Margin.Horizontal);
            var h = HeightFor(c, avail);
            y += c.Margin.Top;
            if (c.Anchor == AnchorStyles.None)
            {
                var w = c is ThemedLabel ? Math.Min(avail, c.GetPreferredSize(new Size(avail, 0)).Width) : c.Width;
                c.SetBounds((Width - w) / 2, y, w, h);
            }
            else if ((c.Anchor & (AnchorStyles.Left | AnchorStyles.Right)) == (AnchorStyles.Left | AnchorStyles.Right))
            {
                c.SetBounds(c.Margin.Left, y, avail, h);
            }
            else
            {
                c.SetBounds(c.Margin.Left, y, c.Width, h);
            }
            y += h + c.Margin.Bottom;
        }
        if (Height != y) Height = y;
    }
}

/// <summary>Тонкая полоска прокрутки: только ползунок 5 px, без дорожки и стрелок; виден лишь когда есть что прокручивать.</summary>
public sealed class ThinScrollBar : SkinControl
{
    private bool _hover, _drag;
    private int _dragStartY, _dragStartOffset;

    public int Maximum { get; private set; }   // content - viewport
    public int Viewport { get; private set; }
    public int Value { get; private set; }

    public event Action<int>? Scrolled;

    public ThinScrollBar()
    {
        Width = 12;
        Cursor = Cursors.Default;
    }

    public void SetRange(int content, int viewport, int value)
    {
        Viewport = viewport;
        Maximum = Math.Max(0, content - viewport);
        Value = Math.Clamp(value, 0, Maximum);
        Visible = Maximum > 0;
        Invalidate();
    }

    private Rectangle Thumb()
    {
        var track = Math.Max(1, Height - 8);
        var content = Maximum + Viewport;
        var h = Math.Max(36, (int)((long)Viewport * track / Math.Max(1, content)));
        var y = 4 + (Maximum == 0 ? 0 : (int)((long)Value * (track - h) / Maximum));
        return new Rectangle(Width / 2 - 3, y, 6, h);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var t = Thumb();
        if (t.Contains(e.Location)) { _drag = true; _dragStartY = e.Y; _dragStartOffset = Value; Capture = true; }
        else
        {
            // Клик по дорожке — страница вверх/вниз.
            Scrolled?.Invoke(Value + (e.Y < t.Y ? -Viewport : Viewport));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drag) return;
        var track = Math.Max(1, Height - 8) - Thumb().Height;
        if (track <= 0) return;
        Scrolled?.Invoke(_dragStartOffset + (int)((long)(e.Y - _dragStartY) * Maximum / track));
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Capture = false; base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Maximum <= 0) return;
        var g = e.Graphics;
        Smooth(g);
        var p = Skin.P;
        var t = Thumb();
        var color = Skin.Blend(p.Text, p.Bg, _hover || _drag ? 0.38f : 0.2f);
        using var path = Skin.Round(new RectangleF(t.X, t.Y, t.Width, t.Height), t.Width / 2f);
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }
}

/// <summary>Прокручиваемая область со своей тонкой полоской поверх контента; колесо мыши работает над всей областью.</summary>
public sealed class ScrollHost : Panel, IMessageFilter
{
    private readonly ThinScrollBar _bar = new();
    private VStack? _content;
    private int _offset;
    private const int Gutter = 16;

    public ScrollHost()
    {
        BackColor = Color.Transparent;
        Controls.Add(_bar);
        _bar.Scrolled += v => SetOffset(v);
    }

    /// <summary>Ширина, которую получает содержимое (полоска лежит рядом, в запасе справа).</summary>
    public int ContentWidth => Math.Max(100, Width - Gutter);

    public VStack Content => _content ?? throw new InvalidOperationException();

    public void SetContent(VStack content)
    {
        _content = content;
        content.Dock = DockStyle.None;
        Controls.Add(content);
        _bar.BringToFront();
        content.SizeChanged += (_, _) => Relayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Relayout();
    }

    private void Relayout()
    {
        if (_content == null || Width < 40) return;
        var w = ContentWidth;
        var h = _content.GetPreferredSize(new Size(w, 0)).Height;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, h - Height));
        _content.SetBounds(0, -_offset, w, h);
        _bar.SetBounds(Width - 12, 0, 12, Height);
        _bar.SetRange(h, Height, _offset);
    }

    private void SetOffset(int value)
    {
        if (_content == null) return;
        var max = Math.Max(0, _content.Height - Height);
        _offset = Math.Clamp(value, 0, max);
        _content.Top = -_offset;
        _bar.SetRange(_content.Height, Height, _offset);
    }

    public bool PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg != WM_MOUSEWHEEL || !IsHandleCreated || !Visible) return false;
        if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) return false;
        // A multiline text box under the cursor scrolls itself.
        if (FromHandle(m.HWnd) is TextBox { Multiline: true }) return false;
        if (_bar.Maximum <= 0) return false;
        var delta = (short)((long)m.WParam >> 16);
        SetOffset(_offset - delta / 120 * 60);
        return true;
    }
}
