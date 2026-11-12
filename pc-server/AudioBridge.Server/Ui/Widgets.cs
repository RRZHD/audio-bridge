using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

namespace AudioBridge.Server.Ui;

public enum BtnKind { Primary, Ghost, Danger }
public enum Tone { Neutral, Success, Warning, Danger, Info, Accent }
public enum PowerMode { Off, Busy, On }

internal static class WidgetUtil
{
    public static void FocusRing(Gfx g, float w, float h, float r) =>
        g.StrokeRound(-2.5f, -2.5f, w + 5, h + 5, r + 2.5f, Skin.P.Accent, 2f, 0.55f);
}

/// <summary>Кнопка действия. Длинный текст переносится на вторую строку вместо обрезания.</summary>
public sealed class ActionButton : Node
{
    private readonly TextBlock _tb;
    private readonly float _fixedH;

    public BtnKind Kind { get; set; }
    public event EventHandler? Click;

    public ActionButton(string text, BtnKind kind = BtnKind.Primary, float height = 36)
    {
        _tb = new TextBlock(text, new TextStyle(9.5f, true));
        Kind = kind;
        _fixedH = height;
        Align = HAlign.Left;
    }

    public string Text { get => _tb.Text; set { _tb.Text = value; Relayout(); } }

    public override bool Interactive => true;
    public override bool Focusable => Enabled;
    public override Cursor? Cursor => Cursors.Hand;

    public override float MeasureWidth(float avail) => (float)Math.Ceiling(_tb.Measure(20000).Width) + 36;

    public override float MeasureHeight(float width)
    {
        var inner = width - 36;
        var natural = _tb.Measure(20000).Width;
        if (natural <= inner) return _fixedH;
        return Math.Max(_fixedH, _tb.Measure(inner, true).Height + 16);
    }

    public override void OnClick() { if (Enabled) Click?.Invoke(this, EventArgs.Empty); }

    public override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick();
    }

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        Color bg, fg, border;
        switch (Kind)
        {
            case BtnKind.Primary: bg = Hover ? p.AccentHover : p.Accent; fg = Color.White; border = bg; break;
            case BtnKind.Danger: bg = p.Danger; fg = Color.White; border = bg; break;
            default: bg = Hover ? p.AccentSoft : p.Surface; fg = Hover ? p.Accent : p.Text; border = Hover ? p.Accent : p.Border; break;
        }
        if (!Enabled) { bg = Skin.Blend(bg, p.Bg, 0.5f); fg = Skin.Blend(fg, p.Bg, 0.5f); }
        g.FillRound(0, 0, W, H, Skin.RadiusSm, bg);
        g.StrokeRound(0, 0, W, H, Skin.RadiusSm, border);
        var inner = W - 36;
        var wrap = _tb.Measure(20000).Width > inner;
        var layout = _tb.Layout(inner, wrap, center: true);
        g.Text(layout, 18, (H - layout.Metrics.Height) / 2, fg);
        if (Ring) WidgetUtil.FocusRing(g, W, H, Skin.RadiusSm);
    }
}

/// <summary>Пилюля-фильтр: выбранная — подсветка и рамка акцентом.</summary>
public sealed class Pill : Node
{
    private readonly TextBlock _tb;
    private bool _selected;
    public object? Tag;
    public event EventHandler? Click;

    public Pill(string text) { _tb = new TextBlock(text, new TextStyle(9.5f)); Align = HAlign.Left; }

    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; Invalidate(); } }
    public override bool Interactive => true;
    public override bool Focusable => true;
    public override Cursor? Cursor => Cursors.Hand;
    public override float MeasureWidth(float avail) => (float)Math.Ceiling(_tb.Measure(20000).Width) + 30;
    public override float MeasureHeight(float width) => 32;
    public override void OnClick() => Click?.Invoke(this, EventArgs.Empty);
    public override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(); }

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        var bg = _selected ? p.AccentSoft : p.Surface;
        var fg = _selected ? p.Accent : p.Text;
        var border = _selected ? p.Accent : (Hover ? Skin.Blend(p.Accent, p.Border, 0.55f) : p.Border);
        g.FillRound(0, 0, W, H, H / 2, bg);
        g.StrokeRound(0, 0, W, H, H / 2, border);
        var layout = _tb.Layout(W, center: true);
        g.Text(layout, 0, (H - layout.Metrics.Height) / 2, fg);
        if (Ring) WidgetUtil.FocusRing(g, W, H, H / 2);
    }
}

