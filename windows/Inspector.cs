using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EasyDeck
{
    /// 위젯 편집 창
    class InspectorForm : Form
    {
        public string WidgetId { get; }
        readonly Widget wk;

        readonly Panel preview = new DoubleBufferedPanel { Size = new Size(110, 110), Margin = new Padding(3, 3, 3, 8) };
        readonly ComboBox kindBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        readonly TextBox labelBox = new TextBox { Width = 300 };
        readonly TextBox iconBox = new TextBox { Width = 200 };
        readonly TextBox hotkeyBox = new TextBox { Width = 180, ReadOnly = true };
        readonly Button colorBtn = new Button { Width = 70, Height = 26, FlatStyle = FlatStyle.Flat };
        readonly Button textColorBtn = new Button { Width = 70, Height = 26, FlatStyle = FlatStyle.Flat };
        readonly TableLayoutPanel actionsTable = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly FlowLayoutPanel optionsPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        readonly Label optionsLabel = new Label { Text = "옵션", AutoSize = true, Margin = new Padding(3, 6, 10, 3) };
        readonly Dictionary<string, TextBox> actionBoxes = new Dictionary<string, TextBox>();
        readonly ToolTip tips = new ToolTip();

        object recordTarget;
        Button recordButton;
        string recordButtonText;

        static readonly string[] Swatches = { "#3B5BDB", "#5F3DC4", "#C2255C", "#E8590C", "#F08C00", "#2B8A3E", "#0B7285", "#343A40" };

        public InspectorForm(string id)
        {
            WidgetId = id;
            wk = (Store.FindWidget(id) ?? new Widget()).Clone();

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font(Fonts.UiFamily, 9f);
            Text = "위젯 편집 — " + (string.IsNullOrEmpty(wk.label) ? KindInfo.Title(wk.kind) : wk.label);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(600, 760);
            MinimumSize = new Size(480, 400);
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
            var tbl = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(tbl);

            void row(string title, Control c)
            {
                var l = new Label { Text = title, AutoSize = true, Margin = new Padding(3, 6, 10, 3) };
                tbl.Controls.Add(l);
                tbl.Controls.Add(c);
            }

            preview.Paint += (s, e) => PaintPreview(e.Graphics);
            row("미리보기", preview);

            foreach (WidgetKind k in Enum.GetValues(typeof(WidgetKind))) kindBox.Items.Add(KindInfo.Title(k));
            kindBox.SelectedIndex = (int)wk.kind;
            kindBox.SelectedIndexChanged += (s, e) =>
            {
                wk.kind = (WidgetKind)kindBox.SelectedIndex;
                BuildActions();
                BuildOptions();
                preview.Invalidate();
            };
            row("종류", kindBox);

            labelBox.Text = wk.label;
            labelBox.TextChanged += (s, e) => { wk.label = labelBox.Text; preview.Invalidate(); };
            row("이름", labelBox);

            iconBox.Text = wk.icon;
            iconBox.TextChanged += (s, e) => { wk.icon = iconBox.Text.Trim(); preview.Invalidate(); };
            var symBtn = new Button { Text = "기호 ▾", AutoSize = true };
            symBtn.Click += (s, e) => SymbolMenu().Show(symBtn, new Point(0, symBtn.Height));
            var fileBtn = new Button { Text = "그림 파일…", AutoSize = true };
            fileBtn.Click += (s, e) => PickIconFile();
            var clearIcon = new Button { Text = "지우기", AutoSize = true };
            clearIcon.Click += (s, e) => iconBox.Text = "";
            row("아이콘", Flow(iconBox, symBtn, fileBtn, clearIcon));
            tips.SetToolTip(iconBox, "기호(sf:globe), 그림 파일 경로, 또는 글자·이모지(👋)");

            colorBtn.Click += (s, e) => PickColor(true);
            var sw = new List<Control> { colorBtn };
            foreach (var hex in Swatches)
            {
                var b = new Button { Width = 24, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = Renderer.Hex(hex, Color.Gray), Margin = new Padding(2, 3, 2, 3) };
                b.FlatAppearance.BorderSize = 0;
                var h = hex;
                b.Click += (s, e) => { wk.color = h; RefreshColors(); };
                sw.Add(b);
            }
            row("배경색", Flow(sw.ToArray()));
            textColorBtn.Click += (s, e) => PickColor(false);
            row("글자색", Flow(textColorBtn));
            RefreshColors();

            var actTitle = new Label { Text = "동작", AutoSize = true, Margin = new Padding(3, 10, 10, 3), Font = new Font(Font, FontStyle.Bold) };
            tbl.Controls.Add(actTitle);
            tbl.SetColumnSpan(actTitle, 2);
            tbl.Controls.Add(actionsTable);
            tbl.SetColumnSpan(actionsTable, 2);

            tbl.Controls.Add(optionsLabel);
            tbl.Controls.Add(optionsPanel);

            hotkeyBox.Text = KeyMap.PrettySpec(wk.hotkey);
            var hkRec = new Button { Text = "녹화", AutoSize = true };
            hkRec.Click += (s, e) => StartRecord(hotkeyBox, hkRec);
            var hkClear = new Button { Text = "지우기", AutoSize = true };
            hkClear.Click += (s, e) => { wk.hotkey = ""; hotkeyBox.Text = ""; };
            row("전역 단축키", Flow(hotkeyBox, hkRec, hkClear));

            var help = new Label
            {
                AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 10, 3, 3),
                Text = "동작은 한 줄에 하나씩, 위에서부터 차례로 실행됩니다. 예: key ctrl+c · type 안녕하세요 · open https://… · volume +5 · page next\n{value} 는 노브·슬라이더 값(0~100)으로 바뀝니다. 전역 단축키는 덱을 누르지 않아도 키보드로 이 위젯을 실행합니다.",
            };
            tbl.Controls.Add(help);
            tbl.SetColumnSpan(help, 2);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 8) };
            var save = new Button { Text = "저장", AutoSize = true };
            save.Click += (s, e) => Save();
            var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
            cancel.Click += (s, e) => Close();
            var del = new Button { Text = "삭제", AutoSize = true, ForeColor = Color.Firebrick };
            del.Click += (s, e) => Delete();
            bar.Controls.Add(save);
            bar.Controls.Add(cancel);
            bar.Controls.Add(del);

            Controls.Add(scroll);
            Controls.Add(bar);
            CancelButton = cancel;

            BuildActions();
            BuildOptions();
            ResumeLayout(false);
            PerformLayout();
        }

        static FlowLayoutPanel Flow(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), MaximumSize = new Size(560, 0) };
            f.Controls.AddRange(cs);
            return f;
        }

        static IEnumerable<(string, string)> ActionKeys(WidgetKind k)
        {
            switch (k)
            {
                case WidgetKind.toggle: return new[] { ("on", "켤 때"), ("off", "끌 때") };
                case WidgetKind.knob: return new[] { ("cw", "오른쪽으로 돌릴 때 (휠 위로)"), ("ccw", "왼쪽으로 돌릴 때 (휠 아래로)"), ("tap", "클릭"), ("change", "값이 바뀔 때 ({value})") };
                case WidgetKind.slider: return new[] { ("change", "값이 바뀔 때 ({value})") };
                case WidgetKind.timer: return new[] { ("end", "끝났을 때") };
                default: return new[] { ("tap", "클릭"), ("double", "더블클릭"), ("long", "길게 누르기 (0.5초)") };
            }
        }

        void BuildActions()
        {
            actionsTable.SuspendLayout();
            actionsTable.Controls.Clear();
            actionBoxes.Clear();
            foreach (var (key, title) in ActionKeys(wk.kind))
            {
                var l = new Label { Text = title, AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
                var tb = new TextBox
                {
                    Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = 62, Width = 520,
                    Font = new Font("Consolas", 10f), Text = wk.Act(key).Replace("\n", "\r\n"), WordWrap = false,
                };
                var k = key;
                tb.TextChanged += (s, e) => wk.actions[k] = tb.Text.Replace("\r\n", "\n");
                actionBoxes[key] = tb;

                var add = new Button { Text = "+ 동작 ▾", AutoSize = true };
                add.Click += (s, e) =>
                {
                    var m = new ContextMenuStrip();
                    foreach (var t in ActionRunner.Templates)
                    {
                        var line = t[1];
                        m.Items.Add(t[0] + "   " + line, null, (s2, e2) => AppendLine(tb, line));
                    }
                    m.Show(add, new Point(0, add.Height));
                };
                var rec = new Button { Text = "⌨ 키 녹화", AutoSize = true };
                rec.Click += (s, e) => StartRecord(tb, rec);
                var test = new Button { Text = "▶ 테스트", AutoSize = true };
                test.Click += (s, e) => ActionRunner.Run(tb.Text.Replace("\r\n", "\n"),
                    new Dictionary<string, string> { ["value"] = ((int)Math.Round(wk.value)).ToString() }, true);
                tips.SetToolTip(test, "이 칸의 동작을 바로 실행해 봅니다 (Ctrl·Alt를 떼면 실행)");

                actionsTable.Controls.Add(l);
                actionsTable.Controls.Add(tb);
                actionsTable.Controls.Add(Flow(add, rec, test));
            }
            actionsTable.ResumeLayout();
        }

        void BuildOptions()
        {
            optionsPanel.Controls.Clear();
            if (wk.kind == WidgetKind.knob)
            {
                var inf = new CheckBox { Text = "무한 회전 (돌릴 때마다 한 칸씩 동작)", AutoSize = true, Checked = wk.infinite };
                inf.CheckedChanged += (s, e) => { wk.infinite = inf.Checked; preview.Invalidate(); };
                optionsPanel.Controls.Add(inf);
            }
            if (wk.kind == WidgetKind.slider)
            {
                var v = new CheckBox { Text = "세로 슬라이더", AutoSize = true, Checked = wk.vertical };
                v.CheckedChanged += (s, e) => { wk.vertical = v.Checked; preview.Invalidate(); };
                optionsPanel.Controls.Add(v);
            }
            if (wk.kind == WidgetKind.knob || wk.kind == WidgetKind.slider)
            {
                var vol = new CheckBox { Text = "시스템 음량에 연결", AutoSize = true, Checked = wk.bind == "volume" };
                vol.CheckedChanged += (s, e) => wk.bind = vol.Checked ? "volume" : "";
                optionsPanel.Controls.Add(vol);
            }
            if (wk.kind == WidgetKind.timer)
            {
                var num = new NumericUpDown { Minimum = 0.1m, Maximum = 600, DecimalPlaces = 1, Increment = 0.5m, Width = 80, Value = (decimal)Math.Max(0.1, Math.Min(600, wk.duration / 60.0)) };
                num.ValueChanged += (s, e) => { wk.duration = Math.Round((double)num.Value * 60); preview.Invalidate(); };
                optionsPanel.Controls.Add(Flow(new Label { Text = "시간(분)", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, num));
            }
            bool any = optionsPanel.Controls.Count > 0;
            optionsPanel.Visible = optionsLabel.Visible = any;
        }

        void AppendLine(TextBox tb, string line)
        {
            var t = tb.Text.TrimEnd();
            tb.Text = t.Length == 0 ? line : t + "\r\n" + line;
            tb.SelectionStart = tb.Text.Length;
            tb.Focus();
        }

        ContextMenuStrip SymbolMenu()
        {
            var m = new ContextMenuStrip();
            foreach (var kv in Icons.Glyphs.OrderBy(x => x.Key))
            {
                var name = "sf:" + kv.Key;
                Image img = null;
                if (Fonts.SymbolFamily != null)
                {
                    var b = new Bitmap(18, 18);
                    using (var g = Graphics.FromImage(b))
                    {
                        Renderer.Setup(g);
                        Renderer.Text(g, kv.Value.ToString(), Fonts.Get(Fonts.SymbolFamily, 15), Color.Black, new RectangleF(-3, -3, 24, 24));
                    }
                    img = b;
                }
                m.Items.Add(kv.Key, img, (s, e) => iconBox.Text = name);
            }
            return m;
        }

        void PickIconFile()
        {
            using (var d = new OpenFileDialog { Filter = "그림|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|프로그램·바로 가기의 아이콘|*.exe;*.lnk|모든 파일|*.*", Title = "아이콘으로 쓸 파일" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string name = null;
                if (Icons.IsImage(d.FileName) && !d.FileName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) name = Icons.ImportImage(d.FileName);
                else using (var b = Icons.FileIcon(d.FileName)) if (b != null) name = Icons.SaveIcon(b, d.FileName);
                if (name != null) iconBox.Text = name;
                else MessageBox.Show(this, "이 파일은 아이콘으로 쓸 수 없습니다.", "EasyDeck");
            }
        }

        void PickColor(bool background)
        {
            using (var d = new ColorDialog { FullOpen = true, Color = Renderer.Hex(background ? wk.color : wk.textColor, Color.Gray) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (background) wk.color = Renderer.ToHex(d.Color); else wk.textColor = Renderer.ToHex(d.Color);
                RefreshColors();
            }
        }

        void RefreshColors()
        {
            colorBtn.BackColor = Renderer.Hex(wk.color, Color.Gray);
            colorBtn.Text = "";
            textColorBtn.BackColor = Renderer.Hex(wk.textColor, Color.White);
            preview.Invalidate();
        }

        void PaintPreview(Graphics g)
        {
            Renderer.Setup(g);
            g.Clear(DeckForm.DeckBg);
            float c = 76, gap = 8;
            float w = wk.w * c + (wk.w - 1) * gap, h = wk.h * c + (wk.h - 1) * gap;
            float avail = preview.Width - 12;
            float k = Math.Min(avail / w, (preview.Height - 12) / h);
            var r = new RectangleF((preview.Width - w * k) / 2, (preview.Height - h * k) / 2, w * k, h * k);
            Renderer.DrawWidget(g, wk, r, k, (float)Store.S.fontScale, false, false, null);
        }

        void StartRecord(object target, Button btn)
        {
            StopRecord();
            recordTarget = target;
            recordButton = btn;
            recordButtonText = btn.Text;
            btn.Text = "키를 누르세요…";
            btn.Focus();
        }

        void StopRecord()
        {
            if (recordButton != null) recordButton.Text = recordButtonText;
            recordTarget = null;
            recordButton = null;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (recordTarget != null)
            {
                var spec = KeyMap.SpecFromKeys(keyData);
                if (spec == null) return true;
                if (recordTarget == hotkeyBox) { wk.hotkey = spec; hotkeyBox.Text = KeyMap.PrettySpec(spec); }
                else if (recordTarget is TextBox tb) AppendLine(tb, "key " + spec);
                StopRecord();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Save()
        {
            wk.label = labelBox.Text;
            wk.icon = iconBox.Text.Trim();
            foreach (var kv in actionBoxes)
            {
                var t = kv.Value.Text.Replace("\r\n", "\n").Trim();
                if (t.Length == 0) wk.actions.Remove(kv.Key); else wk.actions[kv.Key] = t;
            }
            foreach (var key in wk.actions.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList()) wk.actions.Remove(key);
            bool found = false;
            Store.Mutate(prof =>
            {
                foreach (var pg in prof.pages)
                {
                    int i = pg.widgets.FindIndex(x => x.id == WidgetId);
                    if (i < 0) continue;
                    var old = pg.widgets[i];
                    var n = wk.Clone();
                    n.x = old.x; n.y = old.y; n.w = old.w; n.h = old.h; n.isOn = old.isOn;
                    if (n.kind == WidgetKind.slider && n.kind != old.kind) n.vertical = n.h > n.w;
                    pg.widgets[i] = n;
                    found = true;
                    return;
                }
            });
            if (!found) MessageBox.Show(this, "이 위젯을 찾을 수 없습니다 (다른 곳에서 지워졌을 수 있어요).", "EasyDeck");
            Close();
        }

        void Delete()
        {
            var name = string.IsNullOrEmpty(wk.label) ? KindInfo.Title(wk.kind) : wk.label;
            if (MessageBox.Show(this, "‘" + name + "’ 위젯을 지울까요?", "EasyDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Store.Mutate(p => { foreach (var pg in p.pages) pg.widgets.RemoveAll(x => x.id == WidgetId); });
            Close();
        }
    }

    class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }
}
