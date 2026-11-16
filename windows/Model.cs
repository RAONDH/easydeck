using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using WinTimer = System.Windows.Forms.Timer;

namespace EasyDeck
{
    // MARK: 데이터 모델 (profile.json — 맥 버전과 같은 형식)

    enum WidgetKind { button, toggle, knob, slider, timer }

    static class KindInfo
    {
        public static string Title(WidgetKind k)
        {
            switch (k)
            {
                case WidgetKind.toggle: return "토글";
                case WidgetKind.knob: return "노브";
                case WidgetKind.slider: return "슬라이더";
                case WidgetKind.timer: return "타이머";
                default: return "버튼";
            }
        }

        public static WidgetKind Parse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "toggle": return WidgetKind.toggle;
                case "knob": return WidgetKind.knob;
                case "slider": return WidgetKind.slider;
                case "timer": return WidgetKind.timer;
                default: return WidgetKind.button;
            }
        }
    }

    class Widget
    {
        public string id = NewId();
        public WidgetKind kind = WidgetKind.button;
        public int x, y, w = 1, h = 1;
        public string label = "";
        public string icon = "";
        public string color = "#2C313C";
        public string textColor = "#FFFFFF";
        /// 동작 스크립트: tap/double/long, on/off, cw/ccw/change, end
        public Dictionary<string, string> actions = new Dictionary<string, string>();
        public string hotkey = "";
        public bool isOn;
        public double value = 50;
        public string bind = "";
        public bool infinite = true;
        public bool vertical;
        public double duration = 300;

        public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

        public string Act(string key) => actions.TryGetValue(key, out var v) ? v ?? "" : "";

        public Widget Clone()
        {
            var c = (Widget)MemberwiseClone();
            c.actions = new Dictionary<string, string>(actions);
            return c;
        }

        public static Widget FromJson(Dictionary<string, object> d)
        {
            var w = new Widget
            {
                id = Json.S(d, "id", NewId()),
                kind = KindInfo.Parse(Json.S(d, "kind", "button")),
                x = Math.Max(0, Json.I(d, "x", 0)),
                y = Math.Max(0, Json.I(d, "y", 0)),
                w = Math.Max(1, Json.I(d, "w", 1)),
                h = Math.Max(1, Json.I(d, "h", 1)),
                label = Json.S(d, "label", ""),
                icon = Json.S(d, "icon", ""),
                color = Json.S(d, "color", "#2C313C"),
                textColor = Json.S(d, "textColor", "#FFFFFF"),
                hotkey = Json.S(d, "hotkey", ""),
                isOn = Json.B(d, "isOn", false),
                value = Json.N(d, "value", 50),
                bind = Json.S(d, "bind", ""),
                infinite = Json.B(d, "infinite", true),
                vertical = Json.B(d, "vertical", false),
                duration = Json.N(d, "duration", 300),
            };
            if (d.TryGetValue("actions", out var a) && a is Dictionary<string, object> ad)
                foreach (var kv in ad)
                    if (kv.Value is string s) w.actions[kv.Key] = s;
            return w;
        }

        public Dictionary<string, object> ToJson()
        {
            var a = new Dictionary<string, object>();
            foreach (var kv in actions) a[kv.Key] = kv.Value;
            return new Dictionary<string, object>
            {
                ["id"] = id, ["kind"] = kind.ToString(), ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h,
                ["label"] = label, ["icon"] = icon, ["color"] = color, ["textColor"] = textColor,
                ["actions"] = a, ["hotkey"] = hotkey, ["isOn"] = isOn, ["value"] = value, ["bind"] = bind,
                ["infinite"] = infinite, ["vertical"] = vertical, ["duration"] = duration,
            };
        }

        /// 팔레트·빈 칸 + 로 새로 만드는 위젯
        public static Widget Template(WidgetKind k)
        {
            switch (k)
            {
                case WidgetKind.toggle:
                    return new Widget { kind = k, label = "토글", icon = "sf:switch.2", color = "#2B8A3E",
                        actions = { ["on"] = "notify 켰습니다", ["off"] = "notify 껐습니다" } };
                case WidgetKind.knob:
                    return new Widget { kind = k, label = "볼륨", icon = "sf:speaker.wave.2.fill", color = "#E8590C",
                        bind = "volume", actions = { ["tap"] = "volume mute" } };
                case WidgetKind.slider:
                    return new Widget { kind = k, w = 2, label = "볼륨", color = "#E8590C", bind = "volume" };
                case WidgetKind.timer:
                    return new Widget { kind = k, w = 2, label = "타이머", color = "#0B7285", duration = 300,
                        actions = { ["end"] = "notify 타이머 끝!\nsound Glass" } };
                default:
                    return new Widget { kind = WidgetKind.button, label = "새 버튼", icon = "sf:square", color = "#3B5BDB" };
            }
        }
    }

    class Page
    {
        public string id = Widget.NewId();
        public string name = "페이지";
        public int cols = 5, rows = 3;
        public List<Widget> widgets = new List<Widget>();
        /// 이 프로그램이 앞에 오면 자동으로 이 페이지로 (윈도우: 실행 파일 이름, 맥: 번들 ID)
        public List<string> apps = new List<string>();

        public Page() { }
        public Page(string name, int cols = 5, int rows = 3) { this.name = name; this.cols = cols; this.rows = rows; }

        public Page Clone()
        {
            var p = (Page)MemberwiseClone();
            p.widgets = widgets.Select(w => w.Clone()).ToList();
            p.apps = new List<string>(apps);
            return p;
        }

        public static Page FromJson(Dictionary<string, object> d)
        {
            var p = new Page
            {
                id = Json.S(d, "id", Widget.NewId()),
                name = Json.S(d, "name", "페이지"),
                cols = Math.Max(1, Json.I(d, "cols", 5)),
                rows = Math.Max(1, Json.I(d, "rows", 3)),
            };
            foreach (var o in Json.L(d, "widgets"))
                if (o is Dictionary<string, object> wd) p.widgets.Add(Widget.FromJson(wd));
            foreach (var o in Json.L(d, "apps"))
                if (o is string s) p.apps.Add(s);
            return p;
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = id, ["name"] = name, ["cols"] = cols, ["rows"] = rows,
            ["widgets"] = widgets.Select(w => (object)w.ToJson()).ToList(),
            ["apps"] = apps.Select(a => (object)a).ToList(),
        };

        // MARK: 배치 변환

        List<Widget> Ordered(Page p) => p.widgets.OrderBy(w => w.y).ThenBy(w => w.x).ToList();

        /// 모든 위젯을 가로 한 줄로 (높이 1칸)
        public Page AsSingleRow()
        {
            var p = Clone(); int cursor = 0;
            p.widgets = Ordered(p);
            foreach (var w in p.widgets) { w.x = cursor; w.y = 0; w.h = 1; cursor += w.w; if (w.kind == WidgetKind.slider) w.vertical = false; }
            p.cols = Math.Max(1, cursor); p.rows = 1;
            return p;
        }

        /// 모든 위젯을 세로 한 줄로 (너비 1칸)
        public Page AsSingleColumn()
        {
            var p = Clone(); int cursor = 0;
            p.widgets = Ordered(p);
            foreach (var w in p.widgets) { w.y = cursor; w.x = 0; w.w = 1; cursor += w.h; }
            p.rows = Math.Max(1, cursor); p.cols = 1;
            return p;
        }

        /// 가로 ↔ 세로 (행과 열을 맞바꿈, 슬라이더 방향도 같이)
        public Page Transposed()
        {
            var p = Clone();
            p.cols = rows; p.rows = cols;
            foreach (var w in p.widgets)
            {
                int ox = w.x, oy = w.y, ow = w.w, oh = w.h;
                w.x = oy; w.y = ox; w.w = oh; w.h = ow;
                if (w.kind == WidgetKind.slider) w.vertical = w.h > w.w;
            }
            return p;
        }

        // MARK: 자리 찾기

        public bool Fits(int x, int y, int w, int h, params string[] ignore)
        {
            if (x < 0 || y < 0 || w < 1 || h < 1 || x + w > cols || y + h > rows) return false;
            foreach (var o in widgets)
            {
                if (ignore != null && ignore.Contains(o.id)) continue;
                if (x < o.x + o.w && o.x < x + w && y < o.y + o.h && o.y < y + h) return false;
            }
            return true;
        }

        /// 놓은 칸 → 1칸으로 줄여서 → 첫 빈자리 → 덱 늘리기(한 줄이면 오른쪽, 아니면 아래) 순서로 자리를 찾아 넣음
        public Widget Place(Widget w, int cx, int cy)
        {
            bool valid = cx >= 0 && cy >= 0 && cx < cols && cy < rows;
            if (valid && Fits(cx, cy, w.w, w.h)) return Put(w, cx, cy, w.w, w.h);
            if (valid && (w.w > 1 || w.h > 1) && Fits(cx, cy, 1, 1)) return Put(w, cx, cy, 1, 1);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    if (Fits(x, y, w.w, w.h)) return Put(w, x, y, w.w, w.h);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    if (Fits(x, y, 1, 1)) return Put(w, x, y, 1, 1);
            if (rows == 1)
            {
                int x0 = cols; cols += w.w;
                return Put(w, x0, 0, w.w, 1);
            }
            int y0 = rows; int ww = Math.Min(w.w, cols); rows += w.h;
            return Put(w, 0, y0, ww, w.h);
        }

        Widget Put(Widget w, int x, int y, int ww, int hh)
        {
            w.x = x; w.y = y; w.w = ww; w.h = hh;
            widgets.Add(w);
            return w;
        }

        /// 위젯이 차지하는 가장 오른쪽·아래 칸 (칸 수를 이보다 줄일 수 없음)
        public int MinCols => widgets.Count == 0 ? 1 : widgets.Max(w => w.x + w.w);
        public int MinRows => widgets.Count == 0 ? 1 : widgets.Max(w => w.y + w.h);
    }

    class Settings
    {
        public double cellSize = 76, gap = 8, fontScale = 1.0, idleOpacity = 0.55;
        public bool alwaysOnTop = true, locked = false, autoSwitch = true;
        public string hotkey = "ctrl+alt+d";
        public double? frameX, frameY;

        public static Settings FromJson(Dictionary<string, object> d) => new Settings
        {
            cellSize = Clamp(Json.N(d, "cellSize", 76), 30, 240),
            gap = Clamp(Json.N(d, "gap", 8), 0, 40),
            fontScale = Clamp(Json.N(d, "fontScale", 1), 0.5, 2.5),
            idleOpacity = Clamp(Json.N(d, "idleOpacity", 0.55), 0.1, 1),
            alwaysOnTop = Json.B(d, "alwaysOnTop", true),
            locked = Json.B(d, "locked", false),
            hotkey = Json.S(d, "hotkey", "ctrl+alt+d"),
            autoSwitch = Json.B(d, "autoSwitch", true),
            frameX = Json.NN(d, "frameX"),
            frameY = Json.NN(d, "frameY"),
        };

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["cellSize"] = cellSize, ["gap"] = gap, ["fontScale"] = fontScale, ["idleOpacity"] = idleOpacity,
                ["alwaysOnTop"] = alwaysOnTop, ["locked"] = locked, ["hotkey"] = hotkey, ["autoSwitch"] = autoSwitch,
            };
            if (frameX.HasValue) d["frameX"] = frameX.Value;
            if (frameY.HasValue) d["frameY"] = frameY.Value;
            return d;
        }

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }

    class Profile
    {
        public int version = 1;
        public Settings settings = new Settings();
        public List<Page> pages = new List<Page>();
        public int currentPage;

        public static Profile FromJson(object o)
        {
            if (!(o is Dictionary<string, object> d)) throw new FormatException("프로필 형식이 아닙니다");
            var p = new Profile
            {
                version = Json.I(d, "version", 1),
                settings = Settings.FromJson(Json.Obj(d.TryGetValue("settings", out var s) ? s : null)),
                currentPage = Json.I(d, "currentPage", 0),
            };
            foreach (var po in Json.L(d, "pages"))
                if (po is Dictionary<string, object> pd) p.pages.Add(Page.FromJson(pd));
            p.Normalize();
            return p;
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["version"] = version, ["settings"] = settings.ToJson(),
            ["pages"] = pages.Select(p => (object)p.ToJson()).ToList(), ["currentPage"] = currentPage,
        };

        public void Normalize()
        {
            if (pages.Count == 0) pages.Add(new Page("기본"));
            currentPage = Math.Min(Math.Max(0, currentPage), pages.Count - 1);
        }

        public Profile Clone() => FromJson(Json.Parse(Json.Write(ToJson())));

        static Widget W(WidgetKind k, int x, int y, string label, string icon, string color, params string[] acts)
        {
            var w = new Widget { kind = k, x = x, y = y, label = label, icon = icon, color = color };
            for (int i = 0; i + 1 < acts.Length; i += 2) w.actions[acts[i]] = acts[i + 1];
            return w;
        }

        /// 윈도우용 기본 배치 (맥 기본 배치와 같은 모양, 단축키만 윈도우에 맞춤)
        public static Profile MakeDefault()
        {
            var main = new Page("기본", 5, 3);
            main.widgets.AddRange(new[]
            {
                W(WidgetKind.button, 0, 0, "복사", "sf:doc.on.doc", "#3B5BDB", "tap", "key ctrl+c", "long", "key ctrl+x"),
                W(WidgetKind.button, 1, 0, "붙여넣기", "sf:doc.on.clipboard", "#3B5BDB", "tap", "key ctrl+v", "double", "key ctrl+shift+v"),
                W(WidgetKind.button, 2, 0, "캡처", "sf:camera.viewfinder", "#5F3DC4", "tap", "key win+shift+s", "long", "key printscreen"),
                W(WidgetKind.button, 3, 0, "인사말", "👋", "#2B8A3E", "tap", "type 안녕하세요! 좋은 하루 보내세요 :)"),
                W(WidgetKind.button, 4, 0, "구글", "sf:globe", "#1971C2", "tap", "open https://www.google.com"),
                W(WidgetKind.button, 0, 1, "재생", "sf:playpause.fill", "#C2255C", "tap", "media play", "double", "media next", "long", "media prev"),
                W(WidgetKind.knob, 1, 1, "볼륨", "sf:speaker.wave.2.fill", "#E8590C", "tap", "volume mute", "cw", "volume +3", "ccw", "volume -3"),
                W(WidgetKind.toggle, 2, 1, "다크모드", "sf:moon.fill", "#495057", "on", "darkmode on", "off", "darkmode off"),
                W(WidgetKind.timer, 3, 1, "집중 타이머", "", "#0B7285", "end", "notify 타이머 종료!\nsound Glass"),
                W(WidgetKind.slider, 0, 2, "볼륨", "", "#E8590C"),
                W(WidgetKind.button, 3, 2, "밝게", "sf:sun.max.fill", "#F08C00", "tap", "brightness up", "long", "brightness down"),
                W(WidgetKind.button, 4, 2, "다음 페이지", "sf:arrow.right.circle", "#343A40", "tap", "page next"),
            });
            main.widgets[8].w = 2;
            main.widgets[9].w = 3;
            main.widgets[9].bind = "volume";

            var browser = new Page("브라우저", 5, 3);
            browser.widgets.AddRange(new[]
            {
                W(WidgetKind.button, 0, 0, "새 탭", "sf:plus.square", "#1971C2", "tap", "key ctrl+t"),
                W(WidgetKind.button, 1, 0, "탭 닫기", "sf:xmark.square", "#C92A2A", "tap", "key ctrl+w", "long", "key ctrl+shift+t"),
                W(WidgetKind.button, 2, 0, "새로고침", "sf:arrow.clockwise", "#2B8A3E", "tap", "key f5", "long", "key ctrl+f5"),
                W(WidgetKind.knob, 3, 0, "탭 이동", "sf:arrow.left.arrow.right", "#5F3DC4", "cw", "key ctrl+tab", "ccw", "key ctrl+shift+tab"),
                W(WidgetKind.button, 4, 0, "주소창", "sf:magnifyingglass", "#343A40", "tap", "key ctrl+l"),
                W(WidgetKind.button, 0, 1, "뒤로", "sf:chevron.left", "#343A40", "tap", "key alt+left"),
                W(WidgetKind.button, 1, 1, "앞으로", "sf:chevron.right", "#343A40", "tap", "key alt+right"),
                W(WidgetKind.knob, 2, 1, "확대/축소", "sf:plus.magnifyingglass", "#E8590C", "cw", "key ctrl+=", "ccw", "key ctrl+-", "tap", "key ctrl+0"),
                W(WidgetKind.button, 4, 2, "이전 페이지", "sf:arrow.left.circle", "#343A40", "tap", "page prev"),
            });
            browser.apps.AddRange(new[] { "chrome", "msedge", "firefox", "whale", "brave", "opera" });

            var p = new Profile();
            p.pages.Add(main);
            p.pages.Add(browser);
            return p;
        }
    }

    // MARK: 저장소 (%APPDATA%\EasyDeck\profile.json)

    static class Store
    {
        public static string Dir, FilePath, IconsDir, LogPath;
        public static Profile P = Profile.MakeDefault();
        public static bool FirstRun;
        /// 구조가 바뀌어 덱을 다시 그려야 할 때
        public static event Action Changed;
        public static event Action Reloaded;

        static string lastWritten;
        static WinTimer saveTimer, reloadTimer;
        static FileSystemWatcher watcher;

        public static Page Page => P.pages[P.currentPage];
        public static Settings S => P.settings;

        public static void Init(string dir, bool watch = true)
        {
            Dir = dir;
            FilePath = Path.Combine(dir, "profile.json");
            IconsDir = Path.Combine(dir, "icons");
            LogPath = Path.Combine(dir, "EasyDeck.log");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(IconsDir);
            saveTimer = new WinTimer { Interval = 400 };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveNow(); };
            reloadTimer = new WinTimer { Interval = 250 };
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); ReloadFromDisk(); };

            if (File.Exists(FilePath))
            {
                try
                {
                    var text = File.ReadAllText(FilePath, Encoding.UTF8);
                    P = Profile.FromJson(Json.Parse(text));
                    lastWritten = text;
                }
                catch (Exception e)
                {
                    var bad = Path.Combine(dir, "profile.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    try { File.Copy(FilePath, bad, true); } catch { }
                    Log.Write("profile.json 읽기 실패, 기본값 사용 (원본은 " + Path.GetFileName(bad) + "로 보관): " + e.Message);
                    P = Profile.MakeDefault();
                    SaveNow();
                }
            }
            else
            {
                FirstRun = true;
                P = Profile.MakeDefault();
                SaveNow();
            }
            if (watch) StartWatching();
        }

        public static void Mutate(Action<Profile> f)
        {
            f(P);
            P.Normalize();
            ScheduleSave();
            Changed?.Invoke();
        }

        /// 값만 바뀜 (다시 배치하지 않음)
        public static void MutateSilently(Action<Profile> f)
        {
            f(P);
            ScheduleSave();
        }

        public static Widget FindWidget(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in P.pages)
                foreach (var w in p.widgets)
                    if (w.id == id) return w;
            return null;
        }

        public static void UpdateWidget(string id, Action<Widget> f, bool silent = false)
        {
            var w = FindWidget(id);
            if (w == null) return;
            if (silent) MutateSilently(_ => f(w)); else Mutate(_ => f(w));
        }

        public static void Replace(Profile p)
        {
            P = p;
            P.Normalize();
            SaveNow();
            Changed?.Invoke();
        }

        public static void ResetToDefault()
        {
            Backup("backup");
            Replace(Profile.MakeDefault());
        }

        public static void Backup(string suffix)
        {
            try { File.Copy(FilePath, Path.Combine(Dir, "profile." + suffix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true); }
            catch (Exception e) { Log.Write("백업 실패: " + e.Message); }
        }

        static void ScheduleSave()
        {
            if (saveTimer == null) return;
            saveTimer.Stop();
            saveTimer.Start();
        }

        public static void SaveNow()
        {
            saveTimer?.Stop();
            if (FilePath == null) return;
            var text = Json.Write(P.ToJson());
            if (text == lastWritten) return;
            lastWritten = text;
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    try { File.Replace(tmp, FilePath, null); }
                    catch { File.Copy(tmp, FilePath, true); File.Delete(tmp); }
                }
                else File.Move(tmp, FilePath);
            }
            catch (Exception e) { Log.Write("저장 실패: " + e.Message); }
        }

        /// 메모장 등으로 profile.json을 고치면 자동으로 다시 읽음
        static void StartWatching()
        {
            try
            {
                watcher = new FileSystemWatcher(Dir, "profile.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler h = (s, e) => Ui.Post(() => { reloadTimer.Stop(); reloadTimer.Start(); });
                watcher.Changed += h;
                watcher.Created += h;
                watcher.Renamed += (s, e) => Ui.Post(() => { reloadTimer.Stop(); reloadTimer.Start(); });
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) { Log.Write("파일 감시 실패: " + e.Message); }
        }

        static void ReloadFromDisk()
        {
            string text;
            try { text = File.ReadAllText(FilePath, Encoding.UTF8); }
            catch { return; }
            if (text == lastWritten) return;
            try
            {
                var p = Profile.FromJson(Json.Parse(text));
                lastWritten = text;
                P = p;
                Changed?.Invoke();
                Reloaded?.Invoke();
                Log.Write("profile.json 변경 감지 → 다시 불러옴");
            }
            catch (Exception e)
            {
                Log.Write("profile.json 형식 오류: " + e.Message);
                Toast.Say("profile.json 형식 오류 – 로그를 확인하세요");
            }
        }
    }

    // MARK: 화면(UI) 스레드로 넘기기

    static class Ui
    {
        public static SynchronizationContext Ctx;
        public static int ThreadId;

        public static void Init()
        {
            if (!(SynchronizationContext.Current is System.Windows.Forms.WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            Ctx = SynchronizationContext.Current;
            ThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static bool OnUiThread => Thread.CurrentThread.ManagedThreadId == ThreadId;

        public static void Post(Action a)
        {
            if (Ctx == null) { Safe(a); return; }
            Ctx.Post(_ => Safe(a), null);
        }

        public static void Send(Action a)
        {
            if (Ctx == null || OnUiThread) { a(); return; }
            Exception err = null;
            Ctx.Send(_ => { try { a(); } catch (Exception e) { err = e; } }, null);
            if (err != null) throw err;
        }

        static void Safe(Action a)
        {
            try { a(); } catch (Exception e) { Log.Write("화면 처리 오류: " + e); }
        }
    }

    static class Log
    {
        static readonly object gate = new object();

        public static void Write(string s)
        {
            try
            {
                if (Store.LogPath == null) return;
                lock (gate)
                {
                    var fi = new FileInfo(Store.LogPath);
                    if (fi.Exists && fi.Length > 2_000_000) File.Delete(Store.LogPath);
                    File.AppendAllText(Store.LogPath, "[" + DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "] " + s + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
