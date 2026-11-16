using System;

namespace BaroUI.Core
{
    /// <summary>How a picture sits in a box (UiImages.Draw): Contain shows all of it, as large as fits; Cover fills the box and crops the picture's
    /// edges; Stretch fills the box whatever the proportions; Actual is one picture pixel to a screen pixel, centred (a larger picture is cropped);
    /// ScaleDown is the smaller of Actual and Contain (object-fit: scale-down).</summary>
    public enum ImageFit { Contain, Cover, Stretch, Actual, ScaleDown }

    /// <summary>Where a picture (or a part of it) is drawn: the box on screen and the part of the picture shown (a crop when it does not fit).</summary>
    public readonly struct ImagePlacement
    {
        public readonly float X, Y, W, H;
        public readonly int SrcX, SrcY, SrcW, SrcH;
        /// <summary>Screen pixels per picture pixel (across; the vertical one differs only for Stretch).</summary>
        public readonly float ScaleX, ScaleY;

        public ImagePlacement(float x, float y, float w, float h, int sx, int sy, int sw, int sh, float scaleX, float scaleY)
        {
            X = x; Y = y; W = w; H = h; SrcX = sx; SrcY = sy; SrcW = sw; SrcH = sh; ScaleX = scaleX; ScaleY = scaleY;
        }

        public bool IsEmpty => W <= 0 || H <= 0 || SrcW <= 0 || SrcH <= 0;

        /// <summary>The picture pixel under a screen point (false: the point is outside the drawn part).</summary>
        public bool PixelAt(float px, float py, out float picX, out float picY)
        {
            picX = picY = 0;
            if (IsEmpty || px < X || py < Y || px >= X + W || py >= Y + H) { return false; }
            picX = SrcX + (px - X) / ScaleX;
            picY = SrcY + (py - Y) / ScaleY;
            return true;
        }

        /// <summary>The screen point of a picture pixel (its top-left corner).</summary>
        public (float X, float Y) ScreenOf(float picX, float picY) => (X + (picX - SrcX) * ScaleX, Y + (picY - SrcY) * ScaleY);
    }

    /// <summary>The geometry of pictures on a canvas, free of the game so the tests run it.</summary>
    public static class ImageLayout
    {
        /// <summary>Fits the part (srcX, srcY, srcW, srcH) of a picture into the box. integerScale: a scale above 1 is cut down to a whole number
        /// (pixel art stays sharp), a scale below 1 stays as it is.</summary>
        public static ImagePlacement Place(int srcX, int srcY, int srcW, int srcH, float boxX, float boxY, float boxW, float boxH, ImageFit fit, bool integerScale = false)
        {
            if (srcW <= 0 || srcH <= 0 || boxW <= 0 || boxH <= 0) { return default; }
            if (fit == ImageFit.Stretch)
            {
                return new ImagePlacement(boxX, boxY, boxW, boxH, srcX, srcY, srcW, srcH, boxW / srcW, boxH / srcH);
            }
            float fx = boxW / srcW, fy = boxH / srcH;
            float scale = fit switch { ImageFit.Contain => Math.Min(fx, fy), ImageFit.Cover => Math.Max(fx, fy), ImageFit.ScaleDown => Math.Min(1f, Math.Min(fx, fy)), _ => 1f };
            if (integerScale && scale > 1) { scale = (float)Math.Floor(scale); }
            float w = srcW * scale, h = srcH * scale;
            int sx = srcX, sy = srcY, sw = srcW, sh = srcH;
            if (w > boxW + 0.001f)
            {
                sw = Math.Max(1, (int)Math.Round(boxW / scale));
                sx = srcX + (srcW - sw) / 2;
                w = sw * scale;
            }
            if (h > boxH + 0.001f)
            {
                sh = Math.Max(1, (int)Math.Round(boxH / scale));
                sy = srcY + (srcH - sh) / 2;
                h = sh * scale;
            }
            return new ImagePlacement(boxX + (boxW - w) / 2, boxY + (boxH - h) / 2, w, h, sx, sy, sw, sh, scale, scale);
        }

