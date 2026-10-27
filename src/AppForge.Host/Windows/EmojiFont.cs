using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// 只读解析 Segoe UI Emoji 的 TrueType 表：cmap / loca / glyf / COLR v1 / CPAL。
    /// 不依赖 GDI+ 的字体渲染，因此能拿到彩色图层；解析失败时由调用方回退单色图标。
    /// </summary>
    internal sealed class EmojiFont
    {
        private readonly byte[] _data;
        private readonly Dictionary<string, TableInfo> _tables;
        private readonly int _unitsPerEm;
        private readonly int _indexToLocFormat;
        private readonly int _numGlyphs;
        private readonly int[] _loca;
        private readonly TableInfo _glyf;
        private readonly Dictionary<int, GraphicsPath> _pathCache =
            new Dictionary<int, GraphicsPath>();

        private int _cmapFormat;
        private int _cmapOffset;
        private int _numPaletteEntries;
        private int _paletteStart;
        private int _colorRecordsOffset;
        private Dictionary<int, int> _baseGlyphs;
        private int[] _layerOffsets;

        private struct TableInfo
        {
            public int Offset;
            public int Length;
        }

        public static EmojiFont Load()
        {
            var path = string.Empty;
            try
            {
                var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (!string.IsNullOrEmpty(fonts))
                    path = Path.Combine(fonts, "seguiemj.ttf");
            }
            catch { }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                try
                {
                    var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (!string.IsNullOrEmpty(win))
                        path = Path.Combine(win, "Fonts", "seguiemj.ttf");
                }
                catch { }
            }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return new EmojiFont(File.ReadAllBytes(path));
        }

        private EmojiFont(byte[] data)
        {
            _data = data;
            if (data.Length < 12) throw new InvalidDataException("字体文件过短");

            _tables = new Dictionary<string, TableInfo>(StringComparer.Ordinal);
            var count = U16(4);
            for (var i = 0; i < count; i++)
            {
                var rec = 12 + i * 16;
                if (rec + 16 > data.Length) break;
                var tag = Encoding.ASCII.GetString(data, rec, 4);
                var offset = (int)U32(rec + 8);
                var length = (int)U32(rec + 12);
                if (offset < 0 || length < 0 || offset > data.Length ||
                    offset + length > data.Length) continue;
                _tables[tag] = new TableInfo { Offset = offset, Length = length };
            }

            TableInfo head, maxp, loca, cmap, cpal, colr;
            if (!_tables.TryGetValue("head", out head) ||
                !_tables.TryGetValue("maxp", out maxp) ||
                !_tables.TryGetValue("loca", out loca) ||
                !_tables.TryGetValue("cmap", out cmap) ||
                !_tables.TryGetValue("glyf", out _glyf) ||
                !_tables.TryGetValue("COLR", out colr) ||
                !_tables.TryGetValue("CPAL", out cpal))
            {
                throw new InvalidDataException("不是可用的彩色 emoji 字体");
            }

            _unitsPerEm = U16(head.Offset + 18);
            _indexToLocFormat = I16(head.Offset + 50);
            _numGlyphs = U16(maxp.Offset + 4);
            if (_unitsPerEm <= 0 || _numGlyphs <= 0) throw new InvalidDataException("字体头无效");

            _loca = new int[_numGlyphs + 1];
            for (var i = 0; i <= _numGlyphs; i++)
            {
                _loca[i] = _indexToLocFormat == 0
                    ? U16(loca.Offset + i * 2) * 2
                    : (int)U32(loca.Offset + i * 4);
            }

            ReadCmap(cmap);
            ReadCpal(cpal);
            ReadColr(colr);
            if (_cmapFormat == 0 || _baseGlyphs.Count == 0)
                throw new InvalidDataException("字体缺少彩色字形");
        }

        public bool HasColorPaint(int glyph)
        {
            return _baseGlyphs.ContainsKey(glyph);
        }

        private void ReadCmap(TableInfo table)
        {
            var count = U16(table.Offset + 2);
            var best = -1;
            for (var i = 0; i < count; i++)
            {
                var rec = table.Offset + 4 + i * 8;
                var platform = U16(rec);
                var encoding = U16(rec + 2);
                var relative = (int)U32(rec + 4);
                if (relative <= 0 || table.Offset + relative + 4 > _data.Length) continue;
                var format = U16(table.Offset + relative);
                if (format != 4 && format != 12) continue;

                var score = 10;
                if (platform == 3 && encoding == 10) score = 100;
                else if (platform == 0 && (encoding == 4 || encoding == 6)) score = 90;
                else if (platform == 3 && encoding == 1) score = 80;
                else if (platform == 0) score = 70;

                if (score > best)
                {
                    best = score;
                    _cmapFormat = format;
                    _cmapOffset = table.Offset + relative;
                }
            }
        }

        private void ReadCpal(TableInfo table)
        {
            _numPaletteEntries = U16(table.Offset + 2);
            var numPalettes = U16(table.Offset + 4);
            _colorRecordsOffset = table.Offset + (int)U32(table.Offset + 8);
            _paletteStart = numPalettes > 0 ? U16(table.Offset + 12) : 0;
            if (_numPaletteEntries <= 0) throw new InvalidDataException("CPAL 为空");
        }

        private void ReadColr(TableInfo table)
        {
            _baseGlyphs = new Dictionary<int, int>();
            _layerOffsets = new int[0];
            if (U16(table.Offset) != 1) return;

            var baseList = table.Offset + (int)U32(table.Offset + 14);
            var layerList = table.Offset + (int)U32(table.Offset + 18);
            if (baseList <= 0 || baseList + 4 > _data.Length) return;

            var baseCount = (int)U32(baseList);
            for (var i = 0; i < baseCount; i++)
            {
                var rec = baseList + 4 + i * 6;
                if (rec + 6 > _data.Length) break;
                var glyph = U16(rec);
                var relative = (int)U32(rec + 2);
                if (relative > 0 && baseList + relative < _data.Length)
                    _baseGlyphs[glyph] = baseList + relative;
            }

            if (layerList <= 0 || layerList + 4 > _data.Length) return;
            var layerCount = (int)U32(layerList);
            if (layerCount < 0 || layerList + 4 + layerCount * 4 > _data.Length) return;
            _layerOffsets = new int[layerCount];
            for (var i = 0; i < layerCount; i++)
            {
                var relative = (int)U32(layerList + 4 + i * 4);
                _layerOffsets[i] = layerList + relative;
            }
        }

        public int MapCodepoint(int codepoint)
        {
            if (_cmapFormat == 12) return MapFormat12(codepoint);
            if (_cmapFormat == 4) return MapFormat4(codepoint);
            return 0;
        }

        private int MapFormat12(int codepoint)
        {
            var count = (int)U32(_cmapOffset + 12);
            var groups = _cmapOffset + 16;
            var lo = 0;
            var hi = count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var rec = groups + mid * 12;
                if (rec + 12 > _data.Length) return 0;
                var start = (int)U32(rec);
                var end = (int)U32(rec + 4);
                if (codepoint < start) hi = mid - 1;
                else if (codepoint > end) lo = mid + 1;
                else return (int)U32(rec + 8) + (codepoint - start);
            }
            return 0;
        }

        private int MapFormat4(int codepoint)
        {
            if (codepoint > 0xFFFF) return 0;
            var segCount = U16(_cmapOffset + 6) / 2;
            var endCodes = _cmapOffset + 14;
            var startCodes = endCodes + segCount * 2 + 2;
            var deltas = startCodes + segCount * 2;
            var ranges = deltas + segCount * 2;

            for (var i = 0; i < segCount; i++)
            {
                var end = U16(endCodes + i * 2);
                if (end < codepoint) continue;
                var start = U16(startCodes + i * 2);
                if (start > codepoint) return 0;
                var delta = I16(deltas + i * 2);
                var rangeOffset = U16(ranges + i * 2);
                if (rangeOffset == 0) return (codepoint + delta) & 0xFFFF;
                var glyphAt = ranges + i * 2 + rangeOffset + (codepoint - start) * 2;
                if (glyphAt + 2 > _data.Length) return 0;
                var glyph = U16(glyphAt);
                return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
            }
            return 0;
        }

        public RectangleF GetPaintBounds(int glyph)
        {
            int paint;
            if (!_baseGlyphs.TryGetValue(glyph, out paint)) return RectangleF.Empty;
            var bounds = RectangleF.Empty;
            var any = false;
            TraverseBounds(paint, ColorEmojiIcon.Affine.Identity, 0, ref bounds, ref any);
            return any ? bounds : RectangleF.Empty;
        }

        private void TraverseBounds(int offset, ColorEmojiIcon.Affine matrix, int depth,
                                    ref RectangleF bounds, ref bool any)
        {
            if (depth > 64 || !ValidOffset(offset)) return;
            var format = U8(offset);

            if (format == 1)
            {
                var count = U8(offset + 1);
                var first = (int)U32(offset + 2);
                for (var i = 0; i < count; i++)
                {
                    var index = first + i;
                    if (index >= 0 && index < _layerOffsets.Length)
                        TraverseBounds(_layerOffsets[index], matrix, depth + 1, ref bounds, ref any);
                }
                return;
            }

            if (format == 10)
            {
                var glyphId = U16(offset + 4);
                AddTransformedBounds(GetGlyphBounds(glyphId), matrix, ref bounds, ref any);
                TraverseBounds(offset + (int)U24(offset + 1), matrix, depth + 1, ref bounds, ref any);
                return;
            }

            if (format == 32)
            {
                if (offset + 5 <= _data.Length)
                {
                    TraverseBounds(offset + (int)U24(offset + 1), matrix, depth + 1,
                        ref bounds, ref any);
                    TraverseBounds(offset + (int)U24(offset + 5), matrix, depth + 1,
                        ref bounds, ref any);
                }
                return;
            }

            int child;
            ColorEmojiIcon.Affine transform;
            if (!TryReadChild(offset, out child, out transform)) return;
            TraverseBounds(child, matrix.Compose(transform), depth + 1, ref bounds, ref any);
        }

        private RectangleF GetGlyphBounds(int glyph)
        {
            if (glyph < 0 || glyph >= _numGlyphs) return RectangleF.Empty;
            var start = _loca[glyph];
            var end = _loca[glyph + 1];
            if (end <= start) return RectangleF.Empty;
            var offset = _glyf.Offset + start;
            if (offset + 10 > _glyf.Offset + _glyf.Length) return RectangleF.Empty;
            var xMin = I16(offset + 2);
            var yMin = I16(offset + 4);
            var xMax = I16(offset + 6);
            var yMax = I16(offset + 8);
            if (xMax <= xMin || yMax <= yMin) return RectangleF.Empty;
            return new RectangleF(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static void AddTransformedBounds(RectangleF rect, ColorEmojiIcon.Affine matrix,
                                                 ref RectangleF bounds, ref bool any)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var points = new[]
            {
                matrix.Apply(rect.Left, rect.Top),
                matrix.Apply(rect.Right, rect.Top),
                matrix.Apply(rect.Left, rect.Bottom),
                matrix.Apply(rect.Right, rect.Bottom)
            };
            var minX = points[0].X;
            var maxX = points[0].X;
            var minY = points[0].Y;
            var maxY = points[0].Y;
            for (var i = 1; i < points.Length; i++)
            {
                if (points[i].X < minX) minX = points[i].X;
                if (points[i].X > maxX) maxX = points[i].X;
                if (points[i].Y < minY) minY = points[i].Y;
                if (points[i].Y > maxY) maxY = points[i].Y;
            }
            var next = RectangleF.FromLTRB(minX, minY, maxX, maxY);
            if (!any)
            {
                bounds = next;
                any = true;
            }
            else
            {
                bounds = RectangleF.FromLTRB(
                    Math.Min(bounds.Left, next.Left),
                    Math.Min(bounds.Top, next.Top),
                    Math.Max(bounds.Right, next.Right),
                    Math.Max(bounds.Bottom, next.Bottom));
            }
        }

        private bool TryReadChild(int offset, out int child, out ColorEmojiIcon.Affine transform)
        {
            child = 0;
            transform = ColorEmojiIcon.Affine.Identity;
            if (offset + 4 > _data.Length) return false;
            var format = U8(offset);
            switch (format)
            {
                case 10:
                case 14:
                case 16:
                case 18:
                case 20:
                case 22:
                case 24:
                case 26:
                case 28:
                case 30:
                    child = offset + (int)U24(offset + 1);
                    break;
                case 12:
                    child = offset + (int)U24(offset + 1);
                    var t = offset + (int)U24(offset + 4);
                    if (!ValidOffset(t) || t + 24 > _data.Length) return false;
                    transform = new ColorEmojiIcon.Affine(
                        Fixed(t), Fixed(t + 4), Fixed(t + 8),
                        Fixed(t + 12), Fixed(t + 16), Fixed(t + 20));
                    return true;
                default:
                    return false;
            }

            switch (format)
            {
                case 14:
                    transform = new ColorEmojiIcon.Affine(1, 0, 0, 1, I16(offset + 4), I16(offset + 6));
                    break;
                case 16:
                    transform = new ColorEmojiIcon.Affine(
                        F2Dot14(offset + 4), 0, 0, F2Dot14(offset + 6), 0, 0);
                    break;
                case 18:
                {
                    var sx = F2Dot14(offset + 4);
                    var sy = F2Dot14(offset + 6);
                    var cx = I16(offset + 8);
                    var cy = I16(offset + 10);
                    transform = new ColorEmojiIcon.Affine(sx, 0, 0, sy, cx - sx * cx, cy - sy * cy);
                    break;
                }
                case 20:
                {
                    var s = F2Dot14(offset + 4);
                    transform = new ColorEmojiIcon.Affine(s, 0, 0, s, 0, 0);
                    break;
                }
                case 22:
                {
                    var s = F2Dot14(offset + 4);
                    var cx = I16(offset + 6);
                    var cy = I16(offset + 8);
                    transform = new ColorEmojiIcon.Affine(s, 0, 0, s, cx - s * cx, cy - s * cy);
                    break;
                }
                case 24:
                    transform = Rotate(F2Dot14(offset + 4), 0f, 0f);
                    break;
                case 26:
                    transform = Rotate(F2Dot14(offset + 4), I16(offset + 6), I16(offset + 8));
                    break;
                case 28:
                    transform = Skew(F2Dot14(offset + 4), F2Dot14(offset + 6), 0f, 0f);
                    break;
                case 30:
                    transform = Skew(F2Dot14(offset + 4), F2Dot14(offset + 6),
                        I16(offset + 8), I16(offset + 10));
                    break;
            }
            return ValidOffset(child);
        }

        private static ColorEmojiIcon.Affine Rotate(float degrees, float cx, float cy)
        {
            var radians = degrees * (float)Math.PI / 180f;
            var cos = (float)Math.Cos(radians);
            var sin = (float)Math.Sin(radians);
            return new ColorEmojiIcon.Affine(cos, sin, -sin, cos,
                cx - cos * cx + sin * cy,
                cy - sin * cx - cos * cy);
        }

        private static ColorEmojiIcon.Affine Skew(float xDegrees, float yDegrees, float cx, float cy)
        {
            var tx = (float)Math.Tan(xDegrees * Math.PI / 180.0);
            var ty = (float)Math.Tan(yDegrees * Math.PI / 180.0);
            return new ColorEmojiIcon.Affine(1, ty, tx, 1,
                -tx * cy,
                -ty * cx);
        }

        private bool ValidOffset(int offset)
        {
            return offset > 0 && offset < _data.Length;
        }

        // ---------------- 字形轮廓 ----------------

        public GraphicsPath GetGlyphPath(int glyph)
        {
            GraphicsPath cached;
            if (_pathCache.TryGetValue(glyph, out cached)) return cached;
            var path = BuildGlyphPath(glyph, 0);
            _pathCache[glyph] = path;
            return path;
        }

        private GraphicsPath BuildGlyphPath(int glyph, int depth)
        {
            if (depth > 8 || glyph < 0 || glyph >= _numGlyphs) return null;
            var start = _loca[glyph];
            var end = _loca[glyph + 1];
            if (end <= start) return null;
            var offset = _glyf.Offset + start;
            var limit = _glyf.Offset + _glyf.Length;
            if (offset + 10 > limit) return null;
            var contours = I16(offset);
            if (contours == 0) return null;
            if (contours > 0) return BuildSimpleGlyph(offset, contours, limit);
            return BuildCompositeGlyph(offset, depth, limit);
        }

        private GraphicsPath BuildSimpleGlyph(int offset, int contours, int limit)
        {
            var p = offset + 10;
            if (p + contours * 2 + 2 > limit) return null;
            var endPoints = new int[contours];
            for (var i = 0; i < contours; i++)
            {
                endPoints[i] = U16(p);
                p += 2;
            }
            var pointCount = endPoints[contours - 1] + 1;
            if (pointCount <= 0 || pointCount > 200000) return null;

            var instructionLength = U16(p);
            p += 2 + instructionLength;
            if (p > limit) return null;

            var flags = new byte[pointCount];
            for (var i = 0; i < pointCount;)
            {
                if (p >= limit) return null;
                var flag = U8(p++);
                flags[i++] = flag;
                if ((flag & 0x08) != 0)
                {
                    if (p >= limit) return null;
                    var repeat = U8(p++);
                    for (var j = 0; j < repeat && i < pointCount; j++)
                        flags[i++] = flag;
                }
            }

            var xs = new int[pointCount];
            var x = 0;
            for (var i = 0; i < pointCount; i++)
            {
                var flag = flags[i];
                if ((flag & 0x02) != 0)
                {
                    if (p >= limit) return null;
                    var delta = U8(p++);
                    x += (flag & 0x10) != 0 ? delta : -delta;
                }
                else if ((flag & 0x10) == 0)
                {
                    if (p + 2 > limit) return null;
                    x += I16(p);
                    p += 2;
                }
                xs[i] = x;
            }

            var ys = new int[pointCount];
            var y = 0;
            for (var i = 0; i < pointCount; i++)
            {
                var flag = flags[i];
                if ((flag & 0x04) != 0)
                {
                    if (p >= limit) return null;
                    var delta = U8(p++);
                    y += (flag & 0x20) != 0 ? delta : -delta;
                }
                else if ((flag & 0x20) == 0)
                {
                    if (p + 2 > limit) return null;
                    y += I16(p);
                    p += 2;
                }
                ys[i] = y;
            }

            var path = new GraphicsPath(FillMode.Winding);
            var firstPoint = 0;
            for (var i = 0; i < contours; i++)
            {
                BuildContour(path, xs, ys, flags, firstPoint, endPoints[i]);
                firstPoint = endPoints[i] + 1;
            }
            return path.PointCount > 0 ? path : null;
        }

        private static void BuildContour(GraphicsPath path, int[] xs, int[] ys,
                                         byte[] flags, int first, int last)
        {
            var count = last - first + 1;
            if (count <= 0) return;

            var startIndex = -1;
            for (var i = 0; i < count; i++)
            {
                if ((flags[first + i] & 0x01) != 0)
                {
                    startIndex = i;
                    break;
                }
            }

            PointF start;
            if (startIndex >= 0)
            {
                start = new PointF(xs[first + startIndex], ys[first + startIndex]);
            }
            else
            {
                start = Mid(new PointF(xs[first], ys[first]),
                            new PointF(xs[first + 1], ys[first + 1]));
                startIndex = 0;
            }

            path.StartFigure();
            var current = start;
            var index = startIndex;
            var remaining = startIndex == 0 && (flags[first] & 0x01) == 0 ? count : count - 1;

            while (remaining > 0)
            {
                index = (index + 1) % count;
                var point = new PointF(xs[first + index], ys[first + index]);
                var onCurve = (flags[first + index] & 0x01) != 0;
                remaining--;

                if (onCurve)
                {
                    path.AddLine(current, point);
                    current = point;
                }
                else
                {
                    var nextIndex = (index + 1) % count;
                    var next = new PointF(xs[first + nextIndex], ys[first + nextIndex]);
                    var nextOnCurve = (flags[first + nextIndex] & 0x01) != 0;
                    if (!nextOnCurve && remaining > 0)
                    {
                        var mid = Mid(point, next);
                        AddQuad(path, current, point, mid);
                        current = mid;
                    }
                    else
                    {
                        AddQuad(path, current, point, next);
                        current = next;
                        if (remaining > 0)
                        {
                            index = nextIndex;
                            remaining--;
                        }
                    }
                }
            }
            path.CloseFigure();
        }

        private static PointF Mid(PointF a, PointF b)
        {
            return new PointF((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);
        }

        private static void AddQuad(GraphicsPath path, PointF p0, PointF control, PointF p1)
        {
            var c1 = new PointF(
                p0.X + (control.X - p0.X) * 2f / 3f,
                p0.Y + (control.Y - p0.Y) * 2f / 3f);
            var c2 = new PointF(
                p1.X + (control.X - p1.X) * 2f / 3f,
                p1.Y + (control.Y - p1.Y) * 2f / 3f);
            path.AddBezier(p0, c1, c2, p1);
        }

        private GraphicsPath BuildCompositeGlyph(int offset, int depth, int limit)
        {
            var path = new GraphicsPath(FillMode.Winding);
            var p = offset + 10;
            var more = true;
            var guard = 0;
            while (more && guard++ < 64)
            {
                if (p + 4 > limit) break;
                var flags = U16(p);
                var component = U16(p + 2);
                p += 4;

                int arg1;
                int arg2;
                if ((flags & 0x0001) != 0)
                {
                    if (p + 4 > limit) break;
                    arg1 = I16(p);
                    arg2 = I16(p + 2);
                    p += 4;
                }
                else
                {
                    if (p + 2 > limit) break;
                    arg1 = (sbyte)U8(p);
                    arg2 = (sbyte)U8(p + 1);
                    p += 2;
                }

                float a = 1f, b = 0f, c = 0f, d = 1f;
                if ((flags & 0x0008) != 0)
                {
                    if (p + 2 > limit) break;
                    a = d = F2Dot14(p);
                    p += 2;
                }
                else if ((flags & 0x0040) != 0)
                {
                    if (p + 4 > limit) break;
                    a = F2Dot14(p);
                    d = F2Dot14(p + 2);
                    p += 4;
                }
                else if ((flags & 0x0080) != 0)
                {
                    if (p + 8 > limit) break;
                    a = F2Dot14(p);
                    b = F2Dot14(p + 2);
                    c = F2Dot14(p + 4);
                    d = F2Dot14(p + 6);
                    p += 8;
                }

                var e = (flags & 0x0002) != 0 ? arg1 : 0f;
                var f = (flags & 0x0002) != 0 ? arg2 : 0f;
                var child = BuildGlyphPath(component, depth + 1);
                if (child != null && child.PointCount > 0)
                {
                    using (var clone = (GraphicsPath)child.Clone())
                    using (var matrix = new Matrix(a, b, c, d, e, f))
                    {
                        clone.Transform(matrix);
                        path.AddPath(clone, false);
                    }
                }
                more = (flags & 0x0020) != 0;
            }
            return path.PointCount > 0 ? path : null;
        }

        // ---------------- COLR v1 绘制 ----------------

        public void DrawColorGlyph(int glyph, ColorEmojiIcon.Affine matrix, int depth,
                                   ColorEmojiIcon.PaintContext context)
        {
            int offset;
            if (_baseGlyphs.TryGetValue(glyph, out offset))
                DrawPaint(offset, matrix, depth, context);
        }

        public void DrawPaint(int offset, ColorEmojiIcon.Affine matrix, int depth,
                              ColorEmojiIcon.PaintContext context)
        {
            if (depth > 64 || !ValidOffset(offset)) return;
            var format = U8(offset);
            switch (format)
            {
                case 1:
                {
                    var count = U8(offset + 1);
                    var first = (int)U32(offset + 2);
                    for (var i = 0; i < count; i++)
                    {
                        var index = first + i;
                        if (index >= 0 && index < _layerOffsets.Length)
                            DrawPaint(_layerOffsets[index], matrix, depth + 1, context);
                    }
                    break;
                }
                case 2:
                {
                    if (offset + 5 > _data.Length) return;
                    var color = ResolveColor(U16(offset + 1), F2Dot14(offset + 3),
                        context.Foreground);
                    FillSolid(color, context);
                    break;
                }
                case 4:
                    DrawLinearGradient(offset, matrix, context);
                    break;
                case 6:
                    DrawRadialGradient(offset, matrix, context);
                    break;
                case 10:
                {
                    var child = offset + (int)U24(offset + 1);
                    var glyph = U16(offset + 4);
                    var path = GetGlyphPath(glyph);
                    if (path == null || path.PointCount == 0) return;
                    using (var shape = (GraphicsPath)path.Clone())
                    using (var transform = matrix.ToMatrix())
                    {
                        shape.Transform(transform);
                        var state = context.G.Save();
                        try
                        {
                            context.G.SetClip(shape, CombineMode.Intersect);
                            DrawPaint(child, matrix, depth + 1, context);
                        }
                        finally
                        {
                            context.G.Restore(state);
                        }
                    }
                    break;
                }
                case 32:
                {
                    if (offset + 5 > _data.Length) return;
                    var source = offset + (int)U24(offset + 1);
                    var mode = U8(offset + 4);
                    var backdrop = offset + (int)U24(offset + 5);
                    if (mode == 1) DrawPaint(source, matrix, depth + 1, context);
                    else if (mode == 2) DrawPaint(backdrop, matrix, depth + 1, context);
                    else if (mode != 0)
                    {
                        DrawPaint(backdrop, matrix, depth + 1, context);
                        DrawPaint(source, matrix, depth + 1, context);
                    }
                    break;
                }
                default:
                {
                    int child;
                    ColorEmojiIcon.Affine transform;
                    if (TryReadChild(offset, out child, out transform))
                        DrawPaint(child, matrix.Compose(transform), depth + 1, context);
                    break;
                }
            }
        }

        private static void FillSolid(Color color, ColorEmojiIcon.PaintContext context)
        {
            using (var brush = new SolidBrush(color))
                context.G.FillRectangle(brush, 0, 0, context.Size, context.Size);
        }

        private void DrawLinearGradient(int offset, ColorEmojiIcon.Affine matrix,
                                        ColorEmojiIcon.PaintContext context)
        {
            if (offset + 16 > _data.Length) return;
            var line = ReadColorLine(offset + (int)U24(offset + 1));
            if (line.Length == 0) return;
            var x0 = I16(offset + 4);
            var y0 = I16(offset + 6);
            var x1 = I16(offset + 8);
            var y1 = I16(offset + 10);
            var dx = x1 - x0;
            var dy = y1 - y0;
            var length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.5f)
            {
                FillSolid(ResolveColor(line[line.Length - 1].Palette,
                    line[line.Length - 1].Alpha, context.Foreground), context);
                return;
            }

            // GDI+ 的线性画刷在渐变轴外会平铺（出现一条条硬边），而且两点构造函数
            // 遇到水平/垂直轴会得到零宽矩形。这里把画刷矩形扩展到覆盖整个 emoji，
            // 并把首尾颜色钉在矩形两端，等价于 Clamp，既不出条带也没有空白。
            var art = context.ArtBounds;
            var margin = Math.Max(64f, _unitsPerEm * 0.1f);
            var left = art.Left - margin;
            var top = art.Top - margin;
            var right = art.Right + margin;
            var bottom = art.Bottom + margin;

            var length2 = dx * dx + dy * dy;
            var nx = -dy / length;
            var ny = dx / length;
            var tMin = float.MaxValue;
            var tMax = float.MinValue;
            var pMin = float.MaxValue;
            var pMax = float.MinValue;
            var corners = new[]
            {
                new PointF(left, top),
                new PointF(right, top),
                new PointF(left, bottom),
                new PointF(right, bottom)
            };
            for (var i = 0; i < corners.Length; i++)
            {
                var t = ((corners[i].X - x0) * dx + (corners[i].Y - y0) * dy) / length2;
                var p = corners[i].X * nx + corners[i].Y * ny;
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
                if (p < pMin) pMin = p;
                if (p > pMax) pMax = p;
            }
            if (tMin > 0f) tMin = 0f;
            if (tMax < 1f) tMax = 1f;
            var span = tMax - tMin;
            if (span < 0.0001f)
            {
                FillSolid(ResolveColor(line[line.Length - 1].Palette,
                    line[line.Length - 1].Alpha, context.Foreground), context);
                return;
            }

            var centerT = (tMin + tMax) / 2f;
            var axisLength = length * span;
            var perpendicular = Math.Max(pMax - pMin, _unitsPerEm * 0.5f) + margin * 2f;
            var angle = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
            var rect = new RectangleF(
                x0 + dx * centerT - axisLength / 2f,
                y0 + dy * centerT - perpendicular / 2f,
                axisLength,
                perpendicular);

            var blend = BuildClampedBlend(line, context.Foreground, tMin, span);
            if (blend == null) return;
            using (var brush = new LinearGradientBrush(rect, Color.White, Color.Black, angle))
            {
                brush.InterpolationColors = blend;
                using (var transform = matrix.ToMatrix())
                    brush.Transform = transform;
                context.G.FillRectangle(brush, 0, 0, context.Size, context.Size);
            }
        }

        private ColorBlend BuildClampedBlend(ColorStop[] stops, Color foreground,
                                             float tMin, float span)
        {
            var source = BuildBlend(stops, foreground);
            if (source == null || source.Colors.Length == 0) return null;

            var entries = new List<KeyValuePair<float, Color>>();
            entries.Add(new KeyValuePair<float, Color>(0f, source.Colors[0]));
            for (var i = 0; i < source.Positions.Length; i++)
            {
                var position = (source.Positions[i] - tMin) / span;
                if (position > 0.00001f && position < 0.99999f)
                    entries.Add(new KeyValuePair<float, Color>(position, source.Colors[i]));
            }
            entries.Add(new KeyValuePair<float, Color>(
                1f, source.Colors[source.Colors.Length - 1]));
            entries.Sort(delegate(KeyValuePair<float, Color> a, KeyValuePair<float, Color> b)
            {
                return a.Key.CompareTo(b.Key);
            });

            var colors = new List<Color>();
            var positions = new List<float>();
            var previous = -1f;
            for (var i = 0; i < entries.Count; i++)
            {
                var position = entries[i].Key;
                if (position < 0f) position = 0f;
                if (position > 1f) position = 1f;
                if (position <= previous) position = Math.Min(1f, previous + 0.00001f);
                positions.Add(position);
                colors.Add(entries[i].Value);
                previous = position;
            }
            if (positions.Count < 2) return null;
            positions[0] = 0f;
            positions[positions.Count - 1] = 1f;
            return new ColorBlend { Colors = colors.ToArray(), Positions = positions.ToArray() };
        }
        private void DrawRadialGradient(int offset, ColorEmojiIcon.Affine matrix,
                                        ColorEmojiIcon.PaintContext context)
        {
            if (offset + 16 > _data.Length) return;
            var line = ReadColorLine(offset + (int)U24(offset + 1));
            if (line.Length == 0) return;
            var x0 = I16(offset + 4);
            var y0 = I16(offset + 6);
            var x1 = I16(offset + 10);
            var y1 = I16(offset + 12);
            var radius = U16(offset + 14);
            if (radius <= 0)
            {
                FillSolid(ResolveColor(line[line.Length - 1].Palette,
                    line[line.Length - 1].Alpha, context.Foreground), context);
                return;
            }

            var blend = BuildBlend(line, context.Foreground);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(x1 - radius, y1 - radius, radius * 2f, radius * 2f);
                using (var brush = new PathGradientBrush(path))
                {
                    var points = path.PathPoints;
                    var outer = ResolveColor(line[line.Length - 1].Palette,
                        line[line.Length - 1].Alpha, context.Foreground);
                    var surround = new Color[points.Length];
                    for (var i = 0; i < surround.Length; i++) surround[i] = outer;
                    brush.SurroundColors = surround;
                    brush.CenterPoint = new PointF(x0, y0);
                    brush.CenterColor = ResolveColor(line[0].Palette, line[0].Alpha,
                        context.Foreground);
                    if (blend != null) brush.InterpolationColors = blend;
                    brush.WrapMode = WrapMode.Clamp;
                    using (var transform = matrix.ToMatrix())
                        brush.Transform = transform;
                    context.G.FillRectangle(brush, 0, 0, context.Size, context.Size);
                }
            }
        }

        private struct ColorStop
        {
            public float Offset;
            public int Palette;
            public float Alpha;
        }

        private ColorStop[] ReadColorLine(int offset)
        {
            if (!ValidOffset(offset) || offset + 3 > _data.Length) return new ColorStop[0];
            var count = U16(offset + 1);
            if (offset + 3 + count * 6 > _data.Length) return new ColorStop[0];
            var result = new ColorStop[count];
            var p = offset + 3;
            for (var i = 0; i < count; i++)
            {
                result[i] = new ColorStop
                {
                    Offset = F2Dot14(p),
                    Palette = U16(p + 2),
                    Alpha = F2Dot14(p + 4)
                };
                p += 6;
            }
            return result;
        }

        private ColorBlend BuildBlend(ColorStop[] stops, Color foreground)
        {
            if (stops == null || stops.Length == 0) return null;
            var list = new List<ColorStop>(stops);
            list.Sort(delegate(ColorStop a, ColorStop b) { return a.Offset.CompareTo(b.Offset); });

            if (list[0].Offset > 0f)
            {
                var first = list[0];
                first.Offset = 0f;
                list.Insert(0, first);
            }
            if (list[list.Count - 1].Offset < 1f)
            {
                var last = list[list.Count - 1];
                last.Offset = 1f;
                list.Add(last);
            }

            var colors = new Color[list.Count];
            var positions = new float[list.Count];
            var previous = -1f;
            for (var i = 0; i < list.Count; i++)
            {
                var position = Math.Max(0f, Math.Min(1f, list[i].Offset));
                if (position <= previous) position = Math.Min(1f, previous + 0.00001f);
                positions[i] = position;
                previous = position;
                colors[i] = ResolveColor(list[i].Palette, list[i].Alpha, foreground);
            }
            return new ColorBlend { Colors = colors, Positions = positions };
        }

        private Color ResolveColor(int index, float alpha, Color foreground)
        {
            var a = Math.Max(0f, Math.Min(1f, alpha));
            if (index == 0xFFFF)
                return Color.FromArgb((int)Math.Round(255f * a), foreground);

            var i = index % _numPaletteEntries;
            if (i < 0) i += _numPaletteEntries;
            var offset = _colorRecordsOffset + (_paletteStart + i) * 4;
            if (offset < 0 || offset + 4 > _data.Length) return Color.White;
            var blue = U8(offset);
            var green = U8(offset + 1);
            var red = U8(offset + 2);
            var colorAlpha = U8(offset + 3);
            return Color.FromArgb((int)Math.Round(colorAlpha * a), red, green, blue);
        }

        // ---------------- 二进制读取 ----------------

        private byte U8(int offset)
        {
            if (offset < 0 || offset >= _data.Length)
                throw new InvalidDataException("字体偏移越界");
            return _data[offset];
        }

        private ushort U16(int offset)
        {
            if (offset < 0 || offset + 2 > _data.Length)
                throw new InvalidDataException("字体偏移越界");
            return (ushort)((_data[offset] << 8) | _data[offset + 1]);
        }

        private short I16(int offset)
        {
            return unchecked((short)U16(offset));
        }

        private uint U32(int offset)
        {
            if (offset < 0 || offset + 4 > _data.Length)
                throw new InvalidDataException("字体偏移越界");
            return ((uint)_data[offset] << 24) |
                   ((uint)_data[offset + 1] << 16) |
                   ((uint)_data[offset + 2] << 8) |
                   _data[offset + 3];
        }

        private int I32(int offset)
        {
            return unchecked((int)U32(offset));
        }

        private int U24(int offset)
        {
            if (offset < 0 || offset + 3 > _data.Length)
                throw new InvalidDataException("字体偏移越界");
            return (_data[offset] << 16) | (_data[offset + 1] << 8) | _data[offset + 2];
        }

        private float Fixed(int offset)
        {
            return I32(offset) / 65536f;
        }

        private float F2Dot14(int offset)
        {
            return I16(offset) / 16384f;
        }
    }
}