/// <summary>Переключатель консоли: 40×24, трек — акцент, бегунок — поверхность.</summary>
public sealed class ToggleSwitch : Node
{
    private bool _checked;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch() { W = 40; H = 24; Align = HAlign.Right; }

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

    public override bool Interactive => true;
    public override bool Focusable => Enabled;
    public override Cursor? Cursor => Cursors.Hand;
    public override float MeasureWidth(float avail) => 40;
    public override float MeasureHeight(float width) => 24;
    public override void OnClick() { if (Enabled) Checked = !Checked; }
    public override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(); }

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        var track = _checked ? p.Accent : p.Border;
        if (!Enabled) track = Skin.Blend(track, p.Bg, 0.5f);
        g.FillRound(1, 1, W - 2, H - 2, (H - 2) / 2, track);
        var d = H - 2 - 6;
        var x = _checked ? W - 1 - d - 3 : 1 + 3;
        g.FillCircle(x + d / 2, 1 + 3 + d / 2, d / 2, p.Surface);
        if (Ring) WidgetUtil.FocusRing(g, W, H, H / 2);
    }
}

/// <summary>Плашка состояния: капсула с мягкой подложкой тона и, по желанию, точкой.</summary>
public sealed class Chip : Node
{
    private readonly TextBlock _tb;
    private Tone _tone;

    public bool Dot { get; set; }

    public Chip(string text = "", Tone tone = Tone.Neutral, bool dot = false)
    {
        _tb = new TextBlock(text, new TextStyle(8.5f));
        _tone = tone;
        Dot = dot;
        Align = HAlign.Left;
    }

    public Tone Tone { get => _tone; set { if (_tone == value) return; _tone = value; Invalidate(); } }
    public string Text { get => _tb.Text; set { if (_tb.Text == value) return; _tb.Text = value; Relayout(); Invalidate(); } }

    public override float MeasureWidth(float avail) => Math.Max(24, (float)Math.Ceiling(_tb.Measure(20000).Width) + 18 + (Dot ? 12 : 0));
    public override float MeasureHeight(float width) => 22;

    public override void Draw(Gfx g)
    {
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
        g.FillRound(0, 0, W, H, H / 2, bg);
        var left = 0f;
        if (Dot)
        {
            g.FillCircle(12.5f, H / 2, 3.5f, dot);
            left = 14;
        }
        var layout = _tb.Layout(W - left, center: true);
        g.Text(layout, left, (H - layout.Metrics.Height) / 2, fg);
    }
}

/// <summary>Большая круглая кнопка «старт/стоп»: пульсирует, пока сервер ждёт телефон, и светится, когда звук идёт.</summary>
public sealed class PowerButton : Node
{
    private PowerMode _state;
    private float _phase;
    public event EventHandler? Click;

    public PowerButton() { W = H = 150; Align = HAlign.Center; }

    public PowerMode State
    {
        get => _state;
        set
        {
            _state = value;
            _phase = 0;
            if (value == PowerMode.Busy)
                Host?.StartTicker(this, dt => { _phase = (_phase + dt * 0.75f) % 1f; return _state == PowerMode.Busy; });
            Invalidate();
        }
    }