        /// <summary>The cell (column, row) of a picture cut in a grid of equal cells (a sprite sheet): x, y, width, height in picture pixels. Cells are
        /// counted left to right, then down; a cell that is outside the grid is empty. cellW and cellH may be 0: the picture is then divided by cols and rows.</summary>
        public static (int X, int Y, int W, int H) Cell(int picW, int picH, int cols, int rows, int index, int cellW = 0, int cellH = 0)
        {
            if (picW <= 0 || picH <= 0 || cols <= 0 || rows <= 0 || index < 0 || index >= cols * rows) { return (0, 0, 0, 0); }
            if (cellW <= 0) { cellW = picW / cols; }
            if (cellH <= 0) { cellH = picH / rows; }
            int c = index % cols, r = index / cols;
            int x = c * cellW, y = r * cellH;
            return (x, y, Math.Max(0, Math.Min(cellW, picW - x)), Math.Max(0, Math.Min(cellH, picH - y)));
        }
    }

    internal enum BgSizeKind : byte { Auto, Cover, Contain, Lengths }
    /// <summary>background-repeat on one axis.</summary>
    internal enum BgRepeat : byte { Repeat, NoRepeat, Space, Round }

    /// <summary>background-size: auto | cover | contain | &lt;w&gt; [&lt;h&gt;] (a length, a percentage of the area, or auto).</summary>
    internal readonly struct BgSize
    {
        public readonly BgSizeKind Kind;
        public readonly Length W, H;
        public BgSize(BgSizeKind kind, Length w, Length h) { Kind = kind; W = w; H = h; }
        public static readonly BgSize Auto = new(BgSizeKind.Auto, Length.Auto, Length.Auto);
    }

    /// <summary>Where a picture sits on one axis: Pct of the free room (the area minus the picture) plus an offset (negative from the far edge).</summary>
    internal readonly struct BgAxis
    {
        public readonly float Pct;
        public readonly Length Off;
        public BgAxis(float pct, Length off) { Pct = pct; Off = off; }
    }

    internal readonly struct BgPosition
    {
        public readonly BgAxis X, Y;
        public BgPosition(BgAxis x, BgAxis y) { X = x; Y = y; }
        public static readonly BgPosition Origin = new(new BgAxis(0, Length.Zero), new BgAxis(0, Length.Zero));
    }

    /// <summary>One copy of a picture on screen: the destination (already clipped to the area) and the part of the picture it shows.</summary>
    internal readonly struct BgTile
    {
        public readonly float X, Y, W, H;
        public readonly int SrcX, SrcY, SrcW, SrcH;
        public BgTile(float x, float y, float w, float h, int sx, int sy, int sw, int sh) { X = x; Y = y; W = w; H = h; SrcX = sx; SrcY = sy; SrcW = sw; SrcH = sh; }
    }

    /// <summary>The CSS side of pictures, free of the game so the tests run it: url() and the background-* / object-fit values, where a package's picture
    /// path leads, and the placement of a background picture (size, position, repeat) as the list of copies to draw.</summary>
    internal static class ImageCss
    {
        /// <summary>The first layer of a comma list (commas inside brackets or quotes do not split); more: there were others, which are not drawn.</summary>
        public static string FirstLayer(string value, out bool more)
        {
            more = false;
            if (value == null) { return ""; }
            int depth = 0;
            char quote = (char)0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (quote != 0) { if (c == quote) { quote = (char)0; } continue; }
                if (c == '"' || c == '\'') { quote = c; }
                else if (c == '(') { depth++; }
                else if (c == ')') { depth = Math.Max(0, depth - 1); }
                else if (c == ',' && depth == 0) { more = true; return value.Substring(0, i).Trim(); }
            }
            return value.Trim();
        }

        /// <summary>The argument of name(...) when the whole value is one such call (url("a.png") gives a.png); quotes are taken off.</summary>
        public static bool TryCall(string value, string name, out string arg)
        {
            arg = null;
            if (value == null) { return false; }
            var t = value.Trim();
            if (t.Length < name.Length + 2 || !t.StartsWith(name + "(", StringComparison.OrdinalIgnoreCase) || t[t.Length - 1] != ')') { return false; }
            var inner = t.Substring(name.Length + 1, t.Length - name.Length - 2).Trim();
            if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') && inner[inner.Length - 1] == inner[0]) { inner = inner.Substring(1, inner.Length - 2); }
            if (inner.Length == 0) { return false; }
            arg = inner;
            return true;
        }

        public static bool TryUrl(string value, out string url) => TryCall(value, "url", out url);

        private static string[] Words(string v) => v == null ? Array.Empty<string>() : v.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        private static bool TryLen(string w, bool allowAuto, out Length l)
        {
            if (!Length.TryParse(w, out l)) { return false; }
            if (l.IsAuto) { return allowAuto; }
            return l.Unit is Unit.Px or Unit.U or Unit.Em or Unit.Percent && l.Value >= 0 || l.Unit == Unit.None && l.Value == 0;
        }

