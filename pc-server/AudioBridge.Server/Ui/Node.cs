using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AudioBridge.Server.Ui;

public enum HAlign { Stretch, Left, Center, Right }

public readonly struct Thickness
{
    public readonly float L, T, R, B;
    public Thickness(float all) { L = T = R = B = all; }
    public Thickness(float l, float t, float r, float b) { L = l; T = t; R = r; B = b; }
    public float Horizontal => L + R;
    public float Vertical => T + B;
}

/// <summary>
/// Элемент интерфейса. Всё окно — одно дерево таких элементов, рисуемое Direct2D на одной поверхности;
/// размеры и координаты в DIP. Контейнеры сами раскладывают детей (Measure → Arrange).
/// </summary>
public abstract class Node
{
    public Node? Parent;
    internal D2DHost? HostRef;
    public readonly List<Node> Children = new();

    public float X, Y, W, H;
    public Thickness Margin;
    public HAlign Align = HAlign.Stretch;

    private bool _visible = true, _enabled = true;
    public bool Hover, Pressed;

    public bool Visible
    {
        get => _visible;
        set { if (_visible == value) return; _visible = value; Relayout(); }
    }

    public virtual bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; Invalidate(); }
    }

    public D2DHost? Host
    {
        get { for (var n = this; n != null; n = n.Parent) if (n.HostRef != null) return n.HostRef; return null; }
    }

    public virtual bool Interactive => false;
    public virtual bool Focusable => false;
    public virtual Cursor? Cursor => null;
    public virtual float ChildOffsetY => 0;

    public T Add<T>(T child) where T : Node
    {
        child.Parent = this;
        Children.Add(child);
        Relayout();
        return child;
    }

    public void Invalidate() => Host?.Invalidate();
    public void Relayout() => Host?.RequestLayout();

    public bool Focused => Host?.FocusNode == this;
    protected bool Ring => Host != null && Host.FocusNode == this && Host.ShowFocusRing;

    // ---- раскладка ---------------------------------------------------------------------------------

    /// <summary>Высота при заданной ширине.</summary>
    public virtual float MeasureHeight(float width) => H;

    /// <summary>Собственная ширина (для элементов не во всю ширину).</summary>
    public virtual float MeasureWidth(float avail) => W;

    /// <summary>Расставить детей; W и H уже заданы родителем.</summary>
    public virtual void Arrange() { }

    // ---- отрисовка и ввод ----------------------------------------------------------------------------

    public virtual void Draw(Gfx g) => DrawChildren(g);

    protected void DrawChildren(Gfx g)
    {
        foreach (var c in Children)
        {
            if (!c.Visible) continue;
            var old = g.Translate(c.X, c.Y + ChildOffsetY);
            c.Draw(g);
            g.Restore(old);
        }
    }

    public virtual Node? HitTest(float x, float y)
    {
        if (!Visible || x < 0 || y < 0 || x >= W || y >= H) return null;
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var c = Children[i];
            var hit = c.HitTest(x - c.X, y - c.Y - ChildOffsetY);
            if (hit != null) return hit;
        }
        return Interactive ? this : null;
    }

    /// <summary>Положение левого верхнего угла элемента в окне.</summary>
    public (float x, float y) AbsOrigin()
    {
        float ax = 0, ay = 0;
        for (var n = this; n != null; n = n.Parent)
        {
            ax += n.X;
            ay += n.Y + (n.Parent?.ChildOffsetY ?? 0);
        }
        return (ax, ay);
    }

    public virtual void OnMouseEnter() { }
    public virtual void OnMouseLeave() { }
    public virtual void OnMouseDown(float x, float y, MouseButtons button) { }
    public virtual void OnMouseMove(float x, float y) { }
    public virtual void OnMouseUp(float x, float y) { }
    public virtual void OnClick() { }
    /// <summary>Колесо: true — обработано, дальше не передавать.</summary>
    public virtual bool OnWheel(int delta) => false;
    public virtual void OnKeyDown(KeyEventArgs e) { }
    public virtual void OnChar(char c) { }
    public virtual void OnFocusChanged(bool focused) { }
}

// ====================================================================================== контейнеры

