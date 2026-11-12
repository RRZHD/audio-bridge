using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using SD = System.Drawing;

namespace AudioBridge.Server.Ui;

/// <summary>Стиль текста: размер в пунктах, жирность, моноширинный.</summary>
public readonly record struct TextStyle(float Pt, bool Bold = false, bool Mono = false);

/// <summary>Общие фабрики Direct2D / DirectWrite и кэш шрифтов.</summary>
internal static class Fx
{
    public static readonly ID2D1Factory D2D = D2D1.D2D1CreateFactory<ID2D1Factory>();
    public static readonly IDWriteFactory Dw = DWrite.DWriteCreateFactory<IDWriteFactory>();

    private static readonly Dictionary<TextStyle, IDWriteTextFormat> Formats = new();
    private static string? _ui, _mono;

    private static bool Has(string family)
    {
        using var coll = Dw.GetSystemFontCollection(false);
        return coll.FindFamilyName(family, out _);
    }

    public static string UiFamily => _ui ??= Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static string MonoFamily => _mono ??= Has("Cascadia Mono") ? "Cascadia Mono" : "Consolas";

    public static IDWriteTextFormat Format(TextStyle s)
    {
        if (Formats.TryGetValue(s, out var f)) return f;
        f = Dw.CreateTextFormat(s.Mono ? MonoFamily : UiFamily, null!, s.Bold ? FontWeight.Bold : FontWeight.Normal,
            Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, s.Pt * 96f / 72f, "ru-RU");
        return Formats[s] = f;
    }

    public static Color4 C(SD.Color c, float a = 1f) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f * a);
}

/// <summary>Текст с кэшем раскладки DirectWrite: пересоздаётся только при смене текста, стиля или ширины.</summary>
public sealed class TextBlock : IDisposable
{
    private string _text = "";
    private TextStyle _style;
    private IDWriteTextLayout? _layout;
    private IDWriteInlineObject? _sign;
    private (float w, bool wrap, bool ell, bool center) _key;

    public TextBlock(string text, TextStyle style) { _text = text; _style = style; }

    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; Reset(); }
    }

    public TextStyle Style
    {
        get => _style;
        set { if (_style == value) return; _style = value; Reset(); }
    }

    public bool IsEmpty => _text.Length == 0;

    private void Reset()
    {
        _layout?.Dispose(); _layout = null;
        _sign?.Dispose(); _sign = null;
    }

    public IDWriteTextLayout Layout(float maxW, bool wrap = false, bool ellipsis = false, bool center = false)
    {
        maxW = Math.Clamp(maxW, 1f, 20000f);
        var key = (maxW, wrap, ellipsis, center);
        if (_layout != null && key == _key) return _layout;
        Reset();
        _key = key;
        var l = Fx.Dw.CreateTextLayout(_text, Fx.Format(_style), maxW, 20000f);
        l.WordWrapping = wrap ? WordWrapping.Wrap : WordWrapping.NoWrap;
        l.TextAlignment = center ? TextAlignment.Center : TextAlignment.Leading;
        if (ellipsis && !wrap)
        {
            _sign = Fx.Dw.CreateEllipsisTrimmingSign(l);
            l.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, _sign);
        }
        return _layout = l;
    }

    /// <summary>Размер текста: при переносе — по заданной ширине, иначе — по длине строки.</summary>
    public SD.SizeF Measure(float maxW, bool wrap = false)
    {
        if (_text.Length == 0) return SD.SizeF.Empty;
        var m = Layout(wrap ? maxW : 20000f, wrap).Metrics;
        return new SD.SizeF(wrap ? Math.Min(maxW, m.Width) : m.WidthIncludingTrailingWhitespace, m.Height);
    }

    public void Dispose() => Reset();
}

/// <summary>Кисть и примитивы поверх целевой поверхности Direct2D (единицы — DIP).</summary>
public sealed class Gfx
{
    internal ID2D1RenderTarget Rt = null!;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _round;