        public static bool TryParseSize(string value, out BgSize size)
        {
            size = BgSize.Auto;
            var w = Words(value);
            if (w.Length == 0 || w.Length > 2) { return false; }
            if (w.Length == 1 && w[0].Equals("cover", StringComparison.OrdinalIgnoreCase)) { size = new BgSize(BgSizeKind.Cover, Length.Auto, Length.Auto); return true; }
            if (w.Length == 1 && w[0].Equals("contain", StringComparison.OrdinalIgnoreCase)) { size = new BgSize(BgSizeKind.Contain, Length.Auto, Length.Auto); return true; }
            if (!TryLen(w[0], true, out var a)) { return false; }
            var b = Length.Auto;
            if (w.Length == 2 && !TryLen(w[1], true, out b)) { return false; }
            size = a.IsAuto && b.IsAuto ? BgSize.Auto : new BgSize(BgSizeKind.Lengths, a, b);
            return true;
        }

        private static bool IsKeyword(string w) => w is "left" or "right" or "top" or "bottom" or "center";

        private static float KeywordPct(string w) => w is "left" or "top" ? 0 : w is "center" ? 50 : 100;

        private static bool TryLenAxis(string w, out BgAxis axis)
        {
            axis = default;
            if (!TryLen(w, false, out var l)) { return false; }
            axis = l.Unit == Unit.Percent ? new BgAxis(l.Value, Length.Zero) : new BgAxis(0, l);
            return true;
        }

        private static bool KeywordAxis(string kw, string offset, ref BgAxis? slot, bool vertical)
        {
            if (slot != null) { return false; }
            if (kw is "left" or "right" && vertical || kw is "top" or "bottom" && !vertical) { return false; }
            var off = Length.Zero;
            if (offset != null)
            {
                if (kw == "center" || !TryLen(offset, false, out off) || off.Unit == Unit.Percent) { return false; }
                if (kw is "right" or "bottom") { off = new Length(-off.Value, off.Unit); }
            }
            slot = new BgAxis(KeywordPct(kw), off);
            return true;
        }

        private static bool LengthAxis(string w, ref BgAxis? slot)
        {
            if (slot != null || !TryLenAxis(w, out var a)) { return false; }
            slot = a;
            return true;
        }

        /// <summary>background-position: one or two values (keywords, lengths, percentages), or the four value form with edge offsets
        /// ("right 10px bottom 4px"). A value that makes no sense is false.</summary>
        public static bool TryParsePosition(string value, out BgPosition pos)
        {
            pos = BgPosition.Origin;
            var w = Words(value);
            if (w.Length == 0 || w.Length > 4) { return false; }
            for (int i = 0; i < w.Length; i++) { w[i] = w[i].ToLowerInvariant(); }
            BgAxis? x = null, y = null;
            var mid = new BgAxis(50, Length.Zero);
            if (w.Length == 1)
            {
                string a = w[0];
                if (a is "top" or "bottom") { if (!KeywordAxis(a, null, ref y, true)) { return false; } x = mid; }
                else if (IsKeyword(a)) { if (!KeywordAxis(a, null, ref x, false)) { return false; } y = mid; }
                else if (LengthAxis(a, ref x)) { y = mid; }
                else { return false; }
            }
            else if (w.Length == 2)
            {
                string p = w[0], q = w[1];
                if (p is "top" or "bottom" || q is "left" or "right") { (p, q) = (q, p); }   // "top left" is "left top"
                bool okx = p == "center" ? KeywordAxis(p, null, ref x, false) : IsKeyword(p) ? KeywordAxis(p, null, ref x, false) : LengthAxis(p, ref x);
                bool oky = q == "center" ? KeywordAxis(q, null, ref y, true) : IsKeyword(q) ? KeywordAxis(q, null, ref y, true) : LengthAxis(q, ref y);
                if (!okx || !oky) { return false; }
            }
            else
            {
                // three or four values: keyword [offset] keyword [offset]
                int k = 0;
                while (k < w.Length)
                {
                    string kw = w[k++];
                    if (!IsKeyword(kw)) { return false; }
                    string off = k < w.Length && !IsKeyword(w[k]) ? w[k++] : null;
                    bool vertical = kw is "top" or "bottom";
                    bool ok = kw == "center" ? (x == null ? KeywordAxis(kw, off, ref x, false) : KeywordAxis(kw, off, ref y, true))
                        : vertical ? KeywordAxis(kw, off, ref y, true) : KeywordAxis(kw, off, ref x, false);
                    if (!ok) { return false; }
                }
            }
            pos = new BgPosition(x ?? mid, y ?? mid);
            return true;
        }