/// <summary>Вертикальная стопка: дети сверху вниз; высота по содержимому при реальной ширине (так переносятся подсказки).</summary>
public class VStack : Node
{
    public override float MeasureHeight(float width)
    {
        float y = 0;
        foreach (var c in Children)
        {
            if (!c.Visible) continue;
            var avail = Math.Max(20, width - c.Margin.Horizontal);
            var w = ChildWidth(c, avail);
            y += c.Margin.T + c.MeasureHeight(w) + c.Margin.B;
        }
        return y;
    }

    private static float ChildWidth(Node c, float avail) =>
        c.Align == HAlign.Stretch ? avail : Math.Min(avail, c.MeasureWidth(avail));

    public override void Arrange()
    {
        float y = 0;
        foreach (var c in Children)
        {
            if (!c.Visible) continue;
            var avail = Math.Max(20, W - c.Margin.Horizontal);
            var w = ChildWidth(c, avail);
            var h = c.MeasureHeight(w);
            var x = c.Align switch
            {
                HAlign.Center => (W - w) / 2,
                HAlign.Right => W - w - c.Margin.R,
                _ => c.Margin.L,
            };
            y += c.Margin.T;
            c.X = x; c.Y = y; c.W = w; c.H = h;
            c.Arrange();
            y += h + c.Margin.B;
        }
        H = y;
    }
}

/// <summary>Карточка консоли: поверхность, рамка 1 px, скругление 12; внутри один элемент.</summary>
public sealed class Card : Node
{
    public CardTone Tone { get; set; } = CardTone.Neutral;
    public Thickness Padding { get; set; } = new(16);

    public override float MeasureHeight(float width)
    {
        var child = Children.FirstOrDefault();
        return Padding.Vertical + (child?.MeasureHeight(width - Padding.Horizontal) ?? 0);
    }

    public override void Arrange()
    {
        var child = Children.FirstOrDefault();
        if (child == null) return;
        child.X = Padding.L; child.Y = Padding.T;
        child.W = W - Padding.Horizontal;
        child.H = child.MeasureHeight(child.W);
        child.Arrange();
    }

    public override void Draw(Gfx g)
    {
        var p = Skin.P;
        var (fill, border) = Tone switch
        {
            CardTone.Danger => (p.Soft(p.Danger), Skin.Blend(p.Danger, p.Surface, 0.45f)),
            CardTone.Accent => (p.AccentSoft, p.Accent),
            _ => (p.Surface, p.Border),
        };
        g.FillRound(0, 0, W, H, Skin.Radius, fill);
        g.StrokeRound(0, 0, W, H, Skin.Radius, border);
        DrawChildren(g);
    }
}

public enum CardTone { Neutral, Danger, Accent }

/// <summary>Строка «текст слева, элемент справа»: высота — по тексту при реальной ширине, элемент по центру.</summary>
public sealed class SplitRow : Node
{
    private readonly Node _left, _right;
    private const float Gap = 14;

    public SplitRow(Node left, Node right)
    {
        _left = Add(left);
        _right = Add(right);
    }

    private float RightWidth(float width) => _right.MeasureWidth(width);

    public override float MeasureHeight(float width)
    {
        var rw = RightWidth(width);
        var lw = Math.Max(60, width - rw - Gap);
        return Math.Max(_left.MeasureHeight(lw), _right.MeasureHeight(rw));
    }

    public override void Arrange()
    {
        var rw = RightWidth(W);
        var lw = Math.Max(60, W - rw - Gap);
        var lh = _left.MeasureHeight(lw);
        var rh = _right.MeasureHeight(rw);
        var h = Math.Max(lh, rh);
        _left.X = 0; _left.Y = (h - lh) / 2; _left.W = lw; _left.H = lh; _left.Arrange();
        _right.X = W - rw; _right.Y = (h - rh) / 2; _right.W = rw; _right.H = rh; _right.Arrange();
        H = h;
    }
}

/// <summary>Плашки в ряд с переносом, ряды по центру (статус подключений).</summary>
public sealed class Flow : Node
{
    public float MaxWidth = 340;
    public float Gap = 6;

    private List<List<Node>> Rows(float width)
    {
        var rows = new List<List<Node>> { new() };
        float x = 0;
        foreach (var c in Children.Where(c => c.Visible))
        {
            var w = c.MeasureWidth(width);
            if (rows[^1].Count > 0 && x + Gap + w > width) { rows.Add(new List<Node>()); x = 0; }
            x += (rows[^1].Count > 0 ? Gap : 0) + w;
            rows[^1].Add(c);
        }
        return rows;
    }

