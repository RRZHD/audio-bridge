using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AudioBridge.Server.Ui;

public enum ThemeMode { Auto, Light, Dark }

/// <summary>Палитра: те же значения, что в консоли менеджера и в приложении для телефона.</summary>
public sealed class Pal
{
    public bool Dark { get; init; }
    public Color Bg { get; init; }
    public Color Surface { get; init; }
    public Color Text { get; init; }
    public Color Muted { get; init; }
    public Color Border { get; init; }
    public Color Accent { get; init; }
    public Color AccentHover { get; init; }
    public Color Raise { get; init; }

    public Color AccentSoft => Skin.Blend(Accent, Surface, Dark ? 0.18f : 0.08f);
    public Color Success => Dark ? Color.FromArgb(0x4C, 0xC2, 0x83) : Color.FromArgb(0x2F, 0x9E, 0x5B);
    public Color Warning => Dark ? Color.FromArgb(0xE0, 0xA5, 0x26) : Color.FromArgb(0xD9, 0x93, 0x0D);
    public Color Danger => Dark ? Color.FromArgb(0xEF, 0x5B, 0x55) : Color.FromArgb(0xD6, 0x45, 0x45);
    public Color Info => Dark ? Color.FromArgb(0x6F, 0xB1, 0xFF) : Color.FromArgb(0x1F, 0x7A, 0xE0);

    public Color Soft(Color tone) => Skin.Blend(tone, Surface, Dark ? 0.22f : 0.14f);
    public Color Ink(Color tone) => Skin.Blend(tone, Text, 0.85f);
    public Color ChipBg => Skin.Blend(Text, Surface, 0.08f);
    public Color Line => Skin.Blend(Text, Surface, 0.08f);

    public static readonly Pal Light = new()
    {
        Dark = false,
        Bg = Color.FromArgb(0xF2, 0xF3, 0xF5), Surface = Color.White, Text = Color.FromArgb(0x22, 0x22, 0x22),
        Muted = Color.FromArgb(0x8A, 0x8A, 0x8A), Border = Color.FromArgb(0xE4, 0xE4, 0xE7),
        Accent = Color.FromArgb(0xDC, 0x2F, 0x2F), AccentHover = Color.FromArgb(0xC7, 0x29, 0x29),
        Raise = Color.FromArgb(0xF2, 0xF3, 0xF5),
    };

    public static readonly Pal DarkPal = new()
    {
        Dark = true,
        Bg = Color.FromArgb(0x17, 0x17, 0x1B), Surface = Color.FromArgb(0x1F, 0x1F, 0x25), Text = Color.FromArgb(0xE9, 0xE9, 0xEE),
        Muted = Color.FromArgb(0x9A, 0x9A, 0xA6), Border = Color.FromArgb(0x33, 0x33, 0x3D),
        Accent = Color.FromArgb(0xDC, 0x2F, 0x2F), AccentHover = Color.FromArgb(0xC7, 0x29, 0x29),
        Raise = Color.FromArgb(0x2B, 0x2B, 0x33),
    };
}

/// <summary>Тема приложения: светлая/тёмная/как в Windows. Все элементы читают цвета отсюда при отрисовке.</summary>
public static class Skin
{
    public static Pal P { get; private set; } = Pal.Light;
    public static ThemeMode Mode { get; private set; } = ThemeMode.Auto;
    public static event Action? Changed;

    public const int Radius = 12;
    public const int RadiusSm = 8;

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        var dark = mode == ThemeMode.Dark || (mode == ThemeMode.Auto && SystemUsesDark());
        P = dark ? Pal.DarkPal : Pal.Light;
        Changed?.Invoke();
    }

    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Color Blend(Color top, Color bottom, float alpha)
    {
        int Mix(int a, int b) => (int)Math.Round(a * alpha + b * (1 - alpha));
        return Color.FromArgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static readonly Dictionary<(float, FontStyle), Font> Fonts = new();
    private static string? _family;

    private static string Family
    {
        get
        {
            if (_family != null) return _family;
            using var probe = new System.Drawing.Text.InstalledFontCollection();
            var has = Array.Exists(probe.Families, f => f.Name == "Segoe UI Variable Text");
            return _family = has ? "Segoe UI Variable Text" : "Segoe UI";
        }
    }

    /// <summary>Шрифт интерфейса; кэшируется (размер в пунктах).</summary>
    public static Font Font(float pt, FontStyle style = FontStyle.Regular)
    {
        if (!Fonts.TryGetValue((pt, style), out var f)) Fonts[(pt, style)] = f = new Font(Family, pt, style, GraphicsUnit.Point);
        return f;
    }

    public static Font Mono(float pt = 9f) => new("Cascadia Mono", pt, FontStyle.Regular, GraphicsUnit.Point);

    public static float Scale(Control c) => c.DeviceDpi / 96f;

    // ---- заголовок окна и полосы прокрутки в тёмной теме ----------------------------------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string? subIdList);

    public static void StyleWindow(Form form)
    {
        try
        {
            var dark = P.Dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { /* older Windows */ }
    }

    public static void StyleScroll(Control c)
    {
        try { SetWindowTheme(c.Handle, P.Dark ? "DarkMode_Explorer" : "Explorer", null); }
        catch { /* ignore */ }
    }
}
