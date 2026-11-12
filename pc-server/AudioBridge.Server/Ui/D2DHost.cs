using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace AudioBridge.Server.Ui;

/// <summary>
/// Единственный дочерний элемент окна: рисует всё дерево <see cref="Node"/> через Direct2D (GPU, DirectWrite)
/// и маршрутизирует мышь и клавиатуру. Перерисовка — по требованию, анимации — таймером только пока нужны.
/// </summary>
public sealed class D2DHost : Control
{
    private ID2D1HwndRenderTarget? _rt;
    private readonly Gfx _g = new();
    private bool _layoutDirty = true;
    private Node? _hover, _capture;

    private readonly Dictionary<object, Func<float, bool>> _tickers = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 8 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;

    public Node Root { get; }
    public Node? Popup { get; private set; }
    public Node? FocusNode { get; private set; }
    public bool ShowFocusRing { get; private set; }

    /// <summary>Показывать перерисовку без ожидания вертикальной синхронизации (для замеров).</summary>
    public bool Immediate { get; set; }

    public float Scale => Math.Max(0.5f, DeviceDpi / 96f);
    public float ClientHeightDip => Height / Scale;
    public float ClientWidthDip => Width / Scale;

    public D2DHost(Node root)
    {
        Root = root;
        root.HostRef = this;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        Dock = DockStyle.Fill;
        _timer.Tick += (_, _) => Tick();
        Skin.Changed += OnSkinChanged;
    }