        /// <summary>background-repeat: repeat-x, repeat-y, or one or two of repeat, no-repeat, space, round.</summary>
        public static bool TryParseRepeat(string value, out BgRepeat x, out BgRepeat y)
        {
            x = y = BgRepeat.Repeat;
            var w = Words(value);
            if (w.Length == 0 || w.Length > 2) { return false; }
            string a = w[0].ToLowerInvariant();
            if (w.Length == 1 && a == "repeat-x") { y = BgRepeat.NoRepeat; return true; }
            if (w.Length == 1 && a == "repeat-y") { x = BgRepeat.NoRepeat; return true; }
            if (!TryRepeatWord(a, out x)) { return false; }
            y = x;
            return w.Length == 1 || TryRepeatWord(w[1].ToLowerInvariant(), out y);
        }

        public static bool IsRepeatWord(string w)
        {
            w = w.ToLowerInvariant();
            return w is "repeat-x" or "repeat-y" || TryRepeatWord(w, out _);
        }

        private static bool TryRepeatWord(string w, out BgRepeat r)
        {
            r = BgRepeat.Repeat;
            switch (w)
            {
                case "repeat": return true;
                case "no-repeat": r = BgRepeat.NoRepeat; return true;
                case "space": r = BgRepeat.Space; return true;
                case "round": r = BgRepeat.Round; return true;
            }
            return false;
        }