    public override bool Interactive => true;
    public override bool Focusable => true;
    public override Cursor? Cursor => Cursors.Hand;
    public override float MeasureWidth(float avail) => 150;
    public override float MeasureHeight(float width) => 150;
    public override void OnClick() => Click?.Invoke(this, EventArgs.Empty);
    public override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(); }

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        float cx = W / 2f, cy = H / 2f;
        var inner = Math.Min(W, H) * 0.72f;

        var fill = _state switch { PowerMode.On => p.Accent, PowerMode.Busy => p.AccentSoft, _ => p.Bg };
        var ring = _state switch
        {
            PowerMode.On => Skin.Blend(p.Accent, p.Surface, 0.25f),
            PowerMode.Busy => Skin.Blend(p.Accent, p.Surface, 0.55f),
            _ => Hover ? Skin.Blend(p.Accent, p.Border, 0.5f) : p.Border,
        };
        var icon = _state switch { PowerMode.On => Color.White, PowerMode.Busy => p.Accent, _ => Hover ? p.Accent : p.Muted };

        if (_state == PowerMode.Busy)
        {
            var rr = inner / 2 * (1f + _phase * 0.3f);
            g.StrokeCircle(cx, cy, rr, p.Accent, 2f, 1 - _phase);
        }
        if (_state == PowerMode.On)
        {
            foreach (var (k, a) in new[] { (1.28f, 0.08f), (1.14f, 0.12f) })
                g.FillCircle(cx, cy, inner / 2 * k, p.Accent, a);
        }
        g.FillCircle(cx, cy, inner / 2, fill);
        g.StrokeCircle(cx, cy, inner / 2 - 1, ring, 2f);

        // Значок питания: дуга и вертикальная черта.
        var ir = inner * 0.2f;
        var sw = Math.Max(3f, inner * 0.045f);
        g.Arc(cx, cy + 2, ir, -55, 290, icon, sw);
        g.Line(cx, cy - ir - 4, cx, cy - 2, icon, sw);
        if (Ring) g.StrokeCircle(cx, cy, inner / 2 + 4, p.Accent, 2f, 0.55f);
    }
}

/// <summary>Выпадающий выбор в стиле консоли: поле 40 px со стрелкой и списком поверх окна.</summary>
public sealed class SelectBox : Node
{
    private readonly List<object> _items = new();
    private int _selected = -1;
    private readonly TextBlock _tb = new("", new TextStyle(9.5f));

    public IList<object> Items => _items;
    public Func<object, string> Format { get; set; } = o => o.ToString() ?? "";
    private bool _stale = true;

    /// <summary>Подпись выбранного пункта кэшируется (её расчёт может быть дорогим); вызвать, когда она могла измениться.</summary>
    public void InvalidateText() { _stale = true; Invalidate(); }
    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            _stale = true;
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem
    {
        get => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;
        set => SelectedIndex = value == null ? -1 : _items.IndexOf(value);
    }

    public override bool Interactive => true;
    public override bool Focusable => Enabled;
    public override Cursor? Cursor => Enabled ? Cursors.Hand : null;
    public override float MeasureHeight(float width) => 40;

    public override void OnClick() => Open();

    public override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.Down) Open();
    }

    private void Open()
    {
        if (!Enabled || _items.Count == 0 || Host == null) return;
        Host.ShowPopup(new DropDown(this));
    }

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        var border = Hover && Enabled ? Skin.Blend(p.Accent, p.Border, 0.55f) : p.Border;
        g.FillRound(0, 0, W, H, Skin.RadiusSm, p.Surface);
        g.StrokeRound(0, 0, W, H, Skin.RadiusSm, border);
        var fg = Enabled ? p.Text : Skin.Blend(p.Text, p.Surface, 0.5f);
        if (_stale) { _tb.Text = SelectedItem == null ? "" : Format(SelectedItem); _stale = false; }
        if (!_tb.IsEmpty)
        {
            var layout = _tb.Layout(W - 40, ellipsis: true);
            g.Text(layout, 12, (H - layout.Metrics.Height) / 2, fg);
        }
        var ax = W - 22f;
        var ay = H / 2f;
        g.Polyline(p.Muted, 1.8f, new Vector2(ax - 4, ay - 2), new Vector2(ax, ay + 2), new Vector2(ax + 4, ay - 2));
        if (Ring) WidgetUtil.FocusRing(g, W, H, Skin.RadiusSm);
    }
}

/// <summary>Список выбора: рисуется поверх окна, пункты переносятся, если не помещаются.</summary>
internal sealed class DropDown : Node
{
    private const float Pad = 4, RowPadV = 7, CheckW = 24;
    private readonly SelectBox _owner;
    private readonly List<TextBlock> _rows = new();
    private readonly List<float> _rowH = new();
    private readonly float _contentH;
    private int _hot;
    private float _off, _lastY;

