using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace EasyDeck
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "EasyDeckSelfTest"));

            using (var mutex = new Mutex(true, "EasyDeck.SingleInstance.raon-easypaster", out bool created))
            {
                if (!created)
                {
                    // 이미 실행 중이면 그쪽 덱을 보이게 하고 끝냄
                    try { EventWaitHandle.OpenExisting("EasyDeck.Show.raon-easypaster").Set(); } catch { }
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Ui.Init();
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyDeck");
                Store.Init(dir);
                Log.Write("EasyDeck 시작 " + Application.ProductVersion + " (" + Environment.OSVersion + ")");
                Application.ThreadException += (s, e) => { Log.Write("오류: " + e.Exception); Toast.Say("오류가 났습니다 – 로그를 확인하세요"); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("처리 안 된 오류: " + e.ExceptionObject);

                var show = new EventWaitHandle(false, EventResetMode.AutoReset, "EasyDeck.Show.raon-easypaster");
                new Thread(() => { while (show.WaitOne()) Ui.Post(() => DeckForm.Instance?.ShowDeck()); }) { IsBackground = true }.Start();

                Application.Run(new AppMain());
                Store.SaveNow();
                return 0;
            }
        }
    }

    /// 알림 영역 아이콘 + 덱 + 창 관리
    class AppMain : ApplicationContext
    {
        static AppMain I;
        readonly NotifyIcon tray;
        readonly DeckForm deck;
        SettingsForm settings;
        readonly Dictionary<string, InspectorForm> inspectors = new Dictionary<string, InspectorForm>();

        public AppMain()
        {
            I = this;
            deck = new DeckForm();
            Icon icon;
            try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { icon = SystemIcons.Application; }
            tray = new NotifyIcon { Icon = icon, Text = "EasyDeck", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
            tray.ContextMenuStrip.Opening += (s, e) => { deck.FillMainMenu(tray.ContextMenuStrip.Items); e.Cancel = false; };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) deck.ToggleVisible(); };
            Store.Reloaded += () => Toast.Say("설정 파일을 다시 불러왔습니다");
            deck.ShowDeck();
            if (Store.FirstRun)
                Toast.Say("EasyDeck이 시작되었습니다\n프로그램·파일·링크를 덱에 끌어다 놓으면 버튼이 생겨요\n" + KeyMap.PrettySpec(Store.S.hotkey) + " 로 덱을 숨기거나 보이게 할 수 있어요");
        }

        public static void OpenInspector(string id)
        {
            if (I == null || Store.FindWidget(id) == null) return;
            if (I.inspectors.TryGetValue(id, out var f) && !f.IsDisposed) { f.Activate(); return; }
            f = new InspectorForm(id);
            f.FormClosed += (s, e) => I.inspectors.Remove(id);
            I.inspectors[id] = f;
            f.Show();
            f.Activate();
        }

        public static void OpenSettings(int tab)
        {
            if (I == null) return;
            if (I.settings == null || I.settings.IsDisposed)
            {
                I.settings = new SettingsForm(tab);
                I.settings.Show();
            }
            else I.settings.SelectTab(tab);
            I.settings.WindowState = FormWindowState.Normal;
            I.settings.Activate();
        }

        public static void OpenProfileJson()
        {
            Store.SaveNow();
            try { Process.Start("notepad.exe", "\"" + Store.FilePath + "\"")?.Dispose(); }
            catch (Exception e) { Toast.Say("열 수 없습니다: " + e.Message); }
        }

        public static void Quit()
        {
            if (I == null) { Application.Exit(); return; }
            Store.SaveNow();
            I.tray.Visible = false;
            I.tray.Dispose();
            I.deck.AllowClose = true;
            I.deck.Close();
            I.ExitThread();
        }
    }

    // MARK: 자체 시험 (--selftest 폴더) — 빌드 서버에서 실행해 로직을 확인하고 화면을 그림으로 저장

    static class SelfTest
    {
        static int fails;
        static readonly StringBuilder report = new StringBuilder();

        static void Check(bool ok, string name)
        {
            report.AppendLine((ok ? "✓ " : "✗ ") + name);
            if (!ok) fails++;
        }

        static void Try(string name, Action a)
        {
            try { a(); }
            catch (Exception e) { Check(false, name + " — 예외: " + e.GetType().Name + ": " + e.Message); }
        }

        public static int Run(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var data = Path.Combine(outDir, "data");
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Ui.Init();
            Store.Init(data, watch: false);
            Toast.Muted = true;

            Try("키 해석", () =>
            {
                Check(KeyMap.TryParse("ctrl+shift+4", out var a) && a.Vk == 0x34 && a.Ctrl && a.Shift && !a.Alt, "ctrl+shift+4");
                Check(KeyMap.TryParse("cmd+c", out var b) && b.Vk == 0x43 && b.Ctrl, "맥 cmd → Ctrl");
                Check(KeyMap.TryParse("opt+cmd+d", out var c) && c.Alt && c.Ctrl && c.Vk == 0x44, "맥 opt+cmd+d → Ctrl+Alt+D");
                Check(KeyMap.TryParse("ctrl+-", out var d) && d.Vk == 0xBD, "ctrl+-");
                Check(KeyMap.TryParse("ctrl+=", out var e) && e.Vk == 0xBB, "ctrl+=");
                Check(KeyMap.TryParse("win+shift+s", out var f) && f.Win && f.Shift && f.Vk == 0x53, "win+shift+s");
                Check(KeyMap.TryParse("f5", out var g) && g.Vk == 0x74, "f5");
                Check(KeyMap.TryParse("alt+left", out var h) && h.Vk == 0x25 && KeyMap.IsExtended(h.Vk), "alt+left (확장 키)");
                Check(!KeyMap.TryParse("ctrl+없는키", out _), "모르는 키는 거부");
                Check(KeyMap.SpecFromKeys(Keys.Control | Keys.Shift | Keys.A) == "ctrl+shift+a", "키 녹화 → ctrl+shift+a");
                Check(KeyMap.SpecFromKeys(Keys.Control | Keys.ControlKey) == null, "수정 키만 누르면 녹화 안 함");
                Check(KeyMap.PrettySpec("ctrl+alt+d") == "Ctrl+Alt+D", "단축키 표시 " + KeyMap.PrettySpec("ctrl+alt+d"));
            });

            Try("JSON", () =>
            {
                var s = Json.Write(Profile.MakeDefault().ToJson());
                var again = Json.Write(Profile.FromJson(Json.Parse(s)).ToJson());
                Check(s == again, "기본 프로필 저장→읽기→저장이 같음");
                Check(s.Contains("\"label\" : \"복사\""), "한글이 그대로 저장됨 (맥과 같은 모양)");
                bool threw = false;
                try { Json.Parse("{\"a\": [1, 2"); } catch (FormatException) { threw = true; }
                Check(threw, "깨진 JSON은 FormatException");
                var mac = "{\"version\":1,\"currentPage\":5,\"settings\":{\"cellSize\":80,\"hotkey\":\"opt+cmd+d\",\"frameX\":120.5}," +
                          "\"pages\":[{\"name\":\"기본\",\"cols\":2,\"rows\":1,\"apps\":[\"com.google.Chrome\"],\"widgets\":[" +
                          "{\"kind\":\"knob\",\"x\":1,\"label\":\"볼륨\",\"actions\":{\"cw\":\"volume +3\"},\"value\":30,\"bind\":\"volume\"}," +
                          "{\"kind\":\"weird\",\"w\":0,\"label\":\"이스케이프 \\\"따옴표\\\" \\u00e9\"}]}]}";
                var p = Profile.FromJson(Json.Parse(mac));
                Check(p.currentPage == 0 && p.settings.cellSize == 80 && p.settings.frameX == 120.5, "맥 프로필 설정 읽기");
                Check(p.pages[0].widgets[0].kind == WidgetKind.knob && p.pages[0].widgets[0].Act("cw") == "volume +3", "맥 프로필 위젯 읽기");
                Check(p.pages[0].widgets[1].kind == WidgetKind.button && p.pages[0].widgets[1].w == 1, "모르는 값은 기본값으로");
                Check(p.pages[0].widgets[1].label == "이스케이프 \"따옴표\" é", "이스케이프 글자");
            });

            Try("배치", () =>
            {
                var page = Profile.MakeDefault().pages[0];
                var row = page.AsSingleRow();
                Check(row.rows == 1 && row.cols == page.widgets.Sum(w => w.w) && row.widgets.All(w => w.y == 0 && w.h == 1), "가로 한 줄");
                var col = page.AsSingleColumn();
                Check(col.cols == 1 && col.widgets.All(w => w.x == 0 && w.w == 1), "세로 한 줄");
                var t = page.Transposed();
                var slider = t.widgets.First(w => w.kind == WidgetKind.slider);
                Check(t.cols == page.rows && t.rows == page.cols && slider.h == 3 && slider.vertical, "가로↔세로, 슬라이더도 세로로");
                Check(page.widgets[0].y == 0 && page.cols == 5, "원본은 그대로");

                var grid = new Page("t", 3, 2);
                var n1 = grid.Place(new Widget(), 1, 1);
                Check(n1.x == 1 && n1.y == 1, "빈 칸에 그대로 넣음");
                var n2 = grid.Place(new Widget(), 1, 1);
                Check(!(n2.x == 1 && n2.y == 1) && grid.rows == 2, "찬 칸이면 다른 빈자리");
                var wide = new Page("w", 3, 1);
                wide.widgets.Add(new Widget { x = 1 });
                var n3 = wide.Place(new Widget { w = 2 }, 0, 0);
                Check(n3.x == 0 && n3.w == 1, "넓은 위젯이 안 들어가면 1칸으로");
                var full = new Page("f", 1, 1);
                full.widgets.Add(new Widget());
                var n4 = full.Place(new Widget(), 0, 0);
                Check(full.rows == 1 && full.cols == 2 && n4.x == 1, "한 줄 덱이 꽉 차면 오른쪽으로 늘어남");
                var full2 = new Page("f2", 1, 2);
                full2.widgets.Add(new Widget());
                full2.widgets.Add(new Widget { y = 1 });
                var n5 = full2.Place(new Widget(), 0, 0);
                Check(full2.rows == 3 && n5.y == 2, "여러 줄 덱이 꽉 차면 아래로 늘어남");
            });

            Try("동작 해석", () =>
            {
                Check(ActionRunner.Split("key ctrl+c").Item1 == "key" && ActionRunner.Split("key ctrl+c").Item2 == "ctrl+c", "줄 나누기");
                Check(ActionRunner.RiskOf("volume") == ActionRunner.Risk.Safe && ActionRunner.RiskOf("open") == ActionRunner.Risk.Input
                      && ActionRunner.RiskOf("powershell") == ActionRunner.Risk.RunsCode && ActionRunner.RiskOf("새동작") == ActionRunner.Risk.RunsCode, "위험도 분류");
                Check(ActionRunner.Unescape("a\\nb") == "a\nb", "\\n 줄바꿈");
            });

            Try("끌어다 놓기", () =>
            {
                var t1 = DropMaker.Items(new DataObject(DataFormats.UnicodeText, "안녕하세요 반갑습니다"));
                var w1 = DropMaker.WidgetFor(t1[0]);
                Check(t1.Count == 1 && w1.Act("tap") == "type 안녕하세요 반갑습니다", "짧은 글자 → type");
                var w2 = DropMaker.WidgetFor(new DropItem { Text = "첫 줄\n둘째 줄" });
                Check(w2.Act("tap") == "paste 첫 줄\\n둘째 줄", "여러 줄 → paste");
                var u = DropMaker.Items(new DataObject(DataFormats.UnicodeText, "https://www.youtube.com/watch?v=1"));
                var w3 = DropMaker.WidgetFor(u[0]);
                Check(u[0].Url != null && w3.label == "youtube.com" && w3.Act("tap") == "open https://www.youtube.com/watch?v=1", "링크 → open, 이름은 주소");
                var bad = DropMaker.WidgetFor(new DropItem { Url = "https://a.com/x\nshell calc" });
                Check(bad == null || bad.Act("tap").IndexOf('\n') < 0, "링크 속 줄바꿈으로 동작이 끼어들지 않음");
                Check(Icons.IsNetworkPath(@"\\server\share\a.png") && Icons.IsNetworkPath("//server/a.png") && Icons.IsNetworkPath(@"\/server\a.png") && Icons.IsNetworkPath(@"/\server\a.png") && !Icons.IsNetworkPath(@"C:\a.png") && !Icons.IsNetworkPath("ico-1.png"), "네트워크 경로 구별");
                var k = DropMaker.Items(new DataObject(DeckForm.PaletteFormat, "timer"));
                Check(k[0].Kind == "timer" && DropMaker.WidgetFor(k[0]).kind == WidgetKind.timer, "팔레트 → 타이머");
                var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
                var fd = new DataObject(DataFormats.FileDrop, new[] { notepad });
                var f = DropMaker.Items(fd);
                var w4 = DropMaker.WidgetFor(f[0]);
                Check(w4 != null && w4.Act("tap") == "open " + notepad && w4.label.Equals("notepad", StringComparison.OrdinalIgnoreCase), "프로그램 → open");
                Check(w4 != null && File.Exists(Path.Combine(Store.IconsDir, w4.icon)), "프로그램 아이콘 저장");
                using (var b = Icons.FileIcon(notepad, 128))
                {
                    Check(b != null && b.Width >= 32, "큰 아이콘 가져오기 " + (b == null ? "실패" : b.Width + "px"));
                    if (b != null) b.Save(Path.Combine(outDir, "icon-notepad.png"), ImageFormat.Png);
                }
                using (var b = Icons.FileIcon(Environment.GetFolderPath(Environment.SpecialFolder.Windows), 128))
                    if (b != null) b.Save(Path.Combine(outDir, "icon-folder.png"), ImageFormat.Png);
            });

            Try("음량", () =>
            {
                var v = Audio.Get();
                report.AppendLine("  (현재 음량: " + (v?.ToString() ?? "소리 장치 없음") + ")");
                Check(true, "음량 읽기에서 멈추지 않음");
            });

            Try("기호 글꼴", () =>
            {
                report.AppendLine("  (기호 글꼴: " + (Fonts.SymbolFamily ?? "없음") + ", 글꼴: " + Fonts.UiFamily + ")");
                Check(Icons.Glyph("sf:doc.on.doc") == '\uE8C8' && Icons.Glyph("sf:speaker.wave.2.fill") == '\uE767', "SF 이름 → 기호");
            });

            Try("덱 그리기", () =>
            {
                var deck = new DeckForm { SuppressPalette = true };
                Save(deck.Snapshot(), outDir, "deck.png");
                Check(deck.Width > 300 && deck.Height > 200, "덱 크기 " + deck.Width + "x" + deck.Height);
                deck.SetEditing(true);
                Save(deck.Snapshot(), outDir, "deck-edit.png");
                deck.SetEditing(false);
                deck.GoToPage(1, false);
                Save(deck.Snapshot(), outDir, "deck-browser.png");
                deck.GoToPage(0, false);
                deck.ChangeLayout("row");
                Check(Store.Page.rows == 1, "배치 바꾸기: 가로 한 줄");
                Check(Store.P.pages.All(p => p.rows == 1), "모든 페이지에 함께 적용");
                Save(deck.Snapshot(), outDir, "deck-row.png");
                deck.ChangeLayout("undo");
                Check(Store.Page.rows == 3 && Store.Page.cols == 5, "배치 되돌리기");
                deck.ChangeLayout("flip");
                Save(deck.Snapshot(), outDir, "deck-flip.png");
                deck.ChangeLayout("undo");
                deck.AddItems(new List<DropItem> { new DropItem { Text = "추가 시험" } }, -1, -1);
                Check(Store.Page.widgets.Any(w => w.Act("tap") == "type 추가 시험"), "끌어 놓기로 버튼 추가");
                Save(deck.Palette.Snapshot(), outDir, "palette.png");

                var id = Store.Page.widgets.First(w => w.kind == WidgetKind.button).id;
                var insp = new InspectorForm(id) { StartPosition = FormStartPosition.Manual, Location = new Point(20, 20) };
                insp.Show();
                Application.DoEvents();
                SaveForm(insp, outDir, "inspector.png");
                insp.Close();

                var st = new SettingsForm(0) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
                st.Show();
                Application.DoEvents();
                SaveForm(st, outDir, "settings.png");
                st.SelectTab(1);
                Application.DoEvents();
                SaveForm(st, outDir, "settings-pages.png");
                st.Close();

                deck.Show();
                Application.DoEvents();
                Check(deck.Visible, "덱 창 띄우기");
                deck.AllowClose = true;
                deck.Close();
            });

            Try("저장", () =>
            {
                Store.SaveNow();
                var text = File.ReadAllText(Store.FilePath, Encoding.UTF8);
                var p = Profile.FromJson(Json.Parse(text));
                Check(p.pages.Count == 2, "profile.json 저장·다시 읽기");
            });

            report.AppendLine(fails == 0 ? "모두 통과" : "실패 " + fails + "개");
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"), report.ToString(), new UTF8Encoding(false));
            return fails == 0 ? 0 : 1;
        }

        static void Save(Bitmap b, string dir, string name)
        {
            using (b) b.Save(Path.Combine(dir, name), ImageFormat.Png);
        }

        static void SaveForm(Form f, string dir, string name)
        {
            using (var b = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(b, new Rectangle(Point.Empty, f.Size));
                b.Save(Path.Combine(dir, name), ImageFormat.Png);
            }
        }
    }
}