        /// <summary>object-fit: fill, contain, cover, none, scale-down.</summary>
        public static bool TryParseFit(string value, out ImageFit fit)
        {
            fit = ImageFit.Stretch;
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "fill": fit = ImageFit.Stretch; return true;
                case "contain": fit = ImageFit.Contain; return true;
                case "cover": fit = ImageFit.Cover; return true;
                case "none": fit = ImageFit.Actual; return true;
                case "scale-down": fit = ImageFit.ScaleDown; return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- paths

        /// <summary>Where a picture named in a page or its stylesheet is: relative to the page's folder, and inside the package (the nearest folder at or above the
        /// page that holds filelist.xml; the page's own folder when there is none). Absolute paths, web addresses and data: are refused (nothing is downloaded).
        /// fileExists tells whether a path exists (the tests give their own); a query or #fragment after the name is dropped.</summary>
        public static bool ResolvePath(string pageDir, string url, Func<string, bool> fileExists, out string full, out string problem)
        {
            full = null; problem = null;
            if (string.IsNullOrWhiteSpace(url)) { problem = "no picture path"; return false; }
            if (string.IsNullOrEmpty(pageDir)) { problem = "the page has no folder (it was not read from a file)"; return false; }
            int cut = url.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) { url = url.Substring(0, cut); }
            url = url.Trim();
            if (url.Contains("://") || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("//", StringComparison.Ordinal))
            {
                problem = $"'{url}': only files of the package can be shown";
                return false;
            }
            try
            {
                if (System.IO.Path.IsPathRooted(url)) { problem = $"'{url}': an absolute path is not allowed, write it relative to the page"; return false; }
                string dir = System.IO.Path.GetFullPath(pageDir);
                string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, url));
                string root = dir;
                for (var d = new System.IO.DirectoryInfo(dir); d != null; d = d.Parent)
                {
                    if (fileExists(System.IO.Path.Combine(d.FullName, "filelist.xml"))) { root = d.FullName; break; }
                }
                var sep = System.IO.Path.DirectorySeparatorChar;
                if (!path.StartsWith(root.TrimEnd(sep) + sep, StringComparison.OrdinalIgnoreCase)) { problem = $"'{url}' leads out of the package"; return false; }
                full = path;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                problem = $"'{url}': not a valid path";
                return false;
            }
        }

        // ---------------------------------------------------------------- background placement

        private static float Px(Length l, float basis, float unitPx, float emPx) =>
            l.Unit switch { Unit.Percent => basis * l.Value / 100f, Unit.U => l.Value * unitPx, Unit.Em => l.Value * emPx, Unit.Px => l.Value, _ => 0f };

        /// <summary>The size one copy of a picture is drawn at (background-size) in an area: cover and contain by the area, a length by itself or by the area for a
        /// percentage, auto from the other side to keep the proportions (or the natural size when both are auto).</summary>
        public static (float W, float H) TileSize(int imgW, int imgH, float areaW, float areaH, in BgSize size, float unitPx, float emPx)
        {
            if (imgW <= 0 || imgH <= 0) { return (0, 0); }
            switch (size.Kind)
            {
                case BgSizeKind.Cover:
                case BgSizeKind.Contain:
                {
                    float fx = areaW / imgW, fy = areaH / imgH;
                    float s = size.Kind == BgSizeKind.Cover ? Math.Max(fx, fy) : Math.Min(fx, fy);
                    return (imgW * s, imgH * s);
                }
                case BgSizeKind.Lengths:
                {
                    bool aw = size.W.IsAuto, ah = size.H.IsAuto;
                    float w = aw ? 0 : Px(size.W, areaW, unitPx, emPx), h = ah ? 0 : Px(size.H, areaH, unitPx, emPx);
                    if (aw) { w = h * imgW / imgH; }
                    else if (ah) { h = w * imgH / imgW; }
                    return (w, h);
                }
            }
            return (imgW, imgH);
        }

        /// <summary>One axis of a repeating background: the start of the first copy, the step between copies and how many, for an area of length len.</summary>
        private static void Axis(float len, ref float tile, in BgAxis pos, BgRepeat repeat, float unitPx, float emPx, out float first, out float step, out int count)
        {
            if (repeat == BgRepeat.Round && tile > 0) { tile = len / Math.Max(1, (int)Math.Round(len / tile)); }
            step = tile;
            float offset = (len - tile) * pos.Pct / 100f + Px(pos.Off, len, unitPx, emPx);
            if (repeat == BgRepeat.NoRepeat) { first = offset; count = 1; return; }
            if (repeat == BgRepeat.Space)
            {
                int n = tile > 0 ? (int)Math.Floor(len / tile + 0.0001f) : 0;
                if (n <= 1) { first = offset; count = 1; return; }
                step = (len - tile) / (n - 1);
                first = 0; count = n;
                return;
            }
            if (tile <= 0) { first = offset; count = 0; return; }
            first = offset - (float)Math.Ceiling(offset / tile) * tile;   // the first copy that is not wholly left of the area
            if (first + tile <= 0.0001f) { first += tile; }
            count = Math.Max(0, (int)Math.Ceiling((len - first) / tile - 0.0001f));
        }

        /// <summary>The copies of a picture (imgW x imgH) that cover an area, each clipped to the area, written into tiles. Returns how many; truncated is true when
        /// the area needs more copies than tiles holds (a tiny picture over a large area: the rest is not drawn). Nothing is allocated.</summary>
        public static int Tiles(Span<BgTile> tiles, float areaX, float areaY, float areaW, float areaH, int imgW, int imgH, in BgSize size, in BgPosition pos,
            BgRepeat rx, BgRepeat ry, float unitPx, float emPx, out bool truncated)
        {
            truncated = false;
            if (areaW <= 0 || areaH <= 0 || imgW <= 0 || imgH <= 0) { return 0; }
            var (tw, th) = TileSize(imgW, imgH, areaW, areaH, size, unitPx, emPx);
            if (tw < 0.5f || th < 0.5f) { return 0; }
            Axis(areaW, ref tw, pos.X, rx, unitPx, emPx, out float fx, out float stepX, out int nx);
            Axis(areaH, ref th, pos.Y, ry, unitPx, emPx, out float fy, out float stepY, out int ny);
            int n = 0;
            for (int j = 0; j < ny; j++)
            {
                float ty = fy + j * stepY;
                float y0 = Math.Max(ty, 0), y1 = Math.Min(ty + th, areaH);
                if (y1 - y0 <= 0.001f) { continue; }
                for (int i = 0; i < nx; i++)
                {
                    float tx = fx + i * stepX;
                    float x0 = Math.Max(tx, 0), x1 = Math.Min(tx + tw, areaW);
                    if (x1 - x0 <= 0.001f) { continue; }
                    if (n >= tiles.Length) { truncated = true; return n; }
                    int sx = (int)Math.Round((x0 - tx) / tw * imgW), sx1 = (int)Math.Round((x1 - tx) / tw * imgW);
                    int sy = (int)Math.Round((y0 - ty) / th * imgH), sy1 = (int)Math.Round((y1 - ty) / th * imgH);
                    sx = Math.Min(sx, imgW - 1); sy = Math.Min(sy, imgH - 1);
                    sx1 = Math.Min(imgW, Math.Max(sx1, sx + 1)); sy1 = Math.Min(imgH, Math.Max(sy1, sy + 1));
                    tiles[n++] = new BgTile(areaX + x0, areaY + y0, x1 - x0, y1 - y0, sx, sy, sx1 - sx, sy1 - sy);
                }
            }
            return n;
        }
    }
}