    public DropDown(SelectBox owner)
    {
        _owner = owner;
        W = owner.W;
        var textW = W - 2 * Pad - CheckW - 12;
        float total = Pad * 2;
        foreach (var item in owner.Items)
        {
            var tb = new TextBlock(owner.Format(item), new TextStyle(9.5f));
            _rows.Add(tb);
            var h = tb.Measure(textW, true).Height + 2 * RowPadV;
            _rowH.Add(h);
            total += h;
        }
        _contentH = total;
        _hot = owner.SelectedIndex;

        var (ox, oy) = owner.AbsOrigin();
        var hostH = owner.Host!.ClientHeightDip;
        var below = hostH - (oy + owner.H + 2) - 8;
        var above = oy - 2 - 8;
        if (_contentH <= below || below >= above) { H = Math.Min(_contentH, Math.Max(60, below)); Y = oy + owner.H + 2; }
        else { H = Math.Min(_contentH, above); Y = oy - 2 - H; }
        X = ox;
    }

    public override bool Interactive => true;
    public override Cursor? Cursor => Cursors.Default;

    private int RowAt(float y)
    {
        var cy = y + _off - Pad;
        float acc = 0;
        for (var i = 0; i < _rowH.Count; i++)
        {
            if (cy >= acc && cy < acc + _rowH[i]) return i;
            acc += _rowH[i];
        }
        return -1;
    }

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        // Лёгкая тень под списком.
        for (var i = 3; i >= 1; i--) g.FillRound(-i, 2 - i + i, W + 2 * i, H + 2 * i, Skin.RadiusSm + i, Color.Black, p.Dark ? 0.10f : 0.035f);
        g.FillRound(0, 0, W, H, Skin.RadiusSm, p.Surface);
        g.Clip(1, 1, W - 2, H - 2);
        var y = Pad - _off;
        for (var i = 0; i < _rows.Count; i++)
        {
            var h = _rowH[i];
            var selected = i == _owner.SelectedIndex;
            var hot = i == _hot;
            if (hot) g.FillRound(Pad, y, W - 2 * Pad, h, 6, p.AccentSoft);
            var fg = hot || selected ? p.Accent : p.Text;
            if (selected) g.Text(new TextBlock("✓", new TextStyle(9.5f, true)), Pad + 8, y + RowPadV, fg, 20);
            g.Text(_rows[i], Pad + CheckW + 4, y + RowPadV, fg, W - 2 * Pad - CheckW - 12, wrap: true);
            y += h;
        }
        g.Unclip();
        g.StrokeRound(0, 0, W, H, Skin.RadiusSm, p.Border);
    }

    public override void OnMouseMove(float x, float y)
    {
        _lastY = y;
        var r = RowAt(y);
        if (r != _hot) { _hot = r; Invalidate(); }
    }

    public override void OnMouseDown(float x, float y, MouseButtons button) => _lastY = y;
    public override void OnMouseUp(float x, float y) => _lastY = y;

    public override void OnClick()
    {
        var r = RowAt(_lastY);
        if (r >= 0) _owner.SelectedIndex = r;
        Host?.ClosePopup();
    }

    public override bool OnWheel(int delta)
    {
        var max = Math.Max(0, _contentH - H);
        if (max <= 0) { Host?.ClosePopup(); return true; }
        _off = Math.Clamp(_off - delta * 0.5f, 0, max);
        Invalidate();
        return true;
    }

    public override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape: case Keys.Tab: Host?.ClosePopup(); break;
            case Keys.Down: _hot = Math.Min(_rows.Count - 1, _hot + 1); EnsureVisible(); Invalidate(); break;
            case Keys.Up: _hot = Math.Max(0, _hot - 1); EnsureVisible(); Invalidate(); break;
            case Keys.Enter: case Keys.Space:
                if (_hot >= 0) _owner.SelectedIndex = _hot;
                Host?.ClosePopup();
                break;
        }
    }

    private void EnsureVisible()
    {
        if (_hot < 0) return;
        var top = Pad + _rowH.Take(_hot).Sum();
        var bottom = top + _rowH[_hot];
        if (top < _off) _off = top - Pad;
        else if (bottom > _off + H) _off = bottom - H + Pad;
    }
}