    private static float RowHeight(List<Node> row) => row.Count == 0 ? 0 : row.Max(c => c.MeasureHeight(c.MeasureWidth(9999)));

    public override float MeasureWidth(float avail) => Math.Min(avail, MaxWidth);

    public override float MeasureHeight(float width)
    {
        var rows = Rows(Math.Min(width, MaxWidth));
        return rows.Sum(RowHeight) + Gap * Math.Max(0, rows.Count - 1);
    }

    public override void Arrange()
    {
        var rows = Rows(W);
        float y = 0;
        foreach (var row in rows)
        {
            var rh = RowHeight(row);
            var total = row.Sum(c => c.MeasureWidth(W)) + Gap * Math.Max(0, row.Count - 1);
            var x = (W - total) / 2;
            foreach (var c in row)
            {
                var w = c.MeasureWidth(W);
                var h = c.MeasureHeight(w);
                c.X = x; c.Y = y + (rh - h) / 2; c.W = w; c.H = h; c.Arrange();
                x += w + Gap;
            }
            y += rh + Gap;
        }
        H = MeasureHeight(W);
    }
}

public sealed class Divider : Node
{
    public Divider() { H = 1; }
    public override float MeasureHeight(float width) => 1;
    public override void Draw(Gfx g) => g.FillRect(0, 0, W, 1, Skin.P.Line);
}

public enum LabelRole { Text, Muted, Accent, Title, Heading, Small, Danger }

/// <summary>Надпись; цвет и шрифт берутся из темы.</summary>
public sealed class TextLabel : Node
{
    private readonly TextBlock _tb;
    private LabelRole _role;
    private bool _wrap;

    /// <summary>Не шире этого значения (одна строка с многоточием).</summary>
    public float MaxTextWidth { get; set; } = float.MaxValue;

    public TextLabel(string text = "", LabelRole role = LabelRole.Text)
    {
        _role = role;
        _tb = new TextBlock(text, StyleFor(role));
    }

    public string Text
    {
        get => _tb.Text;
        set { if (_tb.Text == value) return; _tb.Text = value; Relayout(); Invalidate(); }
    }

    public LabelRole Role
    {
        get => _role;
        set { _role = value; _tb.Style = StyleFor(value); Relayout(); }
    }

    public bool Wrap
    {
        get => _wrap;
        set { _wrap = value; Relayout(); }
    }

    private static TextStyle StyleFor(LabelRole r) => r switch
    {
        LabelRole.Title => new TextStyle(15f, true),
        LabelRole.Heading => new TextStyle(11.5f, true),
        LabelRole.Muted => new TextStyle(9f),
        LabelRole.Small => new TextStyle(8.5f),
        LabelRole.Accent => new TextStyle(9.5f, true),
        LabelRole.Danger => new TextStyle(9.5f),
        _ => new TextStyle(9.5f),
    };

    private static Color ColorFor(LabelRole r)
    {
        var p = Skin.P;
        return r switch
        {
            LabelRole.Muted or LabelRole.Small => p.Muted,
            LabelRole.Accent => p.Accent,
            LabelRole.Danger => p.Ink(p.Danger),
            _ => p.Text,
        };
    }

    public override float MeasureWidth(float avail)
    {
        var w = _tb.Measure(20000).Width;
        return Math.Min(Math.Min(avail, MaxTextWidth), (float)Math.Ceiling(w));
    }

    public override float MeasureHeight(float width)
    {
        if (_tb.IsEmpty) return 0;
        return _wrap ? _tb.Measure(width, true).Height + 2 : _tb.Measure(20000).Height;
    }

    public override void Draw(Gfx g) =>
        g.Text(_tb, 0, 0, ColorFor(_role), W, _wrap, ellipsis: !_wrap);
}

/// <summary>Основной вид логотипа: скруглённый квадрат акцентом и четыре полоски эквалайзера.</summary>
public sealed class LogoMark : Node
{
    public LogoMark() { W = H = 40; Align = HAlign.Left; }
    public override float MeasureWidth(float avail) => 40;
    public override float MeasureHeight(float width) => 40;

    public override void Draw(Gfx g)
    {
        g.FillRound(0, 0, W, H, 9, Skin.P.Accent);
        var cx = W / 2f;
        var heights = new[] { 0.28f, 0.55f, 0.4f, 0.22f };
        var xs = new[] { -9f, -3f, 3f, 9f };
        for (var i = 0; i < 4; i++)
        {
            var h = H * heights[i];
            g.FillRound(cx + xs[i] - 1.5f, (H - h) / 2f, 3f, h, 1.5f, Color.White);
        }
    }
}

