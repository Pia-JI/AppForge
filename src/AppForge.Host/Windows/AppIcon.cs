using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// 统一提供应用图标：窗口标题栏、任务栏和托盘必须使用同一枚 AF 图标，
    /// 不能再用 SystemIcons.Application 的默认空白程序图标。
    /// </summary>
    internal static class AppIcon
    {
        private static readonly object Sync = new object();
        private static Icon _large;
        private static Icon _small;
        private static readonly Dictionary<string, Icon> AppIcons =
            new Dictionary<string, Icon>(StringComparer.Ordinal);

        public static Icon Large
        {
            get
            {
                lock (Sync)
                {
                    if (_large == null) _large = Load(SystemInformation.IconSize.Width);
                    return _large;
                }
            }
        }

        public static Icon Small
        {
            get
            {
                lock (Sync)
                {
                    if (_small == null) _small = Load(SystemInformation.SmallIconSize.Width);
                    return _small;
                }
            }
        }

        /// <summary>
        /// 生成应用窗口图标：使用应用自己的 emoji，而不是统一的 AF 图标。
        /// 同一个 emoji 只渲染一次，供标题栏与任务栏共用。
        /// </summary>
        public static Icon ForApp(string emoji)
        {
            var glyph = FirstTextElement(emoji);
            if (string.IsNullOrEmpty(glyph)) glyph = "🧩";

            lock (Sync)
            {
                Icon icon;
                if (AppIcons.TryGetValue(glyph, out icon)) return icon;

                icon = RenderAppIcon(glyph);
                AppIcons[glyph] = icon;
                return icon;
            }
        }

        private static string FirstTextElement(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var trimmed = text.Trim();
            var enumerator = StringInfo.GetTextElementEnumerator(trimmed);
            return enumerator.MoveNext() ? enumerator.GetTextElement() : string.Empty;
        }

        private static Icon RenderAppIcon(string glyph)
        {
            // 优先使用 Segoe UI Emoji 的 COLR v1 彩色字形；失败时回退到下面的单色绘制。
            var color = ColorEmojiIcon.Render(glyph);
            if (color != null) return color;

            const int size = 64;
            using (var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);

                // 圆角品牌底色让浅色/深色任务栏都能看清；emoji 本身保持应用里的同一个字符。
                var accent = AccentFor(glyph);
                using (var path = RoundedRect(new RectangleF(3, 3, size - 6, size - 6), 15))
                using (var brush = new LinearGradientBrush(
                    new RectangleF(0, 0, size, size), accent, Darken(accent, 0.18f), 45f))
                {
                    graphics.FillPath(brush, path);
                }

                using (var font = ChooseEmojiFont(glyph, 41f))
                using (var format = new StringFormat(StringFormat.GenericTypographic))
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    using (var white = new SolidBrush(Color.White))
                        graphics.DrawString(glyph, font, white, new RectangleF(0, 1, size, size), format);
                }

                var handle = bitmap.GetHicon();
                try
                {
                    using (var temp = Icon.FromHandle(handle))
                        return (Icon)temp.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static Font ChooseEmojiFont(string glyph, float size)
        {
            var families = new[] { "Segoe UI Emoji", "Microsoft YaHei UI", "Segoe UI Symbol", "Segoe UI" };
            foreach (var family in families)
            {
                try
                {
                    var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
                    if (font.Name.Equals(family, StringComparison.OrdinalIgnoreCase)) return font;
                    font.Dispose();
                }
                catch { /* 换下一个字体 */ }
            }
            return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static Color AccentFor(string glyph)
        {
            var hash = 0;
            for (var i = 0; i < glyph.Length; i++) hash = unchecked(hash * 31 + glyph[i]);
            var palette = new[]
            {
                Color.FromArgb(91, 75, 239),   // 品牌紫
                Color.FromArgb(47, 111, 237),  // 蓝
                Color.FromArgb(28, 155, 126),  // 绿
                Color.FromArgb(224, 122, 43),  // 橙
                Color.FromArgb(201, 74, 125)   // 玫红
            };
            return palette[(hash & int.MaxValue) % palette.Length];
        }

        private static Color Darken(Color color, float amount)
        {
            var factor = 1f - Math.Max(0f, Math.Min(0.8f, amount));
            return Color.FromArgb(color.A,
                (int)(color.R * factor),
                (int)(color.G * factor),
                (int)(color.B * factor));
        }

        private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Icon Load(int size)
        {
            if (size < 16) size = 16;
            try
            {
                var path = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(path))
                {
                    // Extract the icon at the exact pixel size the shell asks for (16 at 100%,
                    // 24/32/48 at higher DPI). Icon.ExtractAssociatedIcon always returns 32x32,
                    // which looks soft when Windows scales it for a 150% taskbar.
                    var handles = new IntPtr[1];
                    var ids = new uint[1];
                    var count = PrivateExtractIcons(path, 0, size, size, handles, ids, 1, 0);
                    if (count > 0 && handles[0] != IntPtr.Zero)
                    {
                        try
                        {
                            using (var temp = Icon.FromHandle(handles[0]))
                                return (Icon)temp.Clone();
                        }
                        finally { DestroyIcon(handles[0]); }
                    }

                    var icon = Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        if (icon.Width == size) return icon;
                        using (icon) return new Icon(icon, new Size(size, size));
                    }
                }
            }
            catch { /* 回退到系统图标 */ }

            return (Icon)SystemIcons.Application.Clone();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
                                                        IntPtr[] icons, uint[] ids, uint count, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