/// <summary>Числовое поле (порт): рамка консоли с кольцом фокуса и собственный однострочный редактор.</summary>
public sealed class NumberBox : Node
{
    private string _text = "";
    private int _caret, _anchor = -1;
    private bool _caretOn = true;
    private readonly TextBlock _tb = new("", new TextStyle(10f));

    public int Minimum { get; set; } = 1024;
    public int Maximum { get; set; } = 65535;

    public NumberBox() { W = 110; Align = HAlign.Right; }

    public int Value
    {
        get => int.TryParse(_text, out var v) ? Math.Clamp(v, Minimum, Maximum) : Minimum;
        set { _text = Math.Clamp(value, Minimum, Maximum).ToString(); _caret = _text.Length; _anchor = -1; _tb.Text = _text; Invalidate(); }
    }

    public override bool Interactive => true;
    public override bool Focusable => Enabled;
    public override Cursor? Cursor => Enabled ? Cursors.IBeam : null;
    public override float MeasureWidth(float avail) => 110;
    public override float MeasureHeight(float width) => 40;

    public override void OnFocusChanged(bool focused)
    {
        if (focused)
        {
            _caretOn = true;
            Host?.StartTicker(this, dt =>
            {
                _blink += dt;
                if (_blink >= 0.53f) { _blink = 0; _caretOn = !_caretOn; }
                return Focused;
            });
        }
        else
        {
            Value = Value; // clamp the typed number
        }
        Invalidate();
    }

    private float _blink;

    private void Touch() { _caretOn = true; _blink = 0; Invalidate(); }

    private int HitCaret(float x)
    {
        if (_text.Length == 0) return 0;
        var m = _tb.Layout(W - 24).HitTestPoint(x - 12, 10, out var trailing, out _);
        return Math.Clamp((int)m.TextPosition + (trailing ? 1 : 0), 0, _text.Length);
    }

    public override void OnMouseDown(float x, float y, MouseButtons button)
    {
        if (!Enabled) return;
        _caret = HitCaret(x);
        _anchor = _caret;
        Touch();
    }

    public override void OnMouseMove(float x, float y)
    {
        if (!Pressed || _anchor < 0) return;
        _caret = HitCaret(x);
        Touch();
    }

    private (int a, int b) Sel => _anchor < 0 || _anchor == _caret ? (_caret, _caret) : (Math.Min(_anchor, _caret), Math.Max(_anchor, _caret));

    private void Replace(string s)
    {
        var (a, b) = Sel;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        var next = _text[..a] + digits + _text[b..];
        if (next.Length > 5) return;
        _text = next;
        _caret = a + digits.Length;
        _anchor = -1;
        _tb.Text = _text;
        Touch();
    }

    public override void OnChar(char c)
    {
        if (char.IsDigit(c)) Replace(c.ToString());
    }

    public override void OnKeyDown(KeyEventArgs e)
    {
        var shift = e.Shift;
        switch (e.KeyCode)
        {
            case Keys.Back:
            {
                var (a, b) = Sel;
                if (a == b && a > 0) { _anchor = a - 1; _caret = a; }
                Replace("");
                break;
            }
            case Keys.Delete:
            {
                var (a, b) = Sel;
                if (a == b && a < _text.Length) { _anchor = a; _caret = a + 1; }
                Replace("");
                break;
            }
            case Keys.Left: Move(Sel.a == Sel.b || shift ? _caret - 1 : Sel.a, shift); break;
            case Keys.Right: Move(Sel.a == Sel.b || shift ? _caret + 1 : Sel.b, shift); break;
            case Keys.Home: Move(0, shift); break;
            case Keys.End: Move(_text.Length, shift); break;
            case Keys.A when e.Control: _anchor = 0; _caret = _text.Length; Touch(); break;
            case Keys.V when e.Control:
                try { if (Clipboard.ContainsText()) Replace(Clipboard.GetText()); } catch { }
                break;
            case Keys.C when e.Control:
                try { var (a, b) = Sel; if (a != b) Clipboard.SetText(_text[a..b]); } catch { }
                break;
        }
    }

