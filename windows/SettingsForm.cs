using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EasyDeck
{
    class SettingsForm : Form
    {
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        bool syncing;

        // 일반
        readonly List<Action> syncers = new List<Action>();
        readonly TextBox hotkeyBox = new TextBox { Width = 160, ReadOnly = true };
        Button hotkeyBtn;
        bool recording;

        // 페이지
        readonly ListBox pageList = new ListBox { Width = 170, Height = 260, IntegralHeight = false };
        readonly TextBox nameBox = new TextBox { Width = 220 };
        readonly NumericUpDown colsBox = new NumericUpDown { Minimum = 1, Maximum = 30, Width = 60 };
        readonly NumericUpDown rowsBox = new NumericUpDown { Minimum = 1, Maximum = 30, Width = 60 };
        readonly TextBox appsBox = new TextBox { Multiline = true, Width = 220, Height = 90, ScrollBars = ScrollBars.Vertical };

        public SettingsForm(int tab)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font(Fonts.UiFamily, 9f);
            Text = "EasyDeck 설정";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(640, 620);
            MinimumSize = new Size(560, 480);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            tabs.TabPages.Add(GeneralTab());
            tabs.TabPages.Add(PagesTab());
            tabs.TabPages.Add(HelpTab());
            Controls.Add(tabs);
            SelectTab(tab);

            Store.Changed += Sync;
            FormClosed += (s, e) => Store.Changed -= Sync;
            Sync();
            ResumeLayout(false);
            PerformLayout();
        }

        public void SelectTab(int i) => tabs.SelectedIndex = Math.Max(0, Math.Min(tabs.TabCount - 1, i));

        void Sync()
        {
            if (IsDisposed) return;
            syncing = true;
            try
            {
                foreach (var s in syncers) s();
                hotkeyBox.Text = KeyMap.PrettySpec(Store.S.hotkey);
                SyncPages();
            }
            finally { syncing = false; }
        }

        // MARK: 일반

        TabPage GeneralTab()
        {
            var page = new TabPage("일반") { Padding = new Padding(12), AutoScroll = true };
            var tbl = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            page.Controls.Add(tbl);

            void slider(string title, int min, int max, Func<Settings, int> get, Action<Settings, int> set, Func<int, string> fmt)
            {
                var tb = new TrackBar { Minimum = min, Maximum = max, TickStyle = TickStyle.None, Dock = DockStyle.Fill, AutoSize = false, Height = 30 };
                var lb = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
                tb.ValueChanged += (s, e) =>
                {
                    lb.Text = fmt(tb.Value);
                    if (!syncing) Store.Mutate(p => set(p.settings, tb.Value));
                };
                syncers.Add(() => { tb.Value = Math.Max(min, Math.Min(max, get(Store.S))); lb.Text = fmt(tb.Value); });
                tbl.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(3, 6, 12, 3) });
                tbl.Controls.Add(tb);
                tbl.Controls.Add(lb);
            }

            slider("버튼 크기", 40, 180, s => (int)s.cellSize, (s, v) => s.cellSize = v, v => v + " px");
            slider("버튼 간격", 0, 24, s => (int)s.gap, (s, v) => s.gap = v, v => v + " px");
            slider("글자·아이콘 크기", 6, 20, s => (int)Math.Round(s.fontScale * 10), (s, v) => s.fontScale = v / 10.0, v => "×" + (v / 10.0).ToString("0.0"));
            slider("평소 선명도", 15, 100, s => (int)Math.Round(s.idleOpacity * 100), (s, v) => s.idleOpacity = v / 100.0, v => v + "%");

            var note = new Label { Text = "마우스를 덱 밖으로 치우면 이 선명도로 흐려집니다. 덱 위에서 Ctrl+휠로 크기를 바로 바꿀 수도 있어요.", AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(560, 0), Margin = new Padding(3, 2, 3, 12) };
            tbl.Controls.Add(note);
            tbl.SetColumnSpan(note, 3);

            void check(string title, Func<bool> get, Action<bool> set)
            {
                var cb = new CheckBox { Text = title, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
                cb.CheckedChanged += (s, e) => { if (!syncing) set(cb.Checked); };
                syncers.Add(() => cb.Checked = get());
                tbl.Controls.Add(cb);
                tbl.SetColumnSpan(cb, 3);
            }

            check("항상 위에 표시", () => Store.S.alwaysOnTop, v => Store.Mutate(p => p.settings.alwaysOnTop = v));
            check("위치 고정 (위쪽 띠를 끌어도 움직이지 않음)", () => Store.S.locked, v => Store.Mutate(p => p.settings.locked = v));
            check("앞에 있는 프로그램에 맞춰 페이지 자동 전환", () => Store.S.autoSwitch, v => Store.Mutate(p => p.settings.autoSwitch = v));
            check("윈도우 시작 시 자동 실행", () => Startup.Enabled, v => { try { Startup.Enabled = v; } catch (Exception e) { MessageBox.Show(this, "설정하지 못했습니다: " + e.Message, "EasyDeck"); } });

            hotkeyBtn = new Button { Text = "바꾸기", AutoSize = true };
            hotkeyBtn.Click += (s, e) => { recording = true; hotkeyBtn.Text = "키를 누르세요…"; hotkeyBtn.Focus(); };
            var clear = new Button { Text = "지우기", AutoSize = true };
            clear.Click += (s, e) => Store.Mutate(p => p.settings.hotkey = "");
            var hkRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
            hkRow.Controls.AddRange(new Control[] { new Label { Text = "덱 보이기/숨기기 단축키", AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, hotkeyBox, hotkeyBtn, clear });
            tbl.Controls.Add(hkRow);
            tbl.SetColumnSpan(hkRow, 3);

            var files = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0), MaximumSize = new Size(580, 0) };
            Button btn(string t, Action a) { var b = new Button { Text = t, AutoSize = true }; b.Click += (s, e) => a(); files.Controls.Add(b); return b; }
            btn("내보내기…", Export);
            btn("가져오기…", Import);
            btn("기본 배치로 되돌리기", () =>
            {
                if (MessageBox.Show(this, "모든 페이지를 기본 배치로 되돌릴까요?\n지금 프로필은 데이터 폴더에 백업됩니다.", "EasyDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Store.ResetToDefault();
            });
            btn("profile.json 열기", AppMain.OpenProfileJson);
            btn("데이터 폴더 열기", () => ActionRunner.ShellOpen(Store.Dir));
            tbl.Controls.Add(files);
            tbl.SetColumnSpan(files, 3);
            return page;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (recording)
            {
                var spec = KeyMap.SpecFromKeys(keyData);
                if (spec == null) return true;
                recording = false;
                hotkeyBtn.Text = "바꾸기";
                if (!spec.Contains("+")) { MessageBox.Show(this, "Ctrl·Alt·Shift·Win 중 하나와 함께 눌러 주세요.", "EasyDeck"); return true; }
                Store.Mutate(p => p.settings.hotkey = spec);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Export()
        {
            using (var d = new SaveFileDialog { Filter = "EasyDeck 프로필|*.json", FileName = "EasyDeck-profile.json" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(d.FileName, Json.Write(Store.P.ToJson()), new UTF8Encoding(false)); Toast.Say("프로필을 내보냈습니다"); }
                catch (Exception e) { MessageBox.Show(this, "저장하지 못했습니다: " + e.Message, "EasyDeck"); }
            }
        }

        void Import()
        {
            using (var d = new OpenFileDialog { Filter = "EasyDeck 프로필|*.json|모든 파일|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Profile p;
                try { p = Profile.FromJson(Json.Parse(File.ReadAllText(d.FileName, Encoding.UTF8))); }
                catch (Exception e) { MessageBox.Show(this, "프로필을 읽을 수 없습니다: " + e.Message, "EasyDeck"); return; }
                if (!ImportCheck.Confirm(this, p)) return;
                Store.Backup("before-import");
                Store.Replace(p);
                Toast.Say("프로필을 가져왔습니다");
            }
        }

        // MARK: 페이지

        TabPage PagesTab()
        {
            var page = new TabPage("페이지") { Padding = new Padding(12), AutoScroll = true };
            var outer = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(outer);

            var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            left.Controls.Add(pageList);
            var lb = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(180, 0) };
            Button b(string t, Action a) { var x = new Button { Text = t, AutoSize = true, MinimumSize = new Size(40, 0) }; x.Click += (s, e) => a(); lb.Controls.Add(x); return x; }
            b("추가", () => { DeckForm.Instance?.AddPage(); });
            b("복제", DuplicatePage);
            b("삭제", DeletePage);
            b("▲", () => MovePage(-1));
            b("▼", () => MovePage(1));
            left.Controls.Add(lb);
            outer.Controls.Add(left);

            var right = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(14, 0, 0, 0) };
            void row(string t, Control c) { right.Controls.Add(new Label { Text = t, AutoSize = true, Margin = new Padding(3, 6, 8, 3) }); right.Controls.Add(c); }
            row("이름", nameBox);
            var size = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            size.Controls.AddRange(new Control[] { new Label { Text = "가로", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, colsBox, new Label { Text = "세로", AutoSize = true, Margin = new Padding(10, 6, 3, 3) }, rowsBox });
            row("칸 수", size);
            var apps = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var pick = new Button { Text = "실행 중인 프로그램에서 추가 ▾", AutoSize = true };
            pick.Click += (s, e) => RunningAppsMenu().Show(pick, new Point(0, pick.Height));
            apps.Controls.Add(appsBox);
            apps.Controls.Add(pick);
            apps.Controls.Add(new Label { Text = "이 프로그램이 앞에 오면 자동으로 이 페이지로 바뀝니다.\n한 줄에 하나씩 실행 파일 이름 (예: chrome, msedge, obs64)", AutoSize = true, ForeColor = SystemColors.GrayText });
            row("자동 전환", apps);
            outer.Controls.Add(right);

            var lay = new GroupBox { Text = "배치 바꾸기 (모든 페이지에 함께 적용)", AutoSize = true, Padding = new Padding(8), Margin = new Padding(3, 16, 3, 3) };
            var lf = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            foreach (var pair in new[] { ("가로 한 줄", "row"), ("세로 한 줄", "column"), ("가로 ↔ 세로", "flip"), ("되돌리기", "undo") })
            {
                var arg = pair.Item2;
                var x = new Button { Text = pair.Item1, AutoSize = true };
                x.Click += (s, e) => DeckForm.Instance?.ChangeLayout(arg);
                lf.Controls.Add(x);
            }
            lay.Controls.Add(lf);
            outer.Controls.Add(lay);
            outer.SetColumnSpan(lay, 2);

            pageList.SelectedIndexChanged += (s, e) => { if (!syncing) LoadPage(); };
            nameBox.TextChanged += (s, e) =>
            {
                int i = pageList.SelectedIndex;
                if (syncing || i < 0) return;
                var n = nameBox.Text;
                Store.MutateSilently(p => p.pages[i].name = n);
                syncing = true;
                try { pageList.Items[i] = n.Length == 0 ? "(이름 없음)" : n; } finally { syncing = false; }
                DeckForm.Instance?.Invalidate();
            };
            colsBox.ValueChanged += (s, e) => { int i = pageList.SelectedIndex; if (!syncing && i >= 0) { var v = (int)colsBox.Value; Store.Mutate(p => p.pages[i].cols = v); } };
            rowsBox.ValueChanged += (s, e) => { int i = pageList.SelectedIndex; if (!syncing && i >= 0) { var v = (int)rowsBox.Value; Store.Mutate(p => p.pages[i].rows = v); } };
            appsBox.TextChanged += (s, e) =>
            {
                int i = pageList.SelectedIndex;
                if (syncing || i < 0) return;
                var list = appsBox.Text.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                Store.MutateSilently(p => p.pages[i].apps = list);
            };
            return page;
        }

        void SyncPages()
        {
            var pages = Store.P.pages;
            int sel = pageList.SelectedIndex;
            if (sel < 0 || sel >= pages.Count) sel = Store.P.currentPage;
            pageList.BeginUpdate();
            pageList.Items.Clear();
            foreach (var p in pages) pageList.Items.Add(p.name.Length == 0 ? "(이름 없음)" : p.name);
            pageList.EndUpdate();
            pageList.SelectedIndex = Math.Min(sel, pages.Count - 1);
            LoadPage();
        }

        void LoadPage()
        {
            int i = pageList.SelectedIndex;
            if (i < 0 || i >= Store.P.pages.Count) return;
            var p = Store.P.pages[i];
            bool was = syncing;
            syncing = true;
            try
            {
                nameBox.Text = p.name;
                colsBox.Minimum = Math.Max(1, p.MinCols);
                rowsBox.Minimum = Math.Max(1, p.MinRows);
                colsBox.Maximum = Math.Max(30, p.cols);
                rowsBox.Maximum = Math.Max(30, p.rows);
                colsBox.Value = Math.Max(colsBox.Minimum, p.cols);
                rowsBox.Value = Math.Max(rowsBox.Minimum, p.rows);
                appsBox.Text = string.Join("\r\n", p.apps);
            }
            finally { syncing = was; }
            if (!syncing && i != Store.P.currentPage) DeckForm.Instance?.GoToPage(i, false);
        }

        void DuplicatePage()
        {
            int i = pageList.SelectedIndex;
            if (i < 0) return;
            Store.Mutate(prof =>
            {
                var c = prof.pages[i].Clone();
                c.id = Widget.NewId();
                c.name += " 복사본";
                foreach (var w in c.widgets) w.id = Widget.NewId();
                prof.pages.Insert(i + 1, c);
                prof.currentPage = i + 1;
            });
        }

        void DeletePage()
        {
            int i = pageList.SelectedIndex;
            if (i < 0) return;
            if (Store.P.pages.Count <= 1) { MessageBox.Show(this, "페이지가 하나뿐이라 지울 수 없습니다.", "EasyDeck"); return; }
            if (MessageBox.Show(this, "‘" + Store.P.pages[i].name + "’ 페이지를 지울까요?", "EasyDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Store.Mutate(prof => { prof.pages.RemoveAt(i); if (prof.currentPage >= prof.pages.Count) prof.currentPage = prof.pages.Count - 1; });
        }

        void MovePage(int d)
        {
            int i = pageList.SelectedIndex, j = i + d;
            if (i < 0 || j < 0 || j >= Store.P.pages.Count) return;
            Store.Mutate(prof =>
            {
                var cur = prof.pages[prof.currentPage];
                var t = prof.pages[i]; prof.pages[i] = prof.pages[j]; prof.pages[j] = t;
                prof.currentPage = prof.pages.IndexOf(cur);
            });
            pageList.SelectedIndex = j;
        }

        ContextMenuStrip RunningAppsMenu()
        {
            var m = new ContextMenuStrip();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.MainWindowHandle != IntPtr.Zero && !p.ProcessName.Equals("EasyDeck", StringComparison.OrdinalIgnoreCase)) names.Add(p.ProcessName.ToLowerInvariant()); }
                catch { }
                finally { p.Dispose(); }
            }
            foreach (var n in names)
            {
                var name = n;
                m.Items.Add(name, null, (s, e) =>
                {
                    var t = appsBox.Text.TrimEnd();
                    appsBox.Text = t.Length == 0 ? name : t + "\r\n" + name;
                });
            }
            if (names.Count == 0) m.Items.Add("(창이 열린 프로그램이 없습니다)").Enabled = false;
            return m;
        }

        // MARK: 도움말

        TabPage HelpTab()
        {
            var page = new TabPage("도움말") { Padding = new Padding(8) };
            page.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window,
                Text = HelpText.Text.Replace("\n", "\r\n"), Font = new Font(Fonts.UiFamily, 9.5f),
            });
            return page;
        }
    }

    static class Startup
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return k?.GetValue("EasyDeck") is string s && s.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value) k.SetValue("EasyDeck", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("EasyDeck", false);
                }
            }
        }
    }

    static class HelpText
    {
        public const string Text =
@"EasyDeck — 화면 위 소프트웨어 스트림덱 (윈도우용)

■ 기본 사용
· 덱의 버튼을 누르면 지금 쓰고 있는 프로그램에 단축키·글자가 들어갑니다. 덱을 눌러도 그 프로그램의 포커스는 그대로입니다.
· 위쪽 띠를 끌어 덱을 옮깁니다. 띠 위에서 휠을 굴리면 페이지가 바뀝니다.
· 덱 위에서 Ctrl+휠: 덱 크기 조절
· 덱 보이기/숨기기: Ctrl+Alt+D (설정에서 바꿀 수 있음), 또는 작업 표시줄 알림 영역의 EasyDeck 아이콘
· 마우스를 덱 밖으로 치우면 덱이 흐려집니다.

■ 위젯
· 버튼: 클릭 / 더블클릭 / 길게 누르기(0.5초)에 각각 동작
· 토글: 누를 때마다 켜짐·꺼짐 동작을 번갈아 실행 (오른쪽 위 점이 초록이면 켜짐)
· 노브: 휠이나 위아래 끌기로 돌림. 시스템 음량에 연결 가능
· 슬라이더: 끌어서 값 조절. 시스템 음량에 연결 가능
· 타이머: 클릭으로 시작/정지, 휠로 1분씩 조절, 길게 누르면 처음으로

■ 끌어다 놓아 버튼 만들기
· 탐색기의 프로그램·바로 가기·파일·폴더, 브라우저의 링크, 선택한 글자를 덱에 끌어다 놓으면 아이콘과 함께 버튼이 생깁니다.
· 버튼 위에 놓으면 동작 바꾸기·덧붙이기, 그림 파일은 아이콘으로 쓸 수 있습니다.
· 빈 칸이 없으면 덱이 저절로 늘어납니다.

■ 편집 모드 (위쪽 띠의 연필, 또는 오른쪽 클릭 메뉴)
· 옆에 뜨는 팔레트에서 위젯을 끌어다 원하는 칸에 놓습니다 (클릭하면 빈 자리에 추가).
· 빈 칸의 + 를 눌러 버튼을 추가합니다.
· 위젯을 끌어 옮깁니다. 다른 위젯 위에 놓으면 자리를 바꿉니다 (파랑).
· 오른쪽 아래 모서리를 끌어 크기를 바꿉니다.
· 위젯을 클릭하면 편집 창이 열립니다.

■ 배치 바꾸기 (≡ 메뉴 › 배치 바꾸기)
· 가로 한 줄 / 세로 한 줄 / 가로 ↔ 세로 — 모든 페이지에 함께 적용, 바로 되돌리기 가능

■ 동작 문법 (한 줄에 하나, 위에서부터 순서대로)
key ctrl+shift+4          단축키 (여러 개: key ctrl+k ctrl+s)
type 안녕하세요\n반가워요   글자 입력 (\n 줄바꿈)
paste 긴 문장             클립보드로 붙여넣기 (원래 클립보드 복원)
open https://…            웹페이지
open C:\예배\찬양         폴더·파일
open notepad              프로그램 (이름 또는 경로)
shell start calc          cmd 명령 (창 없이)
powershell Get-Date       파워셸 명령 (창 없이)
volume up|down|mute|50|+5|-5
media play|next|prev|stop
brightness up|down|50     (노트북 내장 화면)
darkmode toggle|on|off
delay 0.5                 기다리기(초)
page 이름|next|prev|2      페이지 이동
layout row|column|flip|undo
notify 메시지   sound Glass   deck hide|show|toggle
# 주석

키 이름: ctrl alt shift win, a~z 0~9 f1~f24, enter tab space backspace del esc left right up down home end pageup pagedown printscreen, - = [ ] ; ' , . / `
맥 프로필의 cmd 는 Ctrl, opt 는 Alt 로 읽습니다. 맥에서 만든 profile.json 을 그대로 가져와 쓸 수 있습니다.

■ 알아 둘 점
· 관리자 권한으로 실행한 프로그램에는 키 입력이 들어가지 않습니다 (윈도우 보안 정책).
· 게임 일부와 보안 입력 칸에서는 키 입력이 막힐 수 있습니다.
· 밝기 조절은 노트북 내장 화면만 됩니다.
· 처음 실행할 때 'Windows의 PC 보호' 창이 뜨면 [추가 정보] → [실행]을 누르세요.

■ 파일 위치: %APPDATA%\EasyDeck
· profile.json — 페이지·위젯·동작·설정 (메모장으로 고치면 바로 반영)
· icons\ — 추가한 아이콘 그림
· EasyDeck.log — 실행 기록·오류

문의: 카카오 오픈채팅 https://open.kakao.com/o/ghZQNlXi
소스: https://github.com/raon-easypaster/easydeck";
    }
}