    internal void Bind(ID2D1RenderTarget rt)
    {
        if (!ReferenceEquals(Rt, rt)) { _brush?.Dispose(); _brush = null; }
        Rt = rt;
        _brush ??= rt.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        _round ??= Fx.D2D.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
    }

    internal void Release()
    {
        _brush?.Dispose(); _brush = null;
    }

    private ID2D1SolidColorBrush B(SD.Color c, float a = 1f)
    {
        _brush!.Color = Fx.C(c, a);
        return _brush;
    }

    public void Clear(SD.Color c) => Rt.Clear(Fx.C(c));

    public void FillRect(float x, float y, float w, float h, SD.Color c, float a = 1f) =>
        Rt.FillRectangle(new Rect(x, y, w, h), B(c, a));

    /// <summary>Скруглённый прямоугольник; рамка рисуется внутрь по 1 px, как в консоли.</summary>
    public void FillRound(float x, float y, float w, float h, float r, SD.Color c, float a = 1f) =>
        Rt.FillRoundedRectangle(new RoundedRectangle(new SD.RectangleF(x, y, w, h), r, r), B(c, a));

    public void StrokeRound(float x, float y, float w, float h, float r, SD.Color c, float width = 1f, float a = 1f)
    {
        var half = width / 2f;
        Rt.DrawRoundedRectangle(new RoundedRectangle(new SD.RectangleF(x + half, y + half, w - width, h - width), Math.Max(0, r - half), Math.Max(0, r - half)), B(c, a), width);
    }

    public void FillCircle(float cx, float cy, float r, SD.Color c, float a = 1f) =>
        Rt.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), B(c, a));

    public void StrokeCircle(float cx, float cy, float r, SD.Color c, float width, float a = 1f) =>
        Rt.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), B(c, a), width);

    public void Line(float x0, float y0, float x1, float y1, SD.Color c, float width, float a = 1f) =>
        Rt.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), B(c, a), width, _round);

    public void Polyline(SD.Color c, float width, params Vector2[] pts)
    {
        for (var i = 0; i + 1 < pts.Length; i++) Line(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, c, width);
    }

    /// <summary>Дуга по часовой стрелке (углы в градусах, 0 — вправо), с круглыми концами.</summary>
    public void Arc(float cx, float cy, float r, float startDeg, float sweepDeg, SD.Color c, float width)
    {
        Vector2 P(float deg) => new(cx + r * MathF.Cos(deg * MathF.PI / 180f), cy + r * MathF.Sin(deg * MathF.PI / 180f));
        using var geo = Fx.D2D.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(P(startDeg), FigureBegin.Hollow);
            sink.AddArc(new ArcSegment
            {
                Point = P(startDeg + sweepDeg), Size = new Vortice.Mathematics.Size(r, r), RotationAngle = 0,
                SweepDirection = SweepDirection.Clockwise, ArcSize = sweepDeg > 180 ? ArcSize.Large : ArcSize.Small,
            });
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        Rt.DrawGeometry(geo, B(c), width, _round);
    }

    public void Text(TextBlock tb, float x, float y, SD.Color c, float maxW, bool wrap = false, bool ellipsis = false, bool center = false, float a = 1f)
    {
        if (tb.IsEmpty) return;
        Rt.DrawTextLayout(new Vector2(x, y), tb.Layout(maxW, wrap, ellipsis, center), B(c, a));
    }

    public void Text(IDWriteTextLayout layout, float x, float y, SD.Color c, float a = 1f) =>
        Rt.DrawTextLayout(new Vector2(x, y), layout, B(c, a));

    public void Clip(float x, float y, float w, float h) =>
        Rt.PushAxisAlignedClip(new Rect(x, y, w, h), AntialiasMode.Aliased);

    public void Unclip() => Rt.PopAxisAlignedClip();

    public Matrix3x2 Translate(float dx, float dy)
    {
        var old = Rt.Transform;
        Rt.Transform = Matrix3x2.CreateTranslation(dx, dy) * old;
        return old;
    }

    public void Restore(Matrix3x2 m) => Rt.Transform = m;
}