    private void Move(int pos, bool extend)
    {
        if (extend && _anchor < 0) _anchor = _caret;
        if (!extend) _anchor = -1;
        _caret = Math.Clamp(pos, 0, _text.Length);
        Touch();
    }

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        var focused = Focused && Enabled;
        if (focused) g.StrokeRound(-1.5f, -1.5f, W + 3, H + 3, Skin.RadiusSm + 1.5f, p.Accent, 3f, 46 / 255f);
        g.FillRound(0, 0, W, H, Skin.RadiusSm, p.Surface);
        g.StrokeRound(0, 0, W, H, Skin.RadiusSm, focused ? p.Accent : p.Border);

        var fg = Enabled ? p.Text : Skin.Blend(p.Text, p.Surface, 0.5f);
        var layout = _tb.IsEmpty ? null : _tb.Layout(W - 24);
        var th = layout?.Metrics.Height ?? 16;
        var ty = (H - th) / 2;
        var (a, b) = Sel;
        float XAt(int pos)
        {
            if (layout == null || pos <= 0) return 0;
            layout.HitTestTextPosition((uint)(pos - 1), true, out var px, out _, out _);
            return px;
        }
        if (focused && a != b)
            g.FillRect(12 + XAt(a), ty, XAt(b) - XAt(a), th, p.Accent, 0.25f);
        if (layout != null) g.Text(layout, 12, ty, fg);
        if (focused && a == b && _caretOn) g.FillRect(12 + XAt(_caret), ty, 1.2f, th, p.Text);
    }
}

/// <summary>Список подключённых телефонов: строки с точкой, именем и плашкой способа связи.</summary>
public sealed class ClientList : Node
{
    private readonly TextLabel _empty = new("Телефон пока не подключён", LabelRole.Muted);
    private const float RowH = 46, RowGap = 6;

    public ClientList() { Add(_empty); }

    private IEnumerable<ClientRow> Rows => Children.OfType<ClientRow>();

    public void SetClients(IReadOnlyList<(string Name, string Link)> clients)
    {
        foreach (var r in Rows.ToList()) Children.Remove(r);
        _empty.Visible = clients.Count == 0;
        foreach (var (name, link) in clients)
        {
            var shortLink = link.Contains(" (") ? link[..link.IndexOf(" (", StringComparison.Ordinal)] : link;
            Add(new ClientRow(name, shortLink));
        }
        Relayout();
        Invalidate();
    }

    public override float MeasureHeight(float width)
    {
        var n = Rows.Count();
        return n == 0 ? 28 : n * (RowH + RowGap);
    }

    public override void Arrange()
    {
        _empty.X = 0; _empty.Y = 4; _empty.W = W; _empty.H = _empty.MeasureHeight(W);
        float y = 0;
        foreach (var r in Rows)
        {
            r.X = 0; r.Y = y; r.W = W; r.H = RowH; r.Arrange();
            y += RowH + RowGap;
        }
    }

    private sealed class ClientRow : Node
    {
        private readonly TextBlock _name;
        private readonly Chip _chip;

        public ClientRow(string name, string link)
        {
            _name = new TextBlock(name, new TextStyle(10f, true));
            _chip = Add(new Chip(link, Tone.Neutral));
        }

        public override void Arrange()
        {
            _chip.W = _chip.MeasureWidth(W); _chip.H = 22;
            _chip.X = W - _chip.W - 12; _chip.Y = (H - _chip.H) / 2;
        }

        public override void Draw(Gfx g)
        {
            var p = Skin.P;
            g.FillRound(0, 0, W, H, Skin.RadiusSm, p.AccentSoft);
            g.StrokeRound(0, 0, W, H, Skin.RadiusSm, Skin.Blend(p.Accent, p.Surface, 0.5f));
            g.FillCircle(18, H / 2, 4, p.Success);
            var layout = _name.Layout(Math.Max(20, W - 40 - _chip.W), ellipsis: true);
            g.Text(layout, 32, (H - layout.Metrics.Height) / 2, p.Text);
            DrawChildren(g);
        }
    }
}

/// <summary>Лог: моноширинный текст с переносом и своей тонкой прокруткой; колесо уходит наружу, когда лог упёрся в край.</summary>
public sealed class LogView : Node
{
    private const int MaxLines = 600;
    private readonly List<string> _lines = new();
    private readonly TextBlock _tb = new("", new TextStyle(9f, Mono: true));
    private float _off;
    private bool _stick = true;
    private float _fixedH;

