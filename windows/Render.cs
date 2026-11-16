using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace EasyDeck
{
    static class Fonts
    {
        static HashSet<string> installed;
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        static bool Has(string n)
        {
            if (installed == null)
            {
                installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { using (var c = new InstalledFontCollection()) foreach (var f in c.Families) installed.Add(f.Name); } catch { }
            }
            return installed.Contains(n);
        }

        static string ui, symbol, emoji;
        static bool symbolChecked;
        public static string UiFamily => ui ?? (ui = Has("Malgun Gothic") ? "Malgun Gothic" : "Segoe UI");
        public static string EmojiFamily => emoji ?? (emoji = Has("Segoe UI Emoji") ? "Segoe UI Emoji" : UiFamily);
        /// 윈도우 11: Segoe Fluent Icons, 10: Segoe MDL2 Assets (같은 글자 번호)
        public static string SymbolFamily
        {
            get
            {
                if (!symbolChecked)
                {
                    symbolChecked = true;
                    symbol = Has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : Has("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : null;
                }
                return symbol;
            }
        }

        public static Font Get(string family, float px, FontStyle st = FontStyle.Regular)
        {
            px = Math.Max(4f, (float)Math.Round(px * 2) / 2f);
            var key = family + "|" + px + "|" + (int)st;
            if (!cache.TryGetValue(key, out var f))
            {
                try { f = new Font(family, px, st, GraphicsUnit.Pixel); }
                catch { f = new Font(FontFamily.GenericSansSerif, px, st, GraphicsUnit.Pixel); }
                cache[key] = f;
            }
            return f;
        }
    }

    class TimerRun
    {
        public double Remaining;
        public bool Running;
        public DateTime EndAt;
    }

    static class Renderer
    {
        public static readonly Color Yellow = Color.FromArgb(252, 196, 25);
        public static readonly Color Green = Color.FromArgb(64, 192, 87);
        public static readonly Color Red = Color.FromArgb(240, 62, 62);
        public static readonly Color Blue = Color.FromArgb(77, 171, 247);

        public static Color Hex(string s, Color def)
        {
            try
            {
                s = (s ?? "").Trim().TrimStart('#');
                if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
                if (s.Length == 6)
                    return Color.FromArgb(255, Convert.ToInt32(s.Substring(0, 2), 16), Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16));
                if (s.Length == 8) // 맥 형식 RRGGBBAA
                    return Color.FromArgb(Convert.ToInt32(s.Substring(6, 2), 16), Convert.ToInt32(s.Substring(0, 2), 16), Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16));
            }
            catch { }
            return def;
        }

        public static string ToHex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        public static Color Mix(Color c, Color with, float t)
            => Color.FromArgb(c.A, (int)(c.R + (with.R - c.R) * t), (int)(c.G + (with.G - c.G) * t), (int)(c.B + (with.B - c.B) * t));

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r,
            StringAlignment h = StringAlignment.Center, StringAlignment v = StringAlignment.Center, bool wrap = false)
        {
            using (var sf = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter })
            using (var b = new SolidBrush(c))
            {
                if (!wrap) sf.FormatFlags |= StringFormatFlags.NoWrap;
                g.DrawString(s ?? "", f, b, r, sf);
            }
        }

        public static void Setup(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        static float LabelH(Widget w, float k, float fs) => string.IsNullOrEmpty(w.label) ? 0 : 15 * fs * k;

        /// 슬라이더 막대 위치 (그리기와 마우스 계산에 같이 씀)
        public static RectangleF TrackRect(Widget w, RectangleF r, float k, float fs)
        {
            float lh = LabelH(w, k, fs);
            if (w.vertical)
                return new RectangleF(r.X + r.Width / 2 - 9 * k, r.Y + 20 * k, 18 * k, Math.Max(10 * k, r.Height - lh - 30 * k));
            float areaH = r.Height - lh - 6 * k;
            return new RectangleF(r.X + 12 * k, r.Y + areaH / 2 - 8 * k + 4 * k, Math.Max(10 * k, r.Width - 24 * k), 16 * k);
        }

        public static string FormatTime(double sec)
        {
            int s = (int)Math.Ceiling(Math.Max(0, sec));
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
        }

        public static void DrawWidget(Graphics g, Widget w, RectangleF r, float k, float fs, bool editing, bool pressed, TimerRun t)
        {
            var bg = Hex(w.color, Color.FromArgb(44, 49, 60));
            var fg = Hex(w.textColor, Color.White);
            using (var path = Round(r, 11 * k))
            {
                using (var br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Mix(bg, Color.White, 0.10f), Mix(bg, Color.Black, 0.06f), 90f))
                    g.FillPath(br, path);
                if (pressed) using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillPath(b, path);
                if (editing)
                    using (var pen = new Pen(Yellow, 1.6f * k) { DashStyle = DashStyle.Dash })
                        g.DrawPath(pen, path);
            }

            float lh = LabelH(w, k, fs);
            var area = new RectangleF(r.X, r.Y + 4 * k, r.Width, r.Height - lh - 10 * k);

            switch (w.kind)
            {
                case WidgetKind.knob: DrawKnob(g, w, bg, fg, area, k); break;
                case WidgetKind.slider: DrawSlider(g, w, r, fg, k, fs); break;
                case WidgetKind.timer: DrawTimer(g, w, area, fg, k, t); break;
                default:
                    {
                        float s = Math.Min(Math.Min(area.Width, area.Height) * 0.56f, 54 * k * fs);
                        var sq = new RectangleF(area.X + (area.Width - s) / 2, area.Y + (area.Height - s) / 2, s, s);
                        Icons.Draw(g, w.icon, w.label, sq, fg);
                        if (w.kind == WidgetKind.toggle)
                            using (var b = new SolidBrush(w.isOn ? Green : Color.FromArgb(110, 255, 255, 255)))
                                g.FillEllipse(b, r.Right - 15 * k, r.Y + 7 * k, 8 * k, 8 * k);
                        break;
                    }
            }

            if (lh > 0)
                Text(g, w.label, Fonts.Get(Fonts.UiFamily, 11.5f * fs * k, FontStyle.Bold), Color.FromArgb(240, fg),
                    new RectangleF(r.X + 4 * k, r.Bottom - lh - 6 * k, r.Width - 8 * k, lh + 2 * k));

            if (editing)
                using (var pen = new Pen(Color.FromArgb(200, Yellow), 1.4f * k))
                {
                    float x = r.Right - 5 * k, y = r.Bottom - 5 * k;
                    g.DrawLine(pen, x - 9 * k, y, x, y - 9 * k);
                    g.DrawLine(pen, x - 5 * k, y, x, y - 5 * k);
                }
        }

        static void DrawKnob(Graphics g, Widget w, Color bg, Color fg, RectangleF area, float k)
        {
            float d = Math.Min(area.Width, area.Height) * 0.86f;
            float cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
            var c = new RectangleF(cx - d / 2, cy - d / 2, d, d);
            using (var b = new SolidBrush(Mix(bg, Color.Black, 0.28f))) g.FillEllipse(b, c);
            using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255), 2 * k)) g.DrawEllipse(pen, c);
            using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1.4f * k))
                for (int i = 0; i < 12; i++)
                {
                    double a = i * Math.PI / 6;
                    float r1 = d / 2 * 0.74f, r2 = d / 2 * 0.88f;
                    g.DrawLine(pen, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1, cx + (float)Math.Cos(a) * r2, cy + (float)Math.Sin(a) * r2);
                }
            // 0 → 왼쪽 아래, 100 → 오른쪽 아래 (시계 방향)
            double ang = (135 + 270 * Settings.Clamp(w.value, 0, 100) / 100) * Math.PI / 180;
            float pr = d / 2 * 0.60f;
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, cx + (float)Math.Cos(ang) * pr - 3 * k, cy + (float)Math.Sin(ang) * pr - 3 * k, 6 * k, 6 * k);
            float s = d * 0.36f;
            Icons.Draw(g, w.icon, w.label, new RectangleF(cx - s / 2, cy - s / 2, s, s), fg);
        }

        static void DrawSlider(Graphics g, Widget w, RectangleF r, Color fg, float k, float fs)
        {
            var tr = TrackRect(w, r, k, fs);
            float v = (float)Settings.Clamp(w.value, 0, 100) / 100f;
            using (var p = Round(tr, Math.Min(tr.Width, tr.Height) / 2))
            using (var b = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.FillPath(b, p);
            RectangleF fill = w.vertical
                ? new RectangleF(tr.X, tr.Bottom - Math.Max(tr.Width, tr.Height * v), tr.Width, Math.Max(tr.Width, tr.Height * v))
                : new RectangleF(tr.X, tr.Y, Math.Max(tr.Height, tr.Width * v), tr.Height);
            using (var p = Round(fill, Math.Min(tr.Width, tr.Height) / 2))
            using (var b = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                g.FillPath(b, p);
            var vf = Fonts.Get(Fonts.UiFamily, 11 * fs * k, FontStyle.Bold);
            var txt = ((int)Math.Round(w.value)).ToString();
            if (w.vertical) Text(g, txt, vf, fg, new RectangleF(r.X, r.Y + 3 * k, r.Width, 16 * k));
            else Text(g, txt, vf, fg, new RectangleF(r.X, r.Y + 4 * k, r.Width - 9 * k, 16 * k), StringAlignment.Far);
        }

        static void DrawTimer(Graphics g, Widget w, RectangleF area, Color fg, float k, TimerRun t)
        {
            double remain = t?.Remaining ?? w.duration;
            var s = FormatTime(remain);
            float size = Math.Min(area.Height * 0.52f, area.Width / (s.Length * 0.62f));
            var f = Fonts.Get(Fonts.UiFamily, size, FontStyle.Bold);
            var textR = new RectangleF(area.X, area.Y, area.Width, area.Height - 8 * k);
            Text(g, s, f, Color.FromArgb(t != null && t.Running ? 255 : 225, fg), textR);
            var bar = new RectangleF(area.X + 14 * k, area.Bottom - 5 * k, area.Width - 28 * k, 4 * k);
            using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillRectangle(b, bar);
            float frac = w.duration > 0 ? (float)Settings.Clamp(remain / w.duration, 0, 1) : 0;
            using (var b = new SolidBrush(Color.FromArgb(t != null && t.Running ? 240 : 150, 255, 255, 255)))
                g.FillRectangle(b, bar.X, bar.Y, bar.Width * frac, bar.Height);
        }
    }

    // MARK: 아이콘 (기호 글꼴 / 그림 파일 / 글자·이모지)

    static class Icons
    {
        /// 맥의 SF 심볼 이름 → 윈도우 기호 글꼴(Segoe MDL2/Fluent) 글자
        public static readonly Dictionary<string, char> Glyphs = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase)
        {
            ["doc.on.doc"] = '', ["doc.on.clipboard"] = '', ["camera.viewfinder"] = '', ["camera"] = '',
            ["globe"] = '', ["playpause"] = '', ["play"] = '', ["pause"] = '',
            ["speaker.wave.2"] = '', ["speaker"] = '', ["speaker.slash"] = '', ["moon"] = '',
            ["sun.max"] = '', ["arrow.right.circle"] = '', ["arrow.left.circle"] = '',
            ["plus.square"] = '', ["plus"] = '', ["xmark.square"] = '', ["xmark"] = '',
            ["arrow.clockwise"] = '', ["arrow.left.arrow.right"] = '', ["magnifyingglass"] = '',
            ["chevron.left"] = '', ["chevron.right"] = '', ["plus.magnifyingglass"] = '',
            ["minus.magnifyingglass"] = '', ["text.quote"] = '', ["folder"] = '', ["doc"] = '',
            ["mic"] = '', ["video"] = '', ["music.note"] = '', ["timer"] = '', ["stopwatch"] = '',
            ["bell"] = '', ["keyboard"] = '', ["star"] = '', ["heart"] = '', ["house"] = '',
            ["gearshape"] = '', ["gear"] = '', ["trash"] = '', ["lock"] = '', ["power"] = '',
            ["wifi"] = '', ["bolt"] = '', ["square.and.arrow.up"] = '', ["record.circle"] = '',
            ["envelope"] = '', ["person.2"] = '', ["person"] = '', ["checkmark"] = '',
            ["square"] = '', ["switch.2"] = '', ["dial.low"] = '', ["calendar"] = '',
            ["clock"] = '', ["display"] = '', ["tv"] = '', ["photo"] = '', ["printer"] = '',
            ["arrow.down.circle"] = '', ["arrow.up.circle"] = '', ["arrow.triangle.2.circlepath"] = '',
            ["forward"] = '', ["backward"] = '', ["mic.slash"] = '', ["eye"] = '',
            ["link"] = '', ["bookmark"] = '', ["text.bubble"] = '', ["message"] = '',
            ["book"] = '', ["pencil"] = '', ["scissors"] = '', ["paintbrush"] = '',
        };

        public static char? Glyph(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            if (icon.StartsWith("mdl:", StringComparison.OrdinalIgnoreCase))
            {
                try { return (char)Convert.ToInt32(icon.Substring(4), 16); } catch { return null; }
            }
            if (!icon.StartsWith("sf:", StringComparison.OrdinalIgnoreCase)) return null;
            var n = icon.Substring(3);
            if (Glyphs.TryGetValue(n, out var c)) return c;
            foreach (var suffix in new[] { ".fill", ".circle", ".square" })
                if (n.EndsWith(suffix) && Glyphs.TryGetValue(n.Substring(0, n.Length - suffix.Length), out c)) return c;
            n = n.Replace(".fill", "");
            return Glyphs.TryGetValue(n, out c) ? c : (char?)null;
        }

        public static void Draw(Graphics g, string icon, string label, RectangleF sq, Color fg)
        {
            if (string.IsNullOrWhiteSpace(icon) || sq.Width < 2) return;
            if (icon.StartsWith("sf:", StringComparison.OrdinalIgnoreCase) || icon.StartsWith("mdl:", StringComparison.OrdinalIgnoreCase))
            {
                var ch = Glyph(icon);
                if (ch != null && Fonts.SymbolFamily != null)
                {
                    Renderer.Text(g, ch.Value.ToString(), Fonts.Get(Fonts.SymbolFamily, sq.Height * 0.86f), fg, Expand(sq, 0.3f));
                    return;
                }
                var letter = string.IsNullOrEmpty(label) ? "•" : label.Substring(0, 1);
                Renderer.Text(g, letter, Fonts.Get(Fonts.UiFamily, sq.Height * 0.72f, FontStyle.Bold), fg, Expand(sq, 0.3f));
                return;
            }
            var img = Image(icon);
            if (img != null)
            {
                float sc = Math.Min(sq.Width / img.Width, sq.Height / img.Height);
                float w = img.Width * sc, h = img.Height * sc;
                g.DrawImage(img, sq.X + (sq.Width - w) / 2, sq.Y + (sq.Height - h) / 2, w, h);
                return;
            }
            float f = icon.Length <= 2 ? 0.8f : Math.Max(0.3f, 1.6f / icon.Length);
            Renderer.Text(g, icon, Fonts.Get(Fonts.EmojiFamily, sq.Height * f), fg, Expand(sq, 0.5f));
        }

        static RectangleF Expand(RectangleF r, float t) => RectangleF.Inflate(r, r.Width * t, r.Height * t);

        static readonly Dictionary<string, Tuple<DateTime, Image>> cache = new Dictionary<string, Tuple<DateTime, Image>>();

        public static string Resolve(string icon)
        {
            try
            {
                var p = icon;
                if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
                if (IsNetworkPath(p)) return null;   // \\서버\공유 를 열면 윈도우가 로그인 정보를 보낼 수 있음
                if (Path.IsPathRooted(p) && File.Exists(p)) return p;
                if (Store.IconsDir != null && icon.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                {
                    var q = Path.Combine(Store.IconsDir, icon);
                    if (!IsNetworkPath(q) && File.Exists(q)) return q;
                }
            }
            catch { }
            return null;
        }

        /// \\server\share, //server, \\?\ 같은 네트워크·장치 경로
        public static bool IsNetworkPath(string p)
        {
            p = (p ?? "").Trim().Replace('/', '\\');
            if (p.StartsWith(@"\\")) return true;                         // \\서버, \\?\, \\.\ (빗금 섞어 써도)
            try
            {
                if (Uri.TryCreate(p, UriKind.Absolute, out var u) && u.IsUnc) return true;
                if (p.Length >= 2 && p[1] == ':')                          // 네트워크 드라이브(Z: 등)
                    return new DriveInfo(p.Substring(0, 1)).DriveType == DriveType.Network;
            }
            catch { }
            return false;
        }

        public static Image Image(string icon)
        {
            var path = Resolve(icon);
            if (path == null) return null;
            try
            {
                var t = File.GetLastWriteTimeUtc(path);
                if (cache.TryGetValue(path, out var c) && c.Item1 == t) return c.Item2;
                Image img = null;
                if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    using (var ic = new Icon(path, 256, 256)) img = ic.ToBitmap();
                else
                    using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                    using (var raw = System.Drawing.Image.FromStream(ms))
                        img = new Bitmap(raw);
                cache[path] = Tuple.Create(t, img);
                return img;
            }
            catch { return null; }
        }

        public static bool IsImage(string path)
        {
            var e = Path.GetExtension(path ?? "").ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif" || e == ".ico" || e == ".tif" || e == ".tiff";
        }

        static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; }
            return h;
        }

        /// icons 폴더에 PNG로 저장하고 파일 이름을 돌려줌 (같은 대상은 같은 이름)
        public static string SaveIcon(Image img, string key)
        {
            var name = "ico-" + Fnv(key).ToString("x8") + ".png";
            var path = Path.Combine(Store.IconsDir, name);
            using (var b = Fit(img, 256)) b.Save(path, ImageFormat.Png);
            cache.Remove(path);
            return name;
        }

        public static string ImportImage(string file)
        {
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(file)))
                using (var img = System.Drawing.Image.FromStream(ms))
                    return SaveIcon(img, file + "|" + new FileInfo(file).Length);
            }
            catch (Exception e) { Log.Write("그림 가져오기 실패: " + e.Message); return null; }
        }

        static Bitmap Fit(Image img, int max)
        {
            float sc = Math.Min(1f, Math.Min((float)max / img.Width, (float)max / img.Height));
            int w = Math.Max(1, (int)(img.Width * sc)), h = Math.Max(1, (int)(img.Height * sc));
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, w, h);
            }
            return b;
        }

        /// 파일·프로그램·바로 가기의 큰 아이콘 (그림 파일이면 미리보기)
        public static Bitmap FileIcon(string path, int size = 128, bool thumbnail = false)
        {
            Native.IShellItemImageFactory f = null;
            try
            {
                Native.SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(Native.IShellItemImageFactory).GUID, out f);
                int flags = thumbnail ? 0 : 0x4; // SIIGBF_ICONONLY
                if (f.GetImage(new Native.SIZE { cx = size, cy = size }, flags, out var hbm) == 0 && hbm != IntPtr.Zero)
                {
                    try { return FromHBitmap(hbm); }
                    finally { Native.DeleteObject(hbm); }
                }
            }
            catch (Exception e) { Log.Write("아이콘 가져오기 실패 (" + path + "): " + e.Message); }
            finally { if (f != null) Marshal.ReleaseComObject(f); }
            try { using (var ic = Icon.ExtractAssociatedIcon(path)) return ic?.ToBitmap(); }
            catch { return null; }
        }

        static Bitmap FromHBitmap(IntPtr h)
        {
            if (Native.GetObjectDib(h, Marshal.SizeOf(typeof(Native.DIBSECTION)), out var ds) != 0
                && ds.dsBm.bmBitsPixel == 32 && ds.dsBm.bmBits != IntPtr.Zero)
            {
                int w = ds.dsBm.bmWidth, hh = ds.dsBm.bmHeight;
                using (var tmp = new Bitmap(w, hh, ds.dsBm.bmWidthBytes, PixelFormat.Format32bppPArgb, ds.dsBm.bmBits))
                {
                    var copy = new Bitmap(w, hh, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(copy)) g.DrawImage(tmp, 0, 0, w, hh);
                    if (ds.dsBmih.biHeight > 0) copy.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    return copy;
                }
            }
            return System.Drawing.Image.FromHbitmap(h);
        }
    }
}
