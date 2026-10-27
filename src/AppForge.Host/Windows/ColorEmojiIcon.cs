using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// 用 Segoe UI Emoji 的 COLR/CPAL 彩色字形绘制应用图标。
    /// GDI+ 的 DrawString 只会画单色轮廓，和界面里的彩色 emoji 不一致，
    /// 因此这里直接解析字体里的彩色图层并栅格化。
    /// </summary>
    internal static class ColorEmojiIcon
    {
        private static readonly object Sync = new object();
        private static EmojiFont _font;
        private static bool _loadTried;

        public static Icon Render(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                EmojiFont font;
                lock (Sync)
                {
                    if (!_loadTried)
                    {
                        _loadTried = true;
                        try { _font = EmojiFont.Load(); }
                        catch { _font = null; }
                    }
                    font = _font;
                }
                if (font == null) return null;

                var cp = FirstCodepoint(text);
                if (cp <= 0) return null;

                var glyph = font.MapCodepoint(cp);
                if (glyph <= 0 || !font.HasColorPaint(glyph)) return null;

                var bounds = font.GetPaintBounds(glyph);
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;

                var dark = IsDarkTheme();
                var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
                var frames = new List<IconFrame>(sizes.Length);
                foreach (var size in sizes)
                {
                    var bmp = RenderBitmap(font, glyph, bounds, size, dark);
                    if (bmp == null) return null;
                    using (bmp)
                        frames.Add(new IconFrame(size, BuildDibFrame(bmp, size)));
                }
                return BuildIcon(frames);
            }
            catch
            {
                return null;
            }
        }

        private static int FirstCodepoint(string text)
        {
            text = text.Trim();
            for (var i = 0; i < text.Length; i++)
            {
                int cp;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length &&
                    char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else
                {
                    cp = text[i];
                }
                if (cp == 0xFE0E || cp == 0xFE0F) continue;
                return cp;
            }
            return 0;
        }

        private static bool IsDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("AppsUseLightTheme");
                        if (value is int) return (int)value == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        private static Bitmap RenderBitmap(EmojiFont font, int glyph, RectangleF bounds,
                                           int size, bool dark)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.Clear(Color.Transparent);

                    // 与启动台应用卡片一致：浅色 #ECEEF2 / 深色 #21252C 的圆角底。
                    using (var tile = RoundedRect(new RectangleF(0, 0, size, size), size * 0.28f))
                    using (var bg = new SolidBrush(dark
                        ? Color.FromArgb(33, 37, 44)
                        : Color.FromArgb(236, 238, 242)))
                    {
                        g.FillPath(bg, tile);
                    }

                    // 卡片里的 emoji 大约占缩略图的一半；图标略放大到 66%，小尺寸仍可辨认。
                    var art = size * 0.66f;
                    var scale = Math.Min(art / bounds.Width, art / bounds.Height);
                    var cx = bounds.X + bounds.Width / 2f;
                    var cy = bounds.Y + bounds.Height / 2f;
                    var matrix = new Affine(scale, 0f, 0f, -scale,
                        size / 2f - cx * scale,
                        size / 2f + cy * scale);

                    var context = new PaintContext(g, size, dark ? Color.White : Color.Black, bounds);
                    font.DrawColorGlyph(glyph, matrix, 0, context);
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }

        private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var d = Math.Max(1f, Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height)));
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Icon BuildIcon(IList<IconFrame> frames)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms, Encoding.UTF8))
            {
                writer.Write((ushort)0);          // reserved
                writer.Write((ushort)1);          // type = icon
                writer.Write((ushort)frames.Count);

                var offset = 6 + 16 * frames.Count;
                foreach (var frame in frames)
                {
                    writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
                    writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
                    writer.Write((byte)0);        // color count
                    writer.Write((byte)0);        // reserved
                    writer.Write((ushort)1);      // planes
                    writer.Write((ushort)32);     // bit count
                    writer.Write((uint)frame.Data.Length);
                    writer.Write((uint)offset);
                    offset += frame.Data.Length;
                }
                foreach (var frame in frames) writer.Write(frame.Data);
                writer.Flush();
                ms.Position = 0;
                using (var icon = new Icon(ms))
                    return (Icon)icon.Clone();
            }
        }

        private static byte[] BuildDibFrame(Bitmap bitmap, int size)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, size, size),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride;
                var xorSize = size * size * 4;
                var andStride = ((size + 31) / 32) * 4;
                var andSize = andStride * size;
                var bytes = new byte[40 + xorSize + andSize];
                using (var ms = new MemoryStream(bytes))
                using (var writer = new BinaryWriter(ms))
                {
                    writer.Write(40);             // biSize
                    writer.Write(size);           // biWidth
                    writer.Write(size * 2);       // biHeight = XOR + AND
                    writer.Write((ushort)1);      // biPlanes
                    writer.Write((ushort)32);     // biBitCount
                    writer.Write(0);              // BI_RGB
                    writer.Write(xorSize);        // biSizeImage
                    writer.Write(0);              // biXPelsPerMeter
                    writer.Write(0);              // biYPelsPerMeter
                    writer.Write(0);              // biClrUsed
                    writer.Write(0);              // biClrImportant

                    var row = new byte[size * 4];
                    for (var y = size - 1; y >= 0; y--)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, row.Length);
                        writer.Write(row);
                    }

                    var mask = new byte[andSize];
                    for (var y = 0; y < size; y++)
                    {
                        for (var x = 0; x < size; x++)
                        {
                            var alpha = Marshal.ReadByte(IntPtr.Add(
                                data.Scan0, y * stride + x * 4 + 3));
                            if (alpha == 0)
                                mask[y * andStride + (x / 8)] |= (byte)(0x80 >> (x % 8));
                        }
                    }
                    writer.Write(mask);
                }
                return bytes;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private sealed class IconFrame
        {
            public readonly int Size;
            public readonly byte[] Data;

            public IconFrame(int size, byte[] data)
            {
                Size = size;
                Data = data;
            }
        }

        internal struct Affine
        {
            public readonly float A;
            public readonly float B;
            public readonly float C;
            public readonly float D;
            public readonly float E;
            public readonly float F;

            public Affine(float a, float b, float c, float d, float e, float f)
            {
                A = a; B = b; C = c; D = d; E = e; F = f;
            }

            public static Affine Identity
            {
                get { return new Affine(1f, 0f, 0f, 1f, 0f, 0f); }
            }

            /// <summary>先应用 child，再应用 this。</summary>
            public Affine Compose(Affine child)
            {
                return new Affine(
                    A * child.A + C * child.B,
                    B * child.A + D * child.B,
                    A * child.C + C * child.D,
                    B * child.C + D * child.D,
                    A * child.E + C * child.F + E,
                    B * child.E + D * child.F + F);
            }

            public Matrix ToMatrix()
            {
                return new Matrix(A, B, C, D, E, F);
            }

            public PointF Apply(float x, float y)
            {
                return new PointF(A * x + C * y + E, B * x + D * y + F);
            }
        }

        internal sealed class PaintContext
        {
            public readonly Graphics G;
            public readonly int Size;
            public readonly Color Foreground;
            public readonly RectangleF ArtBounds;

            public PaintContext(Graphics graphics, int size, Color foreground, RectangleF artBounds)
            {
                G = graphics;
                Size = size;
                Foreground = foreground;
                ArtBounds = artBounds;
            }
        }
    }
}