/// <summary>Прокручиваемая колонка: тонкая полоска поверх контента, плавная инерция колеса.</summary>
public sealed class ScrollNode : Node
{
    private const float Gutter = 16;
    private const float BarW = 12;

    public VStack Content { get; } = new();
    private float _off, _target;
    private bool _barHover, _drag;
    private float _dragStartY, _dragStartOff;

    public ScrollNode() { Add(Content); }

    public override bool Interactive => true;
    public override float ChildOffsetY => -_off;

    private float ContentHeight => Content.H;
    private float Max => Math.Max(0, ContentHeight - H);

    public override void Arrange()
    {
        var w = Math.Max(100, W - Gutter);
        Content.X = 0; Content.Y = 0; Content.W = w;
        Content.H = Content.MeasureHeight(w);
        Content.Arrange();
        _off = Math.Clamp(_off, 0, Max);
        _target = Math.Clamp(_target, 0, Max);
    }

    public override float MeasureHeight(float width) => H;

    public override void Draw(Gfx g)
    {
        g.Clip(0, 0, W, H);
        DrawChildren(g);
        g.Unclip();
        if (Max <= 0) return;
        var t = Thumb();
        var p = Skin.P;
        g.FillRound(t.X, t.Y, t.Width, t.Height, t.Width / 2f, Skin.Blend(p.Text, p.Bg, _barHover || _drag ? 0.38f : 0.2f));
    }

    private RectangleF Thumb()
    {
        var track = Math.Max(1, H - 8);
        var content = Max + H;
        var h = Math.Max(36, H * track / Math.Max(1, content));
        var y = 4 + (Max == 0 ? 0 : _off * (track - h) / Max);
        return new RectangleF(W - BarW / 2 - 3, y, 6, h);
    }

    private void SetOffset(float v, bool animate)
    {
        _target = Math.Clamp(v, 0, Max);
        if (!animate) { _off = _target; Invalidate(); return; }
        Host?.StartTicker(this, dt =>
        {
            var d = _target - _off;
            if (Math.Abs(d) < 0.4f) { _off = _target; return false; }
            _off += d * Math.Min(1f, dt * 16f);
            return true;
        });
    }

    public override bool OnWheel(int delta)
    {
        if (Max <= 0) return false;
        SetOffset(_target - delta * 0.55f, animate: true);
        return true;
    }

    public override void OnMouseMove(float x, float y)
    {
        if (_drag)
        {
            var track = Math.Max(1, H - 8) - Thumb().Height;
            if (track > 0) SetOffset(_dragStartOff + (y - _dragStartY) * Max / track, false);
            return;
        }
        var over = Max > 0 && x >= W - BarW;
        if (over != _barHover) { _barHover = over; Invalidate(); }
    }

    public override void OnMouseLeave() { if (_barHover) { _barHover = false; Invalidate(); } }

    public override void OnMouseDown(float x, float y, MouseButtons button)
    {
        if (Max <= 0 || x < W - BarW) return;
        var t = Thumb();
        if (y >= t.Y && y <= t.Bottom) { _drag = true; _dragStartY = y; _dragStartOff = _off; }
        else SetOffset(_target + (y < t.Y ? -H : H), true);
    }

    public override void OnMouseUp(float x, float y) { _drag = false; Invalidate(); }
}

/// <summary>Горизонтальный ряд элементов по своей ширине (кнопки рядом).</summary>
public sealed class HRow : Node
{
    public float Gap = 8;

    private IEnumerable<Node> Items => Children.Where(c => c.Visible);

    public override float MeasureWidth(float avail)
    {
        var items = Items.ToList();
        return items.Sum(c => c.MeasureWidth(avail)) + Gap * Math.Max(0, items.Count - 1);
    }

    public override float MeasureHeight(float width)
    {
        var items = Items.ToList();
        return items.Count == 0 ? 0 : items.Max(c => c.MeasureHeight(c.MeasureWidth(width)));
    }

    public override void Arrange()
    {
        float x = 0;
        foreach (var c in Items)
        {
            var w = c.MeasureWidth(W);
            var h = c.MeasureHeight(w);
            c.X = x; c.Y = (H - h) / 2; c.W = w; c.H = h;
            c.Arrange();
            x += w + Gap;
        }
    }
}