    public LogView(float height = 230) { _fixedH = height; }

    public override float MeasureHeight(float width) => _fixedH;
    public override bool Interactive => true;

    public string Text
    {
        get => string.Join("\n", _lines);
        set
        {
            _lines.Clear();
            _lines.AddRange(value.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Changed();
        }
    }

    public string AllText => string.Join(Environment.NewLine, _lines);

    public void AppendLine(string line)
    {
        _lines.Add(line);
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        Changed();
    }

    public void Clear() { _lines.Clear(); Changed(); }

    private void Changed()
    {
        _tb.Text = string.Join("\n", _lines);
        if (_stick) _off = float.MaxValue;
        Invalidate();
    }

    private float TextHeight => _tb.IsEmpty ? 0 : _tb.Layout(Math.Max(20, W - 12), wrap: true).Metrics.Height;
    private float Max => Math.Max(0, TextHeight - H);

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        var max = Max;
        _off = Math.Clamp(_off, 0, max);
        g.Clip(0, 0, W, H);
        if (!_tb.IsEmpty) g.Text(_tb, 0, -_off, p.Text, Math.Max(20, W - 12), wrap: true);
        g.Unclip();
        if (max > 0)
        {
            var track = H - 4;
            var th = Math.Max(30, H * track / (max + H));
            var ty = 2 + _off * (track - th) / max;
            g.FillRound(W - 6, ty, 4, th, 2, Skin.Blend(p.Text, p.Surface, 0.22f));
        }
    }

    public override bool OnWheel(int delta)
    {
        var max = Max;
        if (max <= 0) return false;
        var next = Math.Clamp(_off - delta * 0.5f, 0, max);
        if (next == _off) return false; // at the edge: let the column scroll instead
        _off = next;
        _stick = next >= max - 1;
        Invalidate();
        return true;
    }
}

/// <summary>Верх окна: логотип, название и выбор темы справа.</summary>
public sealed class HeaderNode : Node
{
    private readonly List<Pill> _pills = new();
    private readonly LogoMark _logo = new();
    private readonly TextLabel _title = new("Audio Bridge Server", LabelRole.Heading);
    private readonly TextLabel _sub = new("Звук с ПК на телефон — Wi-Fi, USB, Bluetooth", LabelRole.Muted);

    public HeaderNode()
    {
        Add(_logo); Add(_title); Add(_sub);
    }

    public Pill AddPill(Pill p) { _pills.Add(p); return Add(p); }

    public override float MeasureHeight(float width) => 72;

    public override void Arrange()
    {
        _logo.X = 20; _logo.Y = 18; _logo.W = _logo.H = 40;
        _title.X = 72; _title.Y = 16; _title.W = _title.MeasureWidth(W); _title.H = _title.MeasureHeight(_title.W);
        _sub.X = 73; _sub.Y = 40; _sub.W = _sub.MeasureWidth(W); _sub.H = _sub.MeasureHeight(_sub.W);
        var x = W - 20;
        for (var i = _pills.Count - 1; i >= 0; i--)
        {
            var p = _pills[i];
            p.W = p.MeasureWidth(W); p.H = 32;
            x -= p.W;
            p.X = x; p.Y = 20;
            x -= 6;
        }
    }
}

/// <summary>Корень окна: шапка и две прокручиваемые колонки (слева — состояние, справа — настройки).</summary>
public sealed class MainRoot : Node
{
    public HeaderNode Header { get; } = new();
    public ScrollNode Left { get; } = new();
    public ScrollNode Right { get; } = new();

    public MainRoot() { Add(Header); Add(Left); Add(Right); }

    public override void Arrange()
    {
        Header.X = 0; Header.Y = 0; Header.W = W; Header.H = 72; Header.Arrange();
        const float colLeft = 396;
        var bodyH = Math.Max(0, H - 72 - 20);
        Left.X = 20; Left.Y = 72; Left.W = colLeft; Left.H = bodyH; Left.Arrange();
        Right.X = 20 + colLeft; Right.Y = 72; Right.W = Math.Max(300, W - 40 - colLeft); Right.H = bodyH; Right.Arrange();
    }

    public override void Draw(Gfx g) => DrawChildren(g);
}
