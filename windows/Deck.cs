using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace EasyDeck
{
    /// 화면에 떠 있는 덱. 눌러도 지금 쓰던 프로그램의 포커스를 빼앗지 않음 (WS_EX_NOACTIVATE)
    class DeckForm : Form
    {
        public static DeckForm Instance;
        public const float Pad = 8, Bez = 28;
        public static readonly Color DeckBg = Color.FromArgb(46, 48, 56);
        public const string PaletteFormat = "EasyDeck.Kind";

        public bool Editing { get; private set; }

        readonly Dictionary<string, TimerRun> timers = new Dictionary<string, TimerRun>();
        readonly WinTimer tick = new WinTimer { Interval = 250 };
        readonly WinTimer watch = new WinTimer { Interval = 300 };
        readonly WinTimer longPress = new WinTimer { Interval = 500 };
        readonly WinTimer pendingTap = new WinTimer();
        readonly ToolTip tip = new ToolTip { ShowAlways = true, InitialDelay = 400 };
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ContextMenuStrip widgetMenu = new ContextMenuStrip();
        readonly HotkeyWindow hotkeys = new HotkeyWindow();
        readonly Dictionary<int, string> hotkeyMap = new Dictionary<int, string>();
        int hotkeyCount, watchCount;
        string pendingTapId, lastFgApp, tipText;
        List<Page> layoutUndo;
        PaletteForm palette;
        public bool SuppressPalette;

        enum HK { None, Bezel, BezelButton, Widget, Resize, Empty }
        struct Hit { public HK Kind; public string Id; public int Cx, Cy, Btn; }

        class BezelBtn { public char Glyph; public string Fallback, Tip; public Action Run; public bool Active; public int Pri; public RectangleF Rect; }
        List<BezelBtn> bezelBtns = new List<BezelBtn>();

        // 마우스 상태
        Hit down;
        Point downPt, downScreen, winStart;
        bool windowDrag, moved, longFired, knobDragged, dragging, sliderPending, hover;
        float knobAccum;
        string pressedId, sliderId;
        DateTime lastSliderRun;
        RectangleF? ghost;
        Color ghostColor;
        Action applyGhost;
        PointF dragDelta;
        List<DropItem> dragItems;

        public DeckForm()
        {
            Instance = this;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = DeckBg;
            AllowDrop = true;
            Text = "EasyDeck";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            tick.Tick += (s, e) => TimerTick();
            tick.Start();
            watch.Tick += (s, e) => Watch();
            watch.Start();
            longPress.Tick += (s, e) => { longPress.Stop(); LongFire(); };
            pendingTap.Tick += (s, e) =>
            {
                pendingTap.Stop();
                var w = Store.FindWidget(pendingTapId);
                pendingTapId = null;
                if (w != null) Run(w.Act("tap"), Vars(w));
            };
            hotkeys.Pressed += OnHotkey;
            Store.Changed += Rebuild;
            Rebuild();
            PlaceInitially();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        Settings S => Store.P.settings;
        public float K => DeviceDpi / 96f;
        float C => (float)S.cellSize;
        float G => (float)S.gap;

        // MARK: 배치 계산

        public RectangleF CellRect(int x, int y, int w, int h)
        {
            float k = K, c = C, g = G;
            return new RectangleF((Pad + x * (c + g)) * k, (Bez + y * (c + g)) * k, (w * c + (w - 1) * g) * k, (h * c + (h - 1) * g) * k);
        }

        public Size DeckSize()
        {
            var p = Store.Page;
            float c = C, g = G;
            float wv = Pad * 2 + p.cols * c + (p.cols - 1) * g;
            float hv = Bez + p.rows * c + (p.rows - 1) * g + Pad;
            return new Size((int)Math.Ceiling(wv * K), (int)Math.Ceiling(hv * K));
        }

        (int, int) CellAt(Point pt, bool clamp = false)
        {
            float k = K, c = C, g = G;
            int x = (int)Math.Floor((pt.X / k - Pad + g / 2) / (c + g));
            int y = (int)Math.Floor((pt.Y / k - Bez + g / 2) / (c + g));
            var p = Store.Page;
            if (clamp) return (Math.Max(0, Math.Min(p.cols - 1, x)), Math.Max(0, Math.Min(p.rows - 1, y)));
            if (x < 0 || y < 0 || x >= p.cols || y >= p.rows) return (-1, -1);
            return (x, y);
        }

        Widget WidgetAt(Point pt)
        {
            var p = Store.Page;
            for (int i = p.widgets.Count - 1; i >= 0; i--)
            {
                var w = p.widgets[i];
                if (CellRect(w.x, w.y, w.w, w.h).Contains(pt)) return w;
            }
            return null;
        }

        Hit HitTest(Point pt)
        {
            var hit = new Hit();
            if (pt.Y < Bez * K)
            {
                for (int i = 0; i < bezelBtns.Count; i++)
                    if (bezelBtns[i].Rect.Contains(pt)) { hit.Kind = HK.BezelButton; hit.Btn = i; return hit; }
                hit.Kind = HK.Bezel;
                return hit;
            }
            var (cx, cy) = CellAt(pt, true);
            hit.Cx = cx; hit.Cy = cy;
            var w = WidgetAt(pt);
            if (w != null)
            {
                hit.Id = w.id;
                var r = CellRect(w.x, w.y, w.w, w.h);
                hit.Kind = Editing && pt.X > r.Right - 16 * K && pt.Y > r.Bottom - 16 * K ? HK.Resize : HK.Widget;
                return hit;
            }
            var (ex, ey) = CellAt(pt);
            if (Editing && ex >= 0) { hit.Kind = HK.Empty; hit.Cx = ex; hit.Cy = ey; return hit; }
            hit.Kind = HK.None;
            return hit;
        }

        // MARK: 다시 그리기·위치

        public void Rebuild()
        {
            if (IsDisposed) return;
            var sz = DeckSize();
            if (ClientSize != sz) ClientSize = sz;
            if (TopMost != S.alwaysOnTop) TopMost = S.alwaysOnTop;
            foreach (var kv in timers.ToList())
            {
                var w = Store.FindWidget(kv.Key);
                if (w == null) timers.Remove(kv.Key);
                else if (!kv.Value.Running && kv.Value.Remaining > w.duration) kv.Value.Remaining = w.duration;
            }
            if (IsHandleCreated) ClampOnScreen();
            RegisterHotkeys();
            if (palette != null && palette.Visible) palette.Reposition();
            Invalidate();
        }

        void PlaceInitially()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (S.frameX.HasValue && S.frameY.HasValue) Location = new Point((int)S.frameX.Value, (int)S.frameY.Value);
            else Location = new Point(wa.Right - Width - 24, wa.Top + 80);
            ClampOnScreen();
        }

        void ClampOnScreen()
        {
            var r = Bounds;
            bool visible = Screen.AllScreens.Any(s =>
            {
                var i = Rectangle.Intersect(s.WorkingArea, r);
                return i.Width >= 60 && i.Height >= 30;
            });
            if (!visible)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(Math.Max(wa.Left, wa.Right - Width - 24), wa.Top + 80);
            }
        }

        void SaveFrame() => Store.MutateSilently(p => { p.settings.frameX = Left; p.settings.frameY = Top; });

        // MARK: 그리기

        string Title()
        {
            var p = Store.Page;
            int n = Store.P.pages.Count;
            var t = n > 1 ? p.name + "  " + (Store.P.currentPage + 1) + "/" + n : p.name;
            return Editing ? "✎ 편집 중 · " + t : t;
        }

        void LayoutBezel(Graphics g)
        {
            var list = new List<BezelBtn>();
            if (Store.P.pages.Count > 1)
            {
                list.Add(new BezelBtn { Glyph = '', Fallback = "‹", Tip = "이전 페이지", Run = () => SwitchPage("prev"), Pri = 3 });
                list.Add(new BezelBtn { Glyph = '', Fallback = "›", Tip = "다음 페이지", Run = () => SwitchPage("next"), Pri = 3 });
            }
            list.Add(new BezelBtn { Glyph = Editing ? '' : '', Fallback = Editing ? "✓" : "✎", Tip = Editing ? "편집 끝" : "편집", Run = () => SetEditing(!Editing), Active = Editing, Pri = 0 });
            list.Add(new BezelBtn { Glyph = S.alwaysOnTop ? '' : '', Fallback = "▲", Tip = "항상 위", Run = TogglePin, Active = S.alwaysOnTop, Pri = 2 });
            list.Add(new BezelBtn { Glyph = '', Fallback = "⚙", Tip = "설정", Run = () => AppMain.OpenSettings(0), Pri = 0 });
            var menuBtn = new BezelBtn { Glyph = '', Fallback = "≡", Tip = "메뉴 (배치 바꾸기 등)", Pri = 1 };
            menuBtn.Run = () => ShowMainMenu(new Point((int)menuBtn.Rect.Left, (int)menuBtn.Rect.Bottom));
            list.Add(menuBtn);
            list.Add(new BezelBtn { Glyph = '', Fallback = "–", Tip = "숨기기", Run = HideDeck, Pri = 0 });

            float k = K, bw = 22 * k;
            float titleW = g.MeasureString(Title(), Fonts.Get(Fonts.UiFamily, 12.5f * k, FontStyle.Bold)).Width;
            float avail = Width - Pad * k * 2 - Math.Min(titleW, Width * 0.45f) - 6 * k;
            // 좁으면 덜 쓰는 버튼부터 숨김 (오른쪽 클릭 메뉴로 모두 쓸 수 있음)
            for (int pri = 0; pri <= 2 && list.Count * bw > avail; pri++)
                while (list.Count * bw > avail && list.Any(b => b.Pri == pri))
                    list.Remove(list.Last(b => b.Pri == pri));
            float x = Width - Pad * k - bw * list.Count;
            foreach (var b in list)
            {
                b.Rect = new RectangleF(x, (Bez * k - bw) / 2 + 1 * k, bw, bw);
                x += bw;
            }
            bezelBtns = list;
        }

        protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

        public void Render(Graphics g)
        {
            float k = K, fs = (float)S.fontScale;
            Renderer.Setup(g);
            g.Clear(DeckBg);
            LayoutBezel(g);

            float titleRight = bezelBtns.Count > 0 ? bezelBtns[0].Rect.Left - 4 * k : Width - Pad * k;
            Renderer.Text(g, Title(), Fonts.Get(Fonts.UiFamily, 12.5f * k, FontStyle.Bold),
                Editing ? Renderer.Yellow : Color.FromArgb(230, 255, 255, 255),
                new RectangleF(Pad * k + 3 * k, 0, Math.Max(10, titleRight - Pad * k - 3 * k), Bez * k + 2 * k), StringAlignment.Near);
            var hoverPt = PointToClient(Cursor.Position);
            foreach (var b in bezelBtns)
            {
                var col = b.Active ? Renderer.Yellow : Color.FromArgb(b.Rect.Contains(hoverPt) ? 255 : 190, 255, 255, 255);
                if (Fonts.SymbolFamily != null) Renderer.Text(g, b.Glyph.ToString(), Fonts.Get(Fonts.SymbolFamily, 13 * k), col, b.Rect);
                else Renderer.Text(g, b.Fallback, Fonts.Get(Fonts.UiFamily, 14 * k, FontStyle.Bold), col, b.Rect);
            }

            var p = Store.Page;
            if (Editing)
            {
                var used = new bool[p.cols, p.rows];
                foreach (var w in p.widgets)
                    for (int x = w.x; x < Math.Min(p.cols, w.x + w.w); x++)
                        for (int y = w.y; y < Math.Min(p.rows, w.y + w.h); y++)
                            used[x, y] = true;
                using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1.2f * k) { DashStyle = DashStyle.Dash })
                    for (int x = 0; x < p.cols; x++)
                        for (int y = 0; y < p.rows; y++)
                        {
                            if (used[x, y]) continue;
                            var r = CellRect(x, y, 1, 1);
                            using (var path = Renderer.Round(RectangleF.Inflate(r, -1, -1), 11 * k)) g.DrawPath(pen, path);
                            Renderer.Text(g, "+", Fonts.Get(Fonts.UiFamily, 22 * k), Color.FromArgb(110, 255, 255, 255), r);
                        }
            }

            Widget lifted = null;
            foreach (var w in p.widgets)
            {
                if (dragging && down.Kind == HK.Widget && down.Id == w.id) { lifted = w; continue; }
                timers.TryGetValue(w.id, out var t);
                Renderer.DrawWidget(g, w, CellRect(w.x, w.y, w.w, w.h), k, fs, Editing, pressedId == w.id, t);
            }

            if (ghost.HasValue)
            {
                using (var path = Renderer.Round(ghost.Value, 11 * k))
                {
                    using (var b = new SolidBrush(Color.FromArgb(70, ghostColor))) g.FillPath(b, path);
                    using (var pen = new Pen(ghostColor, 2 * k)) g.DrawPath(pen, path);
                }
            }

            if (lifted != null)
            {
                var r = CellRect(lifted.x, lifted.y, lifted.w, lifted.h);
                r.Offset(dragDelta);
                timers.TryGetValue(lifted.id, out var t);
                Renderer.DrawWidget(g, lifted, r, k, fs, true, false, t);
            }
        }

        public Bitmap Snapshot()
        {
            var b = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            using (var g = Graphics.FromImage(b)) Render(g);
            return b;
        }

        // MARK: 마우스

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            down = HitTest(e.Location);
            downPt = e.Location;
            downScreen = Cursor.Position;
            moved = longFired = knobDragged = dragging = false;
            knobAccum = 0;
            ghost = null;
            if (down.Kind == HK.Bezel && !S.locked) { windowDrag = true; winStart = Location; return; }
            if (down.Kind == HK.Widget && !Editing)
            {
                var w = Store.FindWidget(down.Id);
                if (w == null) return;
                pressedId = w.id;
                if (w.kind == WidgetKind.slider) { sliderId = w.id; SetSlider(w, e.Location, false); }
                else if (w.kind == WidgetKind.timer || !string.IsNullOrWhiteSpace(w.Act("long"))) longPress.Start();
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateTip(e.Location);
            if (e.Button != MouseButtons.Left) { if (e.Y < Bez * K) Invalidate(new Rectangle(0, 0, Width, (int)(Bez * K) + 2)); return; }
            int dx = Cursor.Position.X - downScreen.X, dy = Cursor.Position.Y - downScreen.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 4) moved = true;
            if (windowDrag)
            {
                Location = new Point(winStart.X + dx, winStart.Y + dy);
                if (palette != null && palette.Visible) palette.Reposition();
                return;
            }
            if (sliderId != null)
            {
                var s = Store.FindWidget(sliderId);
                if (s != null) SetSlider(s, e.Location, false);
                return;
            }
            if (!Editing && down.Kind == HK.Widget)
            {
                var w = Store.FindWidget(down.Id);
                if (w == null) return;
                if (moved) longPress.Stop();
                if (w.kind == WidgetKind.knob)
                {
                    float step = 10 * K, total = downPt.Y - e.Y;
                    while (total - knobAccum >= step) { knobAccum += step; KnobStep(w, +1); knobDragged = true; }
                    while (total - knobAccum <= -step) { knobAccum -= step; KnobStep(w, -1); knobDragged = true; }
                }
                return;
            }
            if (Editing && (down.Kind == HK.Widget || down.Kind == HK.Resize) && moved)
            {
                dragging = true;
                UpdateEditGhost(e.Location);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                var w = e.Y > Bez * K ? WidgetAt(e.Location) : null;
                if (w != null) ShowWidgetMenu(w, e.Location); else ShowMainMenu(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            longPress.Stop();
            var now = HitTest(e.Location);
            try
            {
                if (windowDrag)
                {
                    windowDrag = false;
                    if (moved) { ClampOnScreen(); SaveFrame(); }
                    return;
                }
                if (down.Kind == HK.BezelButton)
                {
                    if (now.Kind == HK.BezelButton && now.Btn == down.Btn && down.Btn < bezelBtns.Count) bezelBtns[down.Btn].Run?.Invoke();
                    return;
                }
                if (sliderId != null)
                {
                    var s = Store.FindWidget(sliderId);
                    sliderId = null;
                    if (s != null) SetSlider(s, e.Location, true);
                    return;
                }
                if (Editing)
                {
                    if (dragging) { applyGhost?.Invoke(); return; }
                    if (down.Kind == HK.Widget || down.Kind == HK.Resize) { AppMain.OpenInspector(down.Id); return; }
                    if (down.Kind == HK.Empty)
                    {
                        var nw = Widget.Template(WidgetKind.button);
                        int cx = down.Cx, cy = down.Cy;
                        Store.Mutate(p => p.pages[p.currentPage].Place(nw, cx, cy));
                        AppMain.OpenInspector(nw.id);
                    }
                    return;
                }
                if (down.Kind == HK.Widget && !longFired)
                {
                    var w = Store.FindWidget(down.Id);
                    if (w == null) return;
                    if (w.kind == WidgetKind.knob) { if (!knobDragged) Run(w.Act("tap"), Vars(w)); }
                    else if (now.Kind == HK.Widget && now.Id == w.id) Press(w, false);
                }
            }
            finally
            {
                pressedId = null;
                dragging = false;
                ghost = null;
                applyGhost = null;
                down = default(Hit);
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int n = e.Delta / 120;
            if (n == 0) n = Math.Sign(e.Delta);
            if ((ModifierKeys & Keys.Control) != 0) { Zoom(n); return; }
            if (e.Y < Bez * K) { SwitchPage(n < 0 ? "next" : "prev"); return; }
            if (Editing) return;
            var w = WidgetAt(e.Location);
            if (w == null) return;
            switch (w.kind)
            {
                case WidgetKind.knob:
                    for (int i = 0; i < Math.Abs(n); i++) KnobStep(w, Math.Sign(n));
                    break;
                case WidgetKind.slider:
                    w.value = Settings.Clamp(Math.Round(w.value + n * 2), 0, 100);
                    SliderChanged(w);
                    Store.MutateSilently(_ => { });
                    Invalidate();
                    break;
                case WidgetKind.timer:
                    TimerAdjust(w, n * 60);
                    break;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Invalidate(new Rectangle(0, 0, Width, (int)(Bez * K) + 2));
        }

        void UpdateTip(Point pt)
        {
            string t = null;
            if (pt.Y < Bez * K)
                foreach (var b in bezelBtns) if (b.Rect.Contains(pt)) t = b.Tip;
            if (t == tipText) return;
            tipText = t;
            tip.SetToolTip(this, t ?? "");
        }

        void Zoom(int n) => Store.Mutate(p => p.settings.cellSize = Settings.Clamp(p.settings.cellSize + n * 4, 40, 180));

        void UpdateEditGhost(Point pt)
        {
            var p = Store.Page;
            var w = Store.FindWidget(down.Id);
            if (w == null) return;
            var (cx, cy) = CellAt(pt, true);
            if (down.Kind == HK.Resize)
            {
                int nw = Math.Max(1, cx - w.x + 1), nh = Math.Max(1, cy - w.y + 1);
                bool ok = p.Fits(w.x, w.y, nw, nh, w.id);
                ghost = CellRect(w.x, w.y, nw, nh);
                ghostColor = ok ? Renderer.Green : Renderer.Red;
                applyGhost = ok ? (Action)(() => Store.Mutate(_ => { w.w = nw; w.h = nh; if (w.kind == WidgetKind.slider) w.vertical = nh > nw; })) : null;
                return;
            }
            dragDelta = new PointF(pt.X - downPt.X, pt.Y - downPt.Y);
            int tx = cx - (down.Cx - w.x), ty = cy - (down.Cy - w.y);
            var partner = p.widgets.FirstOrDefault(o => o.id != w.id && o.x <= cx && cx < o.x + o.w && o.y <= cy && cy < o.y + o.h);
            if (partner != null && SwapOk(p, w, partner))
            {
                ghost = CellRect(partner.x, partner.y, w.w, w.h);
                ghostColor = Renderer.Blue;
                applyGhost = () => Store.Mutate(_ =>
                {
                    int ax = w.x, ay = w.y;
                    w.x = partner.x; w.y = partner.y;
                    partner.x = ax; partner.y = ay;
                });
                return;
            }
            bool fits = p.Fits(tx, ty, w.w, w.h, w.id);
            int gx = Math.Max(0, Math.Min(p.cols - 1, tx)), gy = Math.Max(0, Math.Min(p.rows - 1, ty));
            ghost = CellRect(gx, gy, Math.Min(w.w, p.cols - gx), Math.Min(w.h, p.rows - gy));
            ghostColor = fits ? Renderer.Green : Renderer.Red;
            applyGhost = fits ? (Action)(() => Store.Mutate(_ => { w.x = tx; w.y = ty; })) : null;
        }

        static bool SwapOk(Page p, Widget a, Widget b)
        {
            var ignore = new[] { a.id, b.id };
            bool overlap = b.x < a.x + b.w && a.x < b.x + a.w && b.y < a.y + b.h && a.y < b.y + a.h;
            return !overlap && p.Fits(b.x, b.y, a.w, a.h, ignore) && p.Fits(a.x, a.y, b.w, b.h, ignore);
        }

        // MARK: 위젯 동작

        static Dictionary<string, string> Vars(Widget w)
            => new Dictionary<string, string> { ["value"] = ((int)Math.Round(w.value)).ToString() };

        static void Run(string script, Dictionary<string, string> vars = null, bool waitMods = false)
            => ActionRunner.Run(script, vars, waitMods);

        public void Press(Widget w, bool fromHotkey)
        {
            switch (w.kind)
            {
                case WidgetKind.toggle:
                    w.isOn = !w.isOn;
                    Store.MutateSilently(_ => { });
                    Run(w.isOn ? w.Act("on") : w.Act("off"), Vars(w), fromHotkey);
                    Invalidate();
                    break;
                case WidgetKind.timer:
                    TimerStartPause(w);
                    break;
                case WidgetKind.knob:
                    Run(w.Act("tap"), Vars(w), fromHotkey);
                    break;
                case WidgetKind.slider:
                    break;
                default:
                    if (fromHotkey || string.IsNullOrWhiteSpace(w.Act("double")))
                    {
                        Run(w.Act("tap"), Vars(w), fromHotkey);
                        break;
                    }
                    if (pendingTapId == w.id)
                    {
                        pendingTap.Stop();
                        pendingTapId = null;
                        Run(w.Act("double"), Vars(w));
                        break;
                    }
                    if (pendingTapId != null)
                    {
                        pendingTap.Stop();
                        var prev = Store.FindWidget(pendingTapId);
                        if (prev != null) Run(prev.Act("tap"), Vars(prev));
                    }
                    pendingTapId = w.id;
                    pendingTap.Interval = Math.Max(150, Math.Min(600, SystemInformation.DoubleClickTime));
                    pendingTap.Start();
                    break;
            }
        }

        void LongFire()
        {
            if (Editing || down.Kind != HK.Widget) return;
            var w = Store.FindWidget(down.Id);
            if (w == null) return;
            longFired = true;
            pressedId = null;
            if (w.kind == WidgetKind.timer) TimerReset(w);
            else Run(w.Act("long"), Vars(w));
            Invalidate();
        }

        void KnobStep(Widget w, int dir)
        {
            if (w.infinite)
            {
                w.value = ((w.value + dir * 5) % 100 + 100) % 100;
                var act = dir > 0 ? w.Act("cw") : w.Act("ccw");
                if (string.IsNullOrWhiteSpace(act) && w.bind == "volume") act = dir > 0 ? "volume +3" : "volume -3";
                Run(act, Vars(w));
            }
            else
            {
                w.value = Settings.Clamp(w.value + dir * 5, 0, 100);
                if (w.bind == "volume") { var v = w.value; ThreadPool.QueueUserWorkItem(_ => Audio.Set(v)); }
                Run(dir > 0 ? w.Act("cw") : w.Act("ccw"), Vars(w));
                Run(w.Act("change"), Vars(w));
            }
            Store.MutateSilently(_ => { });
            Invalidate();
        }

        void SetSlider(Widget w, Point pt, bool final)
        {
            var r = CellRect(w.x, w.y, w.w, w.h);
            var tr = Renderer.TrackRect(w, r, K, (float)S.fontScale);
            double v = w.vertical ? (tr.Bottom - pt.Y) / tr.Height * 100 : (pt.X - tr.X) / tr.Width * 100;
            v = Math.Round(Settings.Clamp(v, 0, 100));
            bool changed = Math.Abs(v - w.value) >= 0.5;
            w.value = v;
            if (changed)
            {
                Invalidate();
                if (final || (DateTime.Now - lastSliderRun).TotalMilliseconds > 80) { lastSliderRun = DateTime.Now; sliderPending = false; SliderChanged(w); }
                else sliderPending = true;
            }
            else if (final && sliderPending) { sliderPending = false; SliderChanged(w); }
            if (final) Store.MutateSilently(_ => { });
        }

        void SliderChanged(Widget w)
        {
            var v = w.value;
            if (w.bind == "volume") ThreadPool.QueueUserWorkItem(_ => Audio.Set(v));
            Run(w.Act("change"), Vars(w));
        }

        // MARK: 타이머

        TimerRun T(Widget w)
        {
            if (!timers.TryGetValue(w.id, out var t)) { t = new TimerRun { Remaining = w.duration }; timers[w.id] = t; }
            return t;
        }

        void TimerStartPause(Widget w)
        {
            var t = T(w);
            if (t.Running) { t.Remaining = Math.Max(0, (t.EndAt - DateTime.Now).TotalSeconds); t.Running = false; }
            else
            {
                if (t.Remaining <= 0.5) t.Remaining = w.duration;
                t.EndAt = DateTime.Now.AddSeconds(t.Remaining);
                t.Running = true;
            }
            Invalidate();
        }

        void TimerReset(Widget w)
        {
            var t = T(w);
            t.Running = false;
            t.Remaining = w.duration;
            Toast.Say("타이머를 처음으로 돌렸습니다");
            Invalidate();
        }

        void TimerAdjust(Widget w, int sec)
        {
            var t = T(w);
            if (t.Running)
            {
                t.EndAt = t.EndAt.AddSeconds(sec);
                if (t.EndAt < DateTime.Now) t.EndAt = DateTime.Now;
            }
            else
            {
                w.duration = Math.Max(10, w.duration + sec);
                t.Remaining = w.duration;
                Store.MutateSilently(_ => { });
            }
            Invalidate();
        }

        void TimerTick()
        {
            bool any = false;
            foreach (var kv in timers.ToList())
            {
                var t = kv.Value;
                if (!t.Running) continue;
                any = true;
                t.Remaining = (t.EndAt - DateTime.Now).TotalSeconds;
                if (t.Remaining > 0) continue;
                t.Running = false;
                var w = Store.FindWidget(kv.Key);
                t.Remaining = w?.duration ?? 0;
                if (w != null) Run(w.Act("end"), Vars(w));
            }
            if (any) Invalidate();
        }

        // MARK: 마우스 올림·앱 따라 페이지 바꾸기

        void Watch()
        {
            if (!Visible) return;
            var pos = Cursor.Position;
            bool inside = Bounds.Contains(pos) || (palette != null && palette.Visible && palette.Bounds.Contains(pos));
            if (inside != hover)
            {
                hover = inside;
                if (hover) SyncVolume();
            }
            double target = hover || Editing || dragItems != null ? 1.0 : Settings.Clamp(S.idleOpacity, 0.15, 1);
            if (Math.Abs(Opacity - target) > 0.01) Opacity = target;
            if (++watchCount % 2 == 0 && S.autoSwitch && !Editing) AutoSwitch();
        }

        void AutoSwitch()
        {
            var name = ForegroundProcessName();
            if (name == null || name == lastFgApp) return;
            lastFgApp = name;
            if (name == "easydeck") return;
            var pages = Store.P.pages;
            int idx = pages.FindIndex(p => p.apps.Any(a => NormApp(a) == name));
            if (idx >= 0 && idx != Store.P.currentPage) GoToPage(idx, false);
        }

        public static string NormApp(string a)
        {
            a = (a ?? "").Trim().ToLowerInvariant();
            return a.EndsWith(".exe") ? a.Substring(0, a.Length - 4) : a;
        }

        static string ForegroundProcessName()
        {
            try
            {
                var h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return null;
                Native.GetWindowThreadProcessId(h, out var pid);
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant();
            }
            catch { return null; }
        }

        void SyncVolume()
        {
            var ws = Store.P.pages.SelectMany(p => p.widgets)
                .Where(w => w.bind == "volume" && (w.kind == WidgetKind.slider || (w.kind == WidgetKind.knob && !w.infinite))).ToList();
            if (ws.Count == 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var v = Audio.Get();
                if (v == null) return;
                Ui.Post(() => { foreach (var w in ws) w.value = v.Value; Invalidate(); });
            });
        }

        // MARK: 페이지·배치

        public void SwitchPage(string arg)
        {
            var pages = Store.P.pages;
            int n = pages.Count, cur = Store.P.currentPage, to;
            var a = (arg ?? "").Trim();
            if (a.Equals("next", StringComparison.OrdinalIgnoreCase)) to = (cur + 1) % n;
            else if (a.Equals("prev", StringComparison.OrdinalIgnoreCase) || a.Equals("previous", StringComparison.OrdinalIgnoreCase)) to = (cur - 1 + n) % n;
            else if (int.TryParse(a, out var num)) to = num - 1;
            else to = pages.FindIndex(p => p.name.Equals(a, StringComparison.OrdinalIgnoreCase));
            if (to < 0 || to >= n) { Toast.Say("페이지를 찾을 수 없습니다: " + a); return; }
            GoToPage(to, true);
        }

        public void GoToPage(int i, bool toast)
        {
            if (i == Store.P.currentPage || i < 0 || i >= Store.P.pages.Count) return;
            Store.Mutate(p => p.currentPage = i);
            if (toast) Toast.Say(Store.Page.name);
        }

        public bool CanUndoLayout => layoutUndo != null;

        public void ChangeLayout(string arg)
        {
            switch ((arg ?? "").Trim().ToLowerInvariant())
            {
                case "row": case "horizontal": case "가로":
                    ApplyLayout(p => p.AsSingleRow(), "모든 페이지를 가로 한 줄로 바꿨습니다"); break;
                case "column": case "col": case "vertical": case "세로":
                    ApplyLayout(p => p.AsSingleColumn(), "모든 페이지를 세로 한 줄로 바꿨습니다"); break;
                case "flip": case "transpose": case "rotate":
                    ApplyLayout(p => p.Transposed(), "가로와 세로를 바꿨습니다"); break;
                case "undo":
                    {
                        if (layoutUndo == null) { Toast.Say("되돌릴 배치가 없습니다"); return; }
                        var undo = layoutUndo;
                        layoutUndo = null;
                        Store.Mutate(prof =>
                        {
                            foreach (var saved in undo)
                            {
                                var pg = prof.pages.FirstOrDefault(x => x.id == saved.id);
                                if (pg == null) continue;
                                pg.cols = saved.cols; pg.rows = saved.rows;
                                foreach (var sw in saved.widgets)
                                {
                                    var w = pg.widgets.FirstOrDefault(x => x.id == sw.id);
                                    if (w == null) continue;
                                    w.x = sw.x; w.y = sw.y; w.w = sw.w; w.h = sw.h; w.vertical = sw.vertical;
                                }
                                pg.cols = Math.Max(pg.cols, pg.MinCols); pg.rows = Math.Max(pg.rows, pg.MinRows);
                            }
                        });
                        Toast.Say("배치를 되돌렸습니다");
                        break;
                    }
                default:
                    Toast.Say("layout row|column|flip|undo");
                    break;
            }
        }

        void ApplyLayout(Func<Page, Page> f, string msg)
        {
            layoutUndo = Store.P.pages.Select(p => p.Clone()).ToList();
            Store.Mutate(prof => { for (int i = 0; i < prof.pages.Count; i++) prof.pages[i] = f(prof.pages[i]); });
            Toast.Say(msg + "\n되돌리기: ≡ 메뉴 › 배치 바꾸기 › 되돌리기");
        }

        public void AddPage()
        {
            Store.Mutate(p => { p.pages.Add(new Page("새 페이지 " + (p.pages.Count + 1))); p.currentPage = p.pages.Count - 1; });
            Toast.Say("새 페이지를 만들었습니다");
        }

        // MARK: 보이기·숨기기·편집

        public void ShowDeck()
        {
            if (!Visible) Show();
            if (S.alwaysOnTop) { TopMost = false; TopMost = true; }
            if (Editing && palette != null && !palette.Visible) palette.Show();
            Invalidate();
        }

        public void HideDeck()
        {
            SetEditing(false);
            Hide();
            var hk = KeyMap.PrettySpec(S.hotkey);
            Toast.Say(hk.Length > 0 ? "덱을 숨겼습니다 · " + hk + " 로 다시 보이기" : "덱을 숨겼습니다 · 알림 영역 아이콘으로 다시 보이기");
        }

        public void ToggleVisible()
        {
            if (Visible) HideDeck(); else ShowDeck();
        }

        public void SetEditing(bool on)
        {
            if (Editing == on) return;
            Editing = on;
            pendingTapId = null;
            if (on && !SuppressPalette)
            {
                if (palette == null || palette.IsDisposed) palette = new PaletteForm(this);
                palette.Reposition();
                palette.Show();
            }
            else palette?.Hide();
            Opacity = 1;
            Invalidate();
        }

        public PaletteForm Palette => palette ?? (palette = new PaletteForm(this));

        void TogglePin() => Store.Mutate(p => p.settings.alwaysOnTop = !p.settings.alwaysOnTop);

        // MARK: 메뉴

        public void FillMainMenu(ToolStripItemCollection items)
        {
            items.Clear();
            var hk = KeyMap.PrettySpec(S.hotkey);
            items.Add(new ToolStripMenuItem(Visible ? "덱 숨기기" : "덱 보이기", null, (s, e) => ToggleVisible()) { ShortcutKeyDisplayString = hk });
            items.Add(new ToolStripMenuItem("편집 모드", null, (s, e) => { ShowDeck(); SetEditing(!Editing); }) { Checked = Editing });

            var pages = new ToolStripMenuItem("페이지");
            for (int i = 0; i < Store.P.pages.Count; i++)
            {
                int idx = i;
                pages.DropDownItems.Add(new ToolStripMenuItem(Store.P.pages[i].name, null, (s, e) => GoToPage(idx, true)) { Checked = i == Store.P.currentPage });
            }
            pages.DropDownItems.Add(new ToolStripSeparator());
            pages.DropDownItems.Add("새 페이지 추가", null, (s, e) => AddPage());
            items.Add(pages);

            var lay = new ToolStripMenuItem("배치 바꾸기 (모든 페이지)");
            lay.DropDownItems.Add("가로 한 줄", null, (s, e) => ChangeLayout("row"));
            lay.DropDownItems.Add("세로 한 줄", null, (s, e) => ChangeLayout("column"));
            lay.DropDownItems.Add("가로 ↔ 세로", null, (s, e) => ChangeLayout("flip"));
            lay.DropDownItems.Add(new ToolStripSeparator());
            lay.DropDownItems.Add(new ToolStripMenuItem("되돌리기", null, (s, e) => ChangeLayout("undo")) { Enabled = CanUndoLayout });
            items.Add(lay);

            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem("항상 위에 표시", null, (s, e) => TogglePin()) { Checked = S.alwaysOnTop });
            items.Add(new ToolStripMenuItem("위치 고정", null, (s, e) => Store.Mutate(p => p.settings.locked = !p.settings.locked)) { Checked = S.locked });
            items.Add(new ToolStripSeparator());
            items.Add("설정…", null, (s, e) => AppMain.OpenSettings(0));
            items.Add("profile.json 열기", null, (s, e) => AppMain.OpenProfileJson());
            items.Add("데이터 폴더 열기", null, (s, e) => ActionRunner.ShellOpen(Store.Dir));
            items.Add("도움말", null, (s, e) => AppMain.OpenSettings(2));
            items.Add(new ToolStripSeparator());
            items.Add("끝내기", null, (s, e) => AppMain.Quit());
        }

        void ShowMainMenu(Point at)
        {
            FillMainMenu(menu.Items);
            menu.Show(this, at);
        }

        void ShowWidgetMenu(Widget w, Point at)
        {
            var items = widgetMenu.Items;
            items.Clear();
            items.Add("편집…", null, (s, e) => AppMain.OpenInspector(w.id));
            items.Add("테스트 실행", null, (s, e) => Press(w, false));
            items.Add("복제", null, (s, e) =>
            {
                var c = w.Clone();
                c.id = Widget.NewId();
                Store.Mutate(p => p.pages[p.currentPage].Place(c, w.x + w.w, w.y));
            });
            items.Add("삭제", null, (s, e) =>
            {
                var name = string.IsNullOrEmpty(w.label) ? KindInfo.Title(w.kind) : w.label;
                if (MessageBox.Show("‘" + name + "’ 위젯을 지울까요?", "EasyDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Store.Mutate(p => { foreach (var pg in p.pages) pg.widgets.RemoveAll(x => x.id == w.id); });
            });
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem("편집 모드", null, (s, e) => SetEditing(!Editing)) { Checked = Editing });
            widgetMenu.Show(this, at);
        }

        // MARK: 전역 단축키

        void RegisterHotkeys()
        {
            for (int i = 1; i <= hotkeyCount; i++) Native.UnregisterHotKey(hotkeys.Handle, i);
            hotkeyMap.Clear();
            int id = 0;
            void reg(string spec, string target)
            {
                if (string.IsNullOrWhiteSpace(spec)) return;
                if (!KeyMap.TryParse(spec, out var c)) { Log.Write("단축키를 읽을 수 없음: " + spec); return; }
                id++;
                hotkeyMap[id] = target;
                if (!Native.RegisterHotKey(hotkeys.Handle, id, KeyMap.Mods(c) | 0x4000, (uint)c.Vk))
                    Log.Write("단축키 등록 실패 (다른 프로그램이 쓰는 중일 수 있음): " + spec);
            }
            reg(S.hotkey, "");
            foreach (var w in Store.P.pages.SelectMany(p => p.widgets)) reg(w.hotkey, w.id);
            hotkeyCount = id;
        }

        void OnHotkey(int id)
        {
            if (!hotkeyMap.TryGetValue(id, out var target)) return;
            if (target == "") { ToggleVisible(); return; }
            var w = Store.FindWidget(target);
            if (w != null) Press(w, true);
        }

        // MARK: 끌어다 놓기

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            dragItems = DropMaker.Items(e.Data);
            Opacity = 1;
            DragUpdate(e);
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            DragUpdate(e);
        }

        void DragUpdate(DragEventArgs e)
        {
            if (dragItems == null || dragItems.Count == 0) { e.Effect = DragDropEffects.None; return; }
            e.Effect = (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                : (e.AllowedEffect & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Move;
            var pt = PointToClient(new Point(e.X, e.Y));
            var first = dragItems[0];
            var w = pt.Y > Bez * K ? WidgetAt(pt) : null;
            RectangleF? g = null;
            if (w != null && dragItems.Count == 1 && first.Kind == null)
            {
                g = CellRect(w.x, w.y, w.w, w.h);
                ghostColor = Renderer.Blue;
            }
            else
            {
                var (cx, cy) = CellAt(pt);
                if (cx >= 0)
                {
                    var tw = first.Kind != null ? Widget.Template(KindInfo.Parse(first.Kind)) : new Widget();
                    var p = Store.Page;
                    bool fits = p.Fits(cx, cy, tw.w, tw.h) || p.Fits(cx, cy, 1, 1);
                    g = CellRect(cx, cy, p.Fits(cx, cy, tw.w, tw.h) ? tw.w : 1, p.Fits(cx, cy, tw.w, tw.h) ? tw.h : 1);
                    ghostColor = fits ? Renderer.Green : Renderer.Red;
                }
            }
            if (g != ghost) { ghost = g; Invalidate(); }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            dragItems = null;
            ghost = null;
            Invalidate();
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var items = dragItems ?? DropMaker.Items(e.Data);
            dragItems = null;
            ghost = null;
            Invalidate();
            if (items.Count == 0) return;
            var pt = PointToClient(new Point(e.X, e.Y));
            var w = pt.Y > Bez * K ? WidgetAt(pt) : null;
            var (cx, cy) = CellAt(pt);
            if (w != null && items.Count == 1 && items[0].Kind == null)
            {
                var it = items[0];
                BeginInvoke((Action)(() => ShowDropMenu(w, it, pt)));
                return;
            }
            BeginInvoke((Action)(() => AddItems(items, cx, cy)));
        }

        public void AddItems(List<DropItem> items, int cx, int cy)
        {
            var made = new List<Widget>();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (var it in items)
                {
                    try { var w = DropMaker.WidgetFor(it); if (w != null) made.Add(w); }
                    catch (Exception ex) { Log.Write("끌어 놓기 실패: " + ex.Message); }
                }
            }
            finally { Cursor = Cursors.Default; }
            if (made.Count == 0) { Toast.Say("이 항목으로는 버튼을 만들 수 없습니다"); return; }
            Store.Mutate(prof =>
            {
                var pg = prof.pages[prof.currentPage];
                int x = cx, y = cy;
                foreach (var w in made)
                {
                    var placed = pg.Place(w, x, y);
                    x = placed.x + placed.w; y = placed.y;
                }
            });
            if (made[0].kind == WidgetKind.button && items[0].Kind == null)
                Toast.Say(made.Count == 1 ? "‘" + made[0].label + "’ 버튼을 만들었습니다" : "버튼 " + made.Count + "개를 만들었습니다");
        }

        void ShowDropMenu(Widget w, DropItem it, Point pt)
        {
            var m = new ContextMenuStrip();
            Widget nw = null;
            try { nw = DropMaker.WidgetFor(it); } catch (Exception e) { Log.Write("끌어 놓기 실패: " + e.Message); }
            var act = nw?.Act("tap") ?? "";
            if (it.File != null && Icons.IsImage(it.File))
                m.Items.Add("이 그림을 아이콘으로 쓰기", null, (s, e) =>
                {
                    var name = Icons.ImportImage(it.File);
                    if (name != null) Store.UpdateWidget(w.id, x => x.icon = name);
                });
            if (act.Length > 0 && (w.kind == WidgetKind.button || w.kind == WidgetKind.knob))
            {
                m.Items.Add("클릭 동작을 이것으로 바꾸기", null, (s, e) => Store.UpdateWidget(w.id, x => x.actions["tap"] = act));
                m.Items.Add("클릭 동작 뒤에 덧붙이기", null, (s, e) => Store.UpdateWidget(w.id, x => x.actions["tap"] = (x.Act("tap").TrimEnd() + "\n" + act).Trim()));
            }
            if (nw != null)
                m.Items.Add("새 버튼으로 추가", null, (s, e) => Store.Mutate(p => p.pages[p.currentPage].Place(nw, -1, -1)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("취소");
            m.Show(this, pt);
        }

        public void AddKind(WidgetKind k) => AddItems(new List<DropItem> { new DropItem { Kind = k.ToString() } }, -1, -1);

        public bool AllowClose;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideDeck(); return; }
            base.OnFormClosing(e);
        }
    }

    /// 전역 단축키를 받는 보이지 않는 창
    class HotkeyWindow : NativeWindow
    {
        public event Action<int> Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) Pressed?.Invoke(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    // MARK: 끌어다 놓은 것 → 버튼

    class DropItem
    {
        public string Kind, File, Url, Title, Text;
    }

    static class DropMaker
    {
        public static List<DropItem> Items(IDataObject d)
        {
            var list = new List<DropItem>();
            try
            {
                if (d.GetDataPresent(DeckForm.PaletteFormat))
                {
                    list.Add(new DropItem { Kind = d.GetData(DeckForm.PaletteFormat) as string ?? "button" });
                    return list;
                }
                if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files)
                {
                    foreach (var f in files) list.Add(new DropItem { File = f });
                    if (list.Count > 0) return list;
                }
                var url = ReadUrl(d);
                string text = null;
                if (d.GetDataPresent(DataFormats.UnicodeText)) text = d.GetData(DataFormats.UnicodeText) as string;
                else if (d.GetDataPresent(DataFormats.Text)) text = d.GetData(DataFormats.Text) as string;
                if (url == null && text != null && LooksLikeUrl(text.Trim())) url = text.Trim();
                if (url != null)
                {
                    var title = text != null && !LooksLikeUrl(text.Trim()) && text.Trim().Length <= 40 ? text.Trim() : null;
                    list.Add(new DropItem { Url = url, Title = title });
                    return list;
                }
                if (!string.IsNullOrWhiteSpace(text)) list.Add(new DropItem { Text = text });
            }
            catch (Exception e) { Log.Write("끌어 놓은 내용 읽기 실패: " + e.Message); }
            return list;
        }

        static string ReadUrl(IDataObject d)
        {
            foreach (var fmt in new[] { "UniformResourceLocatorW", "UniformResourceLocator" })
            {
                if (!d.GetDataPresent(fmt)) continue;
                var o = d.GetData(fmt);
                string s = null;
                if (o is MemoryStream ms)
                {
                    var bytes = ms.ToArray();
                    s = fmt.EndsWith("W") ? Encoding.Unicode.GetString(bytes) : Encoding.Default.GetString(bytes);
                }
                else if (o is string str) s = str;
                s = OneLine(s);
                if (LooksLikeUrl(s)) return s;
            }
            return null;
        }

        static bool LooksLikeUrl(string s)
            => (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
               && s.IndexOfAny(new[] { ' ', '\n', '\r', '\t' }) < 0;

        static string Shorten(string s, int n) => s.Length > n ? s.Substring(0, n) + "…" : s;

        /// 한 줄 동작에 넣는 값: 줄바꿈·제어 문자를 없앰
        public static string OneLine(string s) => new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

        public static Widget WidgetFor(DropItem it)
        {
            if (it.Kind != null) return Widget.Template(KindInfo.Parse(it.Kind));
            if (it.File != null)
            {
                var p = it.File;
                bool dir = Directory.Exists(p);
                if (!dir && !File.Exists(p)) return null;
                var ext = Path.GetExtension(p).ToLowerInvariant();
                var w = new Widget
                {
                    label = Shorten(dir ? new DirectoryInfo(p).Name : Path.GetFileNameWithoutExtension(p), 14),
                    color = dir ? "#2B8A3E" : (ext == ".exe" || ext == ".lnk" || ext == ".url" || ext == ".appref-ms") ? "#1971C2" : "#343A40",
                };
                if (p.IndexOfAny(new[] { '\r', '\n' }) >= 0) return null;
                w.actions["tap"] = "open " + p;
                using (var bmp = Icons.FileIcon(p, 128, Icons.IsImage(p)))
                    if (bmp != null) w.icon = Icons.SaveIcon(bmp, p);
                if (string.IsNullOrEmpty(w.icon)) w.icon = dir ? "sf:folder" : "sf:doc";
                return w;
            }
            if (it.Url != null)
            {
                var host = "";
                try { host = new Uri(it.Url).Host; } catch { }
                if (host.StartsWith("www.")) host = host.Substring(4);
                var w = new Widget { label = Shorten(it.Title ?? (host.Length > 0 ? host : "링크"), 14), icon = "sf:globe", color = "#1971C2" };
                var url = OneLine(it.Url);
                if (!LooksLikeUrl(url)) return null;
                w.actions["tap"] = "open " + url;
                return w;
            }
            if (it.Text != null)
            {
                var t = new string(it.Text.Replace("\r\n", "\n").Replace('\r', '\n')
                    .Where(c => c == '\n' || c == '\t' || !char.IsControl(c)).ToArray()).Trim('\n', ' ');
                if (t.Length == 0) return null;
                bool single = t.IndexOf('\n') < 0 && t.Length <= 60;
                var w = new Widget { label = Shorten(t.Split('\n')[0].Trim(), 8), icon = "sf:text.quote", color = "#5F3DC4" };
                w.actions["tap"] = single ? "type " + t : "paste " + t.Replace("\n", "\\n");
                return w;
            }
            return null;
        }
    }

    // MARK: 편집 팔레트 (끌어서 추가)

    class PaletteForm : Form
    {
        readonly DeckForm deck;
        static readonly WidgetKind[] kinds = { WidgetKind.button, WidgetKind.toggle, WidgetKind.knob, WidgetKind.slider, WidgetKind.timer };
        static readonly char[] glyphs = { '', '', '', '', '' };
        int downIdx = -1;
        Point downPt;
        bool dragged;

        public PaletteForm(DeckForm d)
        {
            deck = d;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = DeckForm.DeckBg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = PSize;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW; return cp; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle); }

        float K => DeviceDpi / 96f;
        Size PSize => new Size((int)(88 * K), (int)((30 + kinds.Length * 64 + 58) * K));
        RectangleF TileRect(int i) => new RectangleF(16 * K, (30 + i * 64) * K, 56 * K, 56 * K);

        public void Reposition()
        {
            Size = PSize;
            var db = deck.Bounds;
            var scr = Screen.FromRectangle(db).WorkingArea;
            int gap = (int)(8 * K);
            int x = db.Right + gap;
            if (x + Width > scr.Right) x = db.Left - Width - gap;
            int y = Math.Max(scr.Top, Math.Min(db.Top, scr.Bottom - Height));
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

        public void Render(Graphics g)
        {
            float k = K;
            Renderer.Setup(g);
            g.Clear(DeckForm.DeckBg);
            Renderer.Text(g, "끌어서 추가", Fonts.Get(Fonts.UiFamily, 12 * k, FontStyle.Bold), Renderer.Yellow, new RectangleF(0, 4 * k, Width, 22 * k));
            for (int i = 0; i < kinds.Length; i++)
            {
                var r = TileRect(i);
                var t = Widget.Template(kinds[i]);
                var bg = Renderer.Hex(t.color, Color.Gray);
                using (var path = Renderer.Round(r, 10 * k))
                using (var b = new LinearGradientBrush(r, Renderer.Mix(bg, Color.White, 0.1f), bg, 90f))
                    g.FillPath(b, path);
                var sq = new RectangleF(r.X + r.Width / 2 - 11 * k, r.Y + 7 * k, 22 * k, 22 * k);
                if (Fonts.SymbolFamily != null) Renderer.Text(g, glyphs[i].ToString(), Fonts.Get(Fonts.SymbolFamily, 19 * k), Color.White, RectangleF.Inflate(sq, 6 * k, 6 * k));
                Renderer.Text(g, KindInfo.Title(kinds[i]), Fonts.Get(Fonts.UiFamily, 11.5f * k, FontStyle.Bold), Color.White, new RectangleF(r.X, r.Bottom - 22 * k, r.Width, 18 * k));
            }
            Renderer.Text(g, "앱·파일·링크·\n글자도 덱에\n바로 놓으세요", Fonts.Get(Fonts.UiFamily, 9.5f * k), Color.FromArgb(200, 255, 255, 255),
                new RectangleF(2 * k, Height - 56 * k, Width - 4 * k, 52 * k), StringAlignment.Center, StringAlignment.Center, true);
        }

        public Bitmap Snapshot()
        {
            var b = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(b)) Render(g);
            return b;
        }

        int TileAt(Point p)
        {
            for (int i = 0; i < kinds.Length; i++) if (TileRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            downIdx = TileAt(e.Location);
            downPt = e.Location;
            dragged = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button != MouseButtons.Left || downIdx < 0 || dragged) return;
            if (Math.Abs(e.X - downPt.X) + Math.Abs(e.Y - downPt.Y) < 5) return;
            dragged = true;
            var idx = downIdx;
            downIdx = -1;
            DoDragDrop(new DataObject(DeckForm.PaletteFormat, kinds[idx].ToString()), DragDropEffects.Copy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && !dragged && downIdx >= 0 && TileAt(e.Location) == downIdx) deck.AddKind(kinds[downIdx]);
            downIdx = -1;
        }
    }

    // MARK: 알림 (잠깐 떴다 사라지는 글)

    class Toast : Form
    {
        static Toast current;
        public static bool Muted;
        readonly string msg;
        readonly WinTimer life = new WinTimer { Interval = 50 };
        int ticks;

        public static void Say(string msg)
        {
            Log.Write("알림: " + msg.Replace("\n", " / "));
            if (Muted) return;
            if (!Ui.OnUiThread) { Ui.Post(() => Say(msg)); return; }
            try
            {
                if (current != null && !current.IsDisposed) current.Close();
                current = new Toast(msg);
                current.Place();
                current.Show();
            }
            catch (Exception e) { Log.Write("알림 표시 실패: " + e.Message); }
        }

        Toast(string m)
        {
            msg = m;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(28, 30, 36);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            var f = Fonts.Get(Fonts.UiFamily, 13.5f * K);
            var sz = TextRenderer.MeasureText(msg, f, new Size(int.MaxValue, int.MaxValue));
            Size = new Size(sz.Width + (int)(32 * K), sz.Height + (int)(20 * K));
            Opacity = 0.96;
            life.Tick += (s, e) =>
            {
                ticks++;
                if (ticks > 44) Opacity -= 0.12;
                if (Opacity <= 0.05) { life.Stop(); Close(); }
            };
            life.Start();
        }

        float K => DeviceDpi / 96f;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle); }

        void Place()
        {
            var d = DeckForm.Instance;
            Rectangle wa;
            if (d != null && d.Visible)
            {
                wa = Screen.FromRectangle(d.Bounds).WorkingArea;
                int x = d.Left + (d.Width - Width) / 2;
                int y = d.Bottom + (int)(8 * K);
                if (y + Height > wa.Bottom) y = d.Top - Height - (int)(8 * K);
                Location = new Point(Math.Max(wa.Left, Math.Min(wa.Right - Width, x)), Math.Max(wa.Top, y));
            }
            else
            {
                wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Renderer.Setup(g);
            g.Clear(BackColor);
            TextRenderer.DrawText(g, msg, Fonts.Get(Fonts.UiFamily, 13.5f * K), ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