    private void OnSkinChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Skin.Changed -= OnSkinChanged;
            _timer.Dispose();
            DisposeTarget();
        }
        base.Dispose(disposing);
    }

    // ---- анимации ----------------------------------------------------------------------------------

    /// <summary>Запускает покадровую функцию (получает секунды с прошлого кадра; false — закончить).</summary>
    public void StartTicker(object key, Func<float, bool> tick)
    {
        _tickers[key] = tick;
        if (!_timer.Enabled)
        {
            _lastTick = _clock.Elapsed.TotalSeconds;
            _timer.Start();
        }
        Invalidate();
    }

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Min(0.1, now - _lastTick);
        _lastTick = now;
        foreach (var key in _tickers.Keys.ToList())
        {
            if (!_tickers[key](dt)) _tickers.Remove(key);
        }
        if (_tickers.Count == 0) _timer.Stop();
        Invalidate();
    }

    // ---- раскладка и отрисовка -----------------------------------------------------------------------

    public void RequestLayout()
    {
        _layoutDirty = true;
        Invalidate();
    }

    private void EnsureTarget()
    {
        if (_rt != null) return;
        var dpi = (float)DeviceDpi;
        var props = new RenderTargetProperties
        {
            Type = RenderTargetType.Default,
            DpiX = dpi,
            DpiY = dpi,
            Usage = RenderTargetUsage.None,
            MinLevel = FeatureLevel.Default,
            PixelFormat = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.Unknown, Vortice.DCommon.AlphaMode.Unknown),
        };
        var hp = new HwndRenderTargetProperties
        {
            Hwnd = Handle,
            PixelSize = new SizeI(Math.Max(1, Width), Math.Max(1, Height)),
            PresentOptions = Immediate ? PresentOptions.Immediately : PresentOptions.None,
        };
        _rt = Fx.D2D.CreateHwndRenderTarget(props, hp);
        _rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Cleartype;
    }

    private void DisposeTarget()
    {
        _g.Release();
        _rt?.Dispose();
        _rt = null;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        DisposeTarget();
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) => Render();

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        try { _rt?.Resize(new SizeI(Math.Max(1, Width), Math.Max(1, Height))); } catch { DisposeTarget(); }
        RequestLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        DisposeTarget();
        RequestLayout();
    }

    private void DoLayout()
    {
        _layoutDirty = false;
        Root.X = 0; Root.Y = 0;
        Root.W = ClientWidthDip;
        Root.H = ClientHeightDip;
        Root.Arrange();
    }

    public double DbgLayoutMs, DbgDrawMs, DbgEndMs;

    public void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        try
        {
            EnsureTarget();
            var sw = Stopwatch.StartNew();
            if (_layoutDirty) DoLayout();
            DbgLayoutMs = sw.Elapsed.TotalMilliseconds;
            _g.Bind(_rt!);
            _rt!.BeginDraw();
            _rt.Transform = Matrix3x2.Identity;
            _g.Clear(Skin.P.Bg);
            Root.Draw(_g);
            if (Popup != null)
            {
                var old = _g.Translate(Popup.X, Popup.Y);
                Popup.Draw(_g);
                _g.Restore(old);
            }
            DbgDrawMs = sw.Elapsed.TotalMilliseconds - DbgLayoutMs;
            _rt.EndDraw();
            DbgEndMs = sw.Elapsed.TotalMilliseconds - DbgLayoutMs - DbgDrawMs;
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x8899000C || ex is SharpGen.Runtime.SharpGenException)
        {
            // Устройство потеряно (смена видеокарты, режим экрана) — пересоздадим поверхность.
            if (Environment.GetEnvironmentVariable("AB_DEBUG") == "1") System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ab_d2d.log"), ex + Environment.NewLine);
            DisposeTarget();
            Invalidate();
        }
    }

    /// <summary>Снимок окна (для проверки внешнего вида): форсирует кадр и копирует его через DWM.</summary>
    public Bitmap Snapshot()
    {
        Render();
        var bmp = new Bitmap(Width, Height);
        using var gr = Graphics.FromImage(bmp);
        var hdc = gr.GetHdc();
        try { PrintWindow(Handle, hdc, 2); } finally { gr.ReleaseHdc(hdc); }
        return bmp;
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    // ---- всплывающий слой и фокус --------------------------------------------------------------------

    public void ShowPopup(Node popup)
    {
        popup.HostRef = this;
        Popup = popup;
        SetHover(null);
        Invalidate();
    }

    public void ClosePopup()
    {
        if (Popup == null) return;
        Popup.HostRef = null;
        Popup = null;
        Invalidate();
    }

    public void SetFocus(Node? node, bool ring)
    {
        if (FocusNode == node) { ShowFocusRing = ring; Invalidate(); return; }
        var old = FocusNode;
        FocusNode = node;
        ShowFocusRing = ring;
        old?.OnFocusChanged(false);
        node?.OnFocusChanged(true);
        Invalidate();
    }

    private List<Node> Focusables()
    {
        var list = new List<Node>();
        void Walk(Node n)
        {
            if (!n.Visible) return;
            if (n.Focusable && n.Enabled) list.Add(n);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(Root);
        return list;
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        ClosePopup();
        SetFocus(null, false);
    }

    // ---- мышь ----------------------------------------------------------------------------------------

    private Node? HitTest(float x, float y)
    {
        if (Popup != null) return Popup.HitTest(x - Popup.X, y - Popup.Y);
        return Root.HitTest(x, y);
    }

    private (float, float) Dip(Point p) => (p.X / Scale, p.Y / Scale);

    private void SetHover(Node? n)
    {
        if (n == _hover) return;
        if (_hover != null) { _hover.Hover = false; _hover.OnMouseLeave(); }
        _hover = n;
        if (n != null) { n.Hover = true; n.OnMouseEnter(); }
        Cursor = n?.Cursor ?? Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Focused && FindForm()?.ContainsFocus == true) Focus(); // колесо идёт в окно под курсором
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (x, y) = Dip(e.Location);
        if (_capture != null)
        {
            var (cx, cy) = _capture.AbsOrigin();
            _capture.OnMouseMove(x - cx, y - cy);
            return;
        }
        var hit = HitTest(x, y);
        SetHover(hit);
        if (hit != null)
        {
            var (ox, oy) = hit.AbsOrigin();
            hit.OnMouseMove(x - ox, y - oy);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_capture == null) SetHover(null);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        var (x, y) = Dip(e.Location);
        var hit = HitTest(x, y);
        if (Popup != null && hit == null) { ClosePopup(); return; }
        if (hit == null) { SetFocus(null, false); return; }
        _capture = hit;
        hit.Pressed = true;
        Capture0(true);
        SetFocus(hit.Focusable && hit.Enabled ? hit : null, false);
        var (ox, oy) = hit.AbsOrigin();
        hit.OnMouseDown(x - ox, y - oy, e.Button);
        Invalidate();
    }

    private void Capture0(bool on) => Capture = on;

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var c = _capture;
        if (c == null) return;
        _capture = null;
        Capture0(false);
        c.Pressed = false;
        var (x, y) = Dip(e.Location);
        var (ox, oy) = c.AbsOrigin();
        c.OnMouseUp(x - ox, y - oy);
        var hit = HitTest(x, y);
        if (hit == c) c.OnClick();
        SetHover(HitTest(x, y));
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var (x, y) = Dip(e.Location);
        var hit = HitTest(x, y);
        if (Popup != null)
        {
            if (hit == null) ClosePopup(); else Popup.OnWheel(e.Delta);
            return;
        }
        for (var n = hit; n != null; n = n.Parent)
            if (n.OnWheel(e.Delta)) break;
        SetHover(HitTest(x, y));
    }

    // ---- клавиатура ----------------------------------------------------------------------------------

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Popup != null) { Popup.OnKeyDown(e); e.Handled = true; Invalidate(); return; }
        if (e.KeyCode == Keys.Tab)
        {
            var list = Focusables();
            if (list.Count > 0)
            {
                var i = FocusNode == null ? -1 : list.IndexOf(FocusNode);
                i = e.Shift ? (i <= 0 ? list.Count - 1 : i - 1) : (i + 1) % list.Count;
                SetFocus(list[i], true);
            }
            e.Handled = true;
            return;
        }
        if (FocusNode != null)
        {
            ShowFocusRing = true;
            FocusNode.OnKeyDown(e);
            e.Handled = true;
            Invalidate();
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (!char.IsControl(e.KeyChar) && FocusNode != null) { FocusNode.OnChar(e.KeyChar); e.Handled = true; }
    }
}
