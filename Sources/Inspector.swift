import AppKit
import SwiftUI
import UniformTypeIdentifiers

// MARK: - 단축키 녹화 버튼

final class RecorderState: ObservableObject {
    @Published var recording = false
    var monitor: Any?
}

struct KeyRecorder: View {
    @Binding var combo: String
    var placeholder = "녹화"
    var onRecorded: ((String) -> Void)? = nil
    @StateObject private var st = RecorderState()

    var body: some View {
        HStack(spacing: 4) {
            Button(action: toggle) {
                Text(st.recording ? "키를 누르세요… (esc 취소)" : (combo.isEmpty || onRecorded != nil ? placeholder : Keys.pretty(combo)))
                    .frame(minWidth: 90)
            }
            .foregroundColor(st.recording ? .orange : .primary)
            if onRecorded == nil && !combo.isEmpty {
                Button { combo = "" } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.borderless)
            }
        }
        .onDisappear(perform: stop)
    }

    private func toggle() { st.recording ? stop() : start() }

    private func start() {
        st.recording = true
        st.monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            if e.keyCode == 53 && e.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty {
                stop(); return nil
            }
            if let s = Keys.string(from: e) {
                if let cb = onRecorded { cb(s) } else { combo = s }
                stop()
            }
            return nil
        }
    }

    private func stop() {
        st.recording = false
        if let m = st.monitor { NSEvent.removeMonitor(m); st.monitor = nil }
    }
}

// MARK: - 동작 편집기

struct ActionEditor: View {
    let title: String
    let hint: String
    @Binding var text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title).font(.headline)
                Text(hint).font(.caption).foregroundColor(.secondary)
                Spacer()
                KeyRecorder(combo: .constant(""), placeholder: "⌨︎ 키 녹화") { s in append("key \(s)") }
                Menu("＋ 동작") {
                    ForEach(ActionCatalog.templates, id: \.1) { t in
                        Button(t.0) { append(t.1) }
                    }
                }
                .frame(width: 90)
                Button("▶︎") { ActionRunner.run(text, vars: ["value": "50"]) }
                    .help("지금 테스트 실행")
            }
            TextEditor(text: $text)
                .font(.system(.body, design: .monospaced))
                .frame(minHeight: 54, maxHeight: 110)
                .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.3)))
        }
    }

    private func append(_ line: String) {
        if text.isEmpty || text.hasSuffix("\n") { text += line } else { text += "\n" + line }
    }
}

// MARK: - 위젯 편집 창

final class InspectorModel: ObservableObject {
    @Published var w: Widget
    let original: Widget
    init(_ w: Widget) { self.w = w; original = w }
}

struct InspectorView: View {
    @ObservedObject var m: InspectorModel
    var close: () -> Void

    private func act(_ key: String) -> Binding<String> {
        Binding(get: { m.w.actions[key] ?? "" }, set: { m.w.actions[key] = $0 })
    }

    private var bg: Binding<Color> {
        Binding(get: { Color(nsColor: NSColor(hex: m.w.color)) },
                set: { m.w.color = NSColor($0).hexString })
    }
    private var fg: Binding<Color> {
        Binding(get: { Color(nsColor: NSColor(hex: m.w.textColor)) },
                set: { m.w.textColor = NSColor($0).hexString })
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    header
                    Divider()
                    appearance
                    Divider()
                    behaviour
                    Divider()
                    Text("동작은 한 줄에 하나씩, 위에서부터 순서대로 실행됩니다. '＋ 동작' 메뉴에서 예시를 넣어 고쳐 쓰세요.")
                        .font(.caption).foregroundColor(.secondary)
                }
                .padding(18)
            }
            Divider()
            HStack {
                Button(role: .destructive) {
                    let id = m.w.id
                    Store.shared.mutate { p in for i in p.pages.indices { p.pages[i].widgets.removeAll { $0.id == id } } }
                    close()
                } label: { Text("삭제") }
                Spacer()
                Button("취소") { close() }.keyboardShortcut(.cancelAction)
                Button("저장") { save() }.keyboardShortcut(.defaultAction)
            }
            .padding(12)
        }
        .frame(width: 560, height: 640)
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 16) {
            preview
            VStack(alignment: .leading, spacing: 8) {
                Picker("종류", selection: $m.w.kind) {
                    ForEach(WidgetKind.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                TextField("이름 (아래에 표시)", text: $m.w.label)
                HStack {
                    Text("크기")
                    Stepper("가로 \(m.w.w)칸", value: $m.w.w, in: 1...12)
                    Stepper("세로 \(m.w.h)칸", value: $m.w.h, in: 1...12)
                }
            }
        }
    }

    private var preview: some View {
        let nsBG = NSColor(hex: m.w.color)
        return ZStack {
            RoundedRectangle(cornerRadius: 12).fill(Color(nsColor: nsBG))
            VStack(spacing: 4) {
                PreviewIcon(icon: m.w.icon, tint: NSColor(hex: m.w.textColor)).frame(width: 34, height: 34)
                if !m.w.label.isEmpty {
                    Text(m.w.label).font(.system(size: 11, weight: .semibold))
                        .foregroundColor(Color(nsColor: NSColor(hex: m.w.textColor))).lineLimit(1)
                }
            }.padding(6)
        }
        .frame(width: 84, height: 84)
    }

    private var appearance: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("모양").font(.headline)
            HStack {
                TextField("아이콘: 이모지·글자, sf:star.fill, 또는 이미지 파일", text: $m.w.icon)
                Menu("예시") {
                    ForEach(["sf:star.fill", "sf:bolt.fill", "sf:play.fill", "sf:mic.fill", "sf:video.fill",
                             "sf:camera.fill", "sf:folder.fill", "sf:terminal.fill", "sf:globe", "sf:envelope.fill",
                             "sf:speaker.wave.2.fill", "sf:moon.fill", "sf:sun.max.fill", "sf:lock.fill",
                             "🔥", "✅", "📌", "🎵", "☕️", "🙏", "A", "Ⅰ"], id: \.self) { s in
                        Button(s) { m.w.icon = s }
                    }
                }.frame(width: 60)
                Button("파일…") { pickIcon() }
            }
            Text("SF 심볼 이름은 'SF Symbols' 앱이나 developer.apple.com/sf-symbols 에서 찾을 수 있어요.")
                .font(.caption).foregroundColor(.secondary)
            HStack(spacing: 20) {
                ColorPicker("배경색", selection: bg, supportsOpacity: false)
                ColorPicker("글자·아이콘색", selection: fg, supportsOpacity: false)
                HStack(spacing: 4) {
                    ForEach(["#3B5BDB", "#5F3DC4", "#C2255C", "#E8590C", "#F08C00", "#2B8A3E", "#0B7285", "#343A40"], id: \.self) { c in
                        Circle().fill(Color(nsColor: NSColor(hex: c))).frame(width: 16, height: 16)
                            .onTapGesture { m.w.color = c }
                    }
                }
            }
        }
    }

    @ViewBuilder private var behaviour: some View {
        VStack(alignment: .leading, spacing: 12) {
            switch m.w.kind {
            case .button:
                hotkeyRow
                ActionEditor(title: "클릭", hint: "", text: act("tap"))
                ActionEditor(title: "더블클릭", hint: "비워두면 클릭이 즉시 실행돼요", text: act("double"))
                ActionEditor(title: "길게 누르기", hint: "0.5초", text: act("long"))
            case .toggle:
                hotkeyRow
                Toggle("현재 켜짐 상태", isOn: $m.w.isOn)
                ActionEditor(title: "켤 때", hint: "", text: act("on"))
                ActionEditor(title: "끌 때", hint: "", text: act("off"))
            case .knob:
                Picker("노브 종류", selection: $m.w.infinite) {
                    Text("무한 노브 (한 칸씩 동작)").tag(true)
                    Text("유한 노브 (0~100 값)").tag(false)
                }.pickerStyle(.radioGroup)
                if m.w.infinite {
                    ActionEditor(title: "시계방향", hint: "휠 위 / 위로 드래그", text: act("cw"))
                    ActionEditor(title: "반시계방향", hint: "휠 아래 / 아래로 드래그", text: act("ccw"))
                } else {
                    valueRows
                }
                ActionEditor(title: "누르기", hint: "노브 클릭", text: act("tap"))
                ActionEditor(title: "길게 누르기", hint: "", text: act("long"))
            case .slider:
                Toggle("세로 슬라이더", isOn: $m.w.vertical)
                valueRows
            case .timer:
                hotkeyRow
                HStack {
                    Text("시간")
                    Stepper("\(Int(m.w.duration) / 60)분 \(Int(m.w.duration) % 60)초",
                            value: $m.w.duration, in: 10...86400, step: 60)
                    Stepper("", value: $m.w.duration, in: 10...86400, step: 10).labelsHidden()
                    Text("(±10초)").font(.caption).foregroundColor(.secondary)
                }
                Text("클릭: 시작/일시정지 · 길게: 초기화 · 휠: ±1분").font(.caption).foregroundColor(.secondary)
                ActionEditor(title: "끝났을 때", hint: "비워두면 알림+소리", text: act("end"))
            }
        }
    }

    private var hotkeyRow: some View {
        HStack {
            Text("전역 단축키").font(.headline)
            KeyRecorder(combo: $m.w.hotkey)
            Text("어느 앱에서든 이 키로 실행").font(.caption).foregroundColor(.secondary)
        }
    }

    private var valueRows: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("값 연결", selection: $m.w.bind) {
                Text("없음 (아래 동작만 실행)").tag("")
                Text("시스템 음량").tag("volume")
            }
            HStack {
                Text("현재 값 \(Int(m.w.value))")
                Slider(value: $m.w.value, in: 0...100)
            }
            ActionEditor(title: "값이 바뀔 때", hint: "{value} = 0~100", text: act("change"))
        }
    }

    private func pickIcon() {
        let p = NSOpenPanel()
        p.allowedContentTypes = [.png, .jpeg, .svg, .icns, .image]
        p.canChooseDirectories = false
        guard p.runModal() == .OK, let url = p.url else { return }
        let dst = Store.shared.iconsDir.appendingPathComponent(url.lastPathComponent)
        if !FileManager.default.fileExists(atPath: dst.path) { try? FileManager.default.copyItem(at: url, to: dst) }
        IconCache.images[url.lastPathComponent] = nil
        m.w.icon = url.lastPathComponent
    }

    private func save() {
        var w = m.w
        // 칸 수가 넘치면 덱 안으로
        if let page = Store.shared.profile.pages.first(where: { $0.widgets.contains { $0.id == w.id } }) {
            w.w = min(w.w, page.cols); w.h = min(w.h, page.rows)
            w.x = min(w.x, page.cols - w.w); w.y = min(w.y, page.rows - w.h)
        }
        if w.duration != m.original.duration { DeckController.shared.timers[w.id] = nil }
        IconCache.images[w.icon] = nil
        Store.shared.updateWidget(w.id) { $0 = w }
        close()
    }
}

struct PreviewIcon: NSViewRepresentable {
    let icon: String
    let tint: NSColor
    func makeNSView(context: Context) -> IconNSView { IconNSView() }
    func updateNSView(_ v: IconNSView, context: Context) { v.icon = icon; v.tint = tint; v.needsDisplay = true }
    final class IconNSView: NSView {
        var icon = ""; var tint = NSColor.white
        override var isFlipped: Bool { true }
        override func draw(_ dirtyRect: NSRect) { IconCache.draw(icon, in: bounds, tint: tint) }
    }
}

// MARK: - 설정 창

final class SettingsModel: ObservableObject {
    @Published var p: Profile
    @Published var selected = 0
    @Published var launchAtLogin = LoginItem.enabled {
        didSet { if launchAtLogin != LoginItem.enabled { LoginItem.set(launchAtLogin) } }
    }
    init() { p = Store.shared.profile }
}

struct SettingsView: View {
    @ObservedObject var m: SettingsModel
    var close: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            TabView {
                general.tabItem { Text("일반") }.padding(16)
                pages.tabItem { Text("페이지") }.padding(16)
                help.tabItem { Text("사용법") }.padding(16)
            }
            .padding(10)
            Divider()
            HStack {
                Menu("가져오기/내보내기") {
                    Button("프로필 내보내기…") { exportProfile() }
                    Button("프로필 가져오기…") { importProfile() }
                    Divider()
                    Button("기본 배치로 되돌리기 (현재 배치는 백업)") {
                        Store.shared.resetToDefault(); m.p = Store.shared.profile
                    }
                }.frame(width: 160)
                Spacer()
                Button("닫기") { close() }.keyboardShortcut(.cancelAction)
                Button("적용") { apply(); close() }.keyboardShortcut(.defaultAction)
            }
            .padding(12)
        }
        .frame(width: 600, height: 560)
    }

    private var general: some View {
        Form {
            Section {
                slider("버튼 크기", $m.p.settings.cellSize, 40...180, "%.0f pt")
                slider("버튼 간격", $m.p.settings.gap, 0...24, "%.0f pt")
                slider("글자·아이콘 크기", $m.p.settings.fontScale, 0.6...1.6, "×%.1f")
                slider("평소 선명도", $m.p.settings.idleOpacity, 0.05...1, "%.0f%%", mult: 100)
                Text("마우스를 덱 밖으로 치우면 이 선명도로 흐려집니다. Ctrl+휠로 크기를 바로 바꿀 수도 있어요.")
                    .font(.caption).foregroundColor(.secondary)
            }
            Section {
                Toggle("항상 위에 표시", isOn: $m.p.settings.alwaysOnTop)
                Toggle("위치 고정 (베젤 드래그로 이동 안 함)", isOn: $m.p.settings.locked)
                Toggle("앞에 있는 앱에 맞춰 페이지 자동 전환", isOn: $m.p.settings.autoSwitch)
                Toggle("로그인 시 자동 실행", isOn: $m.launchAtLogin)
                HStack {
                    Text("덱 보이기/숨기기 단축키")
                    KeyRecorder(combo: $m.p.settings.hotkey)
                }
            }
            Section {
                HStack {
                    Text(AXIsProcessTrusted() ? "✅ 손쉬운 사용 권한 허용됨" : "⚠️ 손쉬운 사용 권한 필요 (키 입력·글자 입력용)")
                    Spacer()
                    Button("시스템 설정 열기") { Permissions.openAccessibilitySettings() }
                }
            }
        }
        .formStyle(.grouped)
    }

    private func slider(_ t: String, _ v: Binding<Double>, _ r: ClosedRange<Double>, _ fmt: String, mult: Double = 1) -> some View {
        HStack {
            Text(t).frame(width: 110, alignment: .leading)
            Slider(value: v, in: r)
            Text(String(format: fmt, v.wrappedValue * mult)).monospacedDigit().frame(width: 60, alignment: .trailing)
        }
    }

    private var pages: some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(spacing: 6) {
                List(selection: Binding(get: { m.selected }, set: { m.selected = $0 ?? 0 })) {
                    ForEach(Array(m.p.pages.enumerated()), id: \.offset) { i, pg in
                        Text("\(i + 1). \(pg.name)").tag(i)
                    }
                }
                .frame(width: 170)
                HStack {
                    Button { m.p.pages.append(Page(name: "새 페이지")); m.selected = m.p.pages.count - 1 } label: { Image(systemName: "plus") }
                    Button {
                        guard m.p.pages.count > 1 else { return }
                        m.p.pages.remove(at: m.selected); m.selected = max(0, m.selected - 1)
                    } label: { Image(systemName: "minus") }
                    Button { move(-1) } label: { Image(systemName: "arrow.up") }
                    Button { move(1) } label: { Image(systemName: "arrow.down") }
                    Button {
                        var c = m.p.pages[m.selected]; c.id = UUID().uuidString; c.name += " 사본"
                        c.widgets = c.widgets.map { var w = $0; w.id = UUID().uuidString; return w }
                        m.p.pages.insert(c, at: m.selected + 1); m.selected += 1
                    } label: { Image(systemName: "plus.square.on.square") }.help("복제")
                }.buttonStyle(.borderless)
            }
            if m.p.pages.indices.contains(m.selected) {
                Form {
                    TextField("이름", text: $m.p.pages[m.selected].name)
                    Stepper("가로 \(m.p.pages[m.selected].cols)칸", value: $m.p.pages[m.selected].cols, in: 1...16)
                    Stepper("세로 \(m.p.pages[m.selected].rows)칸", value: $m.p.pages[m.selected].rows, in: 1...12)
                    Text("칸을 줄여 밖으로 나가는 위젯은 적용할 때 안쪽으로 옮겨집니다.").font(.caption).foregroundColor(.secondary)
                    HStack {
                        Text("배치")
                        Button("가로 한 줄") { m.p.pages[m.selected] = m.p.pages[m.selected].asSingleRow() }
                        Button("세로 한 줄") { m.p.pages[m.selected] = m.p.pages[m.selected].asSingleColumn() }
                        Button("가로↔세로") { m.p.pages[m.selected] = m.p.pages[m.selected].transposed() }
                    }
                    Section("이 앱이 앞에 오면 자동으로 이 페이지로") {
                        ForEach(m.p.pages[m.selected].apps, id: \.self) { bid in
                            HStack {
                                Text(appName(bid)); Text(bid).font(.caption).foregroundColor(.secondary)
                                Spacer()
                                Button { m.p.pages[m.selected].apps.removeAll { $0 == bid } } label: {
                                    Image(systemName: "xmark.circle.fill")
                                }.buttonStyle(.borderless)
                            }
                        }
                        HStack {
                            Button("앱 선택…") { pickApp() }
                            if let last = DeckController.shared.lastFrontApp, let bid = last.bundleIdentifier {
                                Button("방금 쓰던 앱 추가 (\(last.localizedName ?? bid))") {
                                    if !m.p.pages[m.selected].apps.contains(bid) { m.p.pages[m.selected].apps.append(bid) }
                                }
                            }
                        }
                    }
                }
                .formStyle(.grouped)
            }
        }
    }

    private var help: some View {
        ScrollView {
            Text(HelpText.body).font(.system(size: 12)).textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func move(_ d: Int) {
        let t = m.selected + d
        guard m.p.pages.indices.contains(t) else { return }
        m.p.pages.swapAt(m.selected, t); m.selected = t
    }

    private func appName(_ bid: String) -> String {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bid) else { return bid }
        return FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: "")
    }

    private func pickApp() {
        let p = NSOpenPanel()
        p.allowedContentTypes = [.application]
        p.directoryURL = URL(fileURLWithPath: "/Applications")
        guard p.runModal() == .OK, let url = p.url, let bid = Bundle(url: url)?.bundleIdentifier else { return }
        if !m.p.pages[m.selected].apps.contains(bid) { m.p.pages[m.selected].apps.append(bid) }
    }

    private func apply() {
        var p = m.p
        for i in p.pages.indices {
            let cols = p.pages[i].cols, rows = p.pages[i].rows
            for j in p.pages[i].widgets.indices {
                var w = p.pages[i].widgets[j]
                w.w = min(w.w, cols); w.h = min(w.h, rows)
                w.x = min(w.x, cols - w.w); w.y = min(w.y, rows - w.h)
                p.pages[i].widgets[j] = w
            }
        }
        // 현재 페이지 유지 (설정 창이 열린 동안 바뀌었을 수 있음)
        p.currentPage = min(Store.shared.profile.currentPage, p.pages.count - 1)
        Store.shared.mutate { $0 = p }
    }

    private func exportProfile() {
        let s = NSSavePanel()
        s.nameFieldStringValue = "EasyDeck-profile.json"
        s.allowedContentTypes = [.json]
        guard s.runModal() == .OK, let url = s.url else { return }
        Store.shared.saveNow()
        try? FileManager.default.removeItem(at: url)
        try? FileManager.default.copyItem(at: Store.shared.fileURL, to: url)
    }

    private func importProfile() {
        let o = NSOpenPanel()
        o.allowedContentTypes = [.json]
        guard o.runModal() == .OK, let url = o.url, let data = try? Data(contentsOf: url) else { return }
        do {
            let p = try JSONDecoder().decode(Profile.self, from: data)
            guard confirmRiskyImport(p) else { return }
            Store.shared.backup(suffix: "before-import")
            Store.shared.replaceProfile(p)
            m.p = p
        } catch {
            NSAlert(error: error).runModal()
        }
    }
}

/// 다른 사람이 준 프로필에는 명령 실행 동작이 들어 있을 수 있으므로 가져오기 전에 모두 보여주고 확인
func confirmRiskyImport(_ p: Profile) -> Bool {
    // 표시용 정리: 제어문자·방향전환·폭 없는 문자를 지우고 길이 제한
    func clean(_ s: String, max: Int = 160) -> String {
        let t = String(s.unicodeScalars.filter { u in
            !(CharacterSet.controlCharacters.contains(u) || (0x200B...0x200F).contains(u.value)
              || (0x202A...0x202E).contains(u.value) || (0x2066...0x2069).contains(u.value) || u.value == 0xFEFF)
        }.map(Character.init))
        return t.count > max ? String(t.prefix(max)) + "…" : t
    }
    var danger: [String] = [], other: [String] = []
    for page in p.pages {
        for w in page.widgets {
            let name = "[\(clean(page.name, max: 30))] \(clean(w.label.isEmpty ? w.kind.title : w.label, max: 30))"
            if !w.hotkey.isEmpty { other.append("\(name) — 전역 단축키 \(clean(Keys.pretty(w.hotkey), max: 30))") }
            for (_, script) in w.actions.sorted(by: { $0.key < $1.key }) {
                for raw in script.components(separatedBy: .newlines) {
                    let line = raw.trimmingCharacters(in: .whitespaces)
                    if line.isEmpty || line.hasPrefix("#") { continue }
                    switch ActionRunner.risk(of: ActionRunner.split(line).0) {
                    case .safe: break
                    // 명령·입력 동작은 자르지 않고 전체를 보여줌 (공백 연속은 하나로)
                    case .runsCode, .input:
                        let full = clean(line.split(whereSeparator: { $0 == " " || $0 == "\t" }).joined(separator: " "),
                                         max: Int.max)
                        danger.append("\(name)\n    ⚠️ \(full)")
                    }
                }
            }
        }
    }
    if danger.isEmpty && other.isEmpty { return true }

    var text = ""
    if !danger.isEmpty { text += "■ 명령 실행·키 입력·열기 동작 (\(danger.count)개, 전체 표시)\n" + danger.joined(separator: "\n") + "\n\n" }
    if !other.isEmpty { text += "■ 전역 단축키 (\(other.count)개)\n" + other.joined(separator: "\n") }

    let a = NSAlert()
    a.alertStyle = .warning
    a.messageText = danger.isEmpty ? "이 프로필에는 전역 단축키가 들어 있습니다"
                                   : "이 프로필에는 명령 실행·키 입력 동작이 들어 있습니다"
    a.informativeText = "믿을 수 있는 사람이 준 파일만 가져오세요. 셸·애플스크립트 명령과 키 입력 동작은 컴퓨터의 파일을 바꾸거나 지울 수 있습니다. 아래 목록을 모두 확인하세요."
    let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 460, height: 240))
    scroll.hasVerticalScroller = true
    scroll.borderType = .bezelBorder
    let tv = NSTextView(frame: scroll.bounds)
    tv.isEditable = false
    tv.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
    tv.string = text
    tv.autoresizingMask = [.width]
    scroll.documentView = tv
    a.accessoryView = scroll
    a.addButton(withTitle: "취소")
    a.addButton(withTitle: "가져오기")
    return a.runModal() == .alertSecondButtonReturn
}

// MARK: - 로그인 시 실행

import ServiceManagement

enum LoginItem {
    static var enabled: Bool { SMAppService.mainApp.status == .enabled }
    static func set(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            Log.write("로그인 항목 설정 실패: \(error)")
            Toast.show("로그인 항목 설정 실패: 앱을 /Applications 로 옮긴 뒤 다시 시도하세요", atScreen: true)
        }
    }
}

enum HelpText {
    static let body = """
    EasyDeck 사용법

    ■ 기본 조작
    • 상단 베젤을 끌면 이동, 더블클릭하면 편집 모드
    • Ctrl + 휠: 덱 크기 조절 · 베젤 위에서 휠/가로 스와이프: 페이지 넘기기
    • ⌥⌘D (설정에서 변경): 어느 앱에서든 덱 보이기/숨기기
    • 덱을 눌러도 지금 쓰던 앱의 포커스가 그대로 유지됩니다.

    ■ 위젯
    • 버튼: 클릭 / 더블클릭 / 길게(0.5초) 각각 다른 동작
    • 토글: 켤 때·끌 때 동작
    • 노브: 휠·드래그로 회전. 무한(한 칸씩 동작) / 유한(0~100 값)
    • 슬라이더: 드래그·클릭·휠. 가로/세로. 시스템 음량에 연결 가능
    • 타이머: 클릭 시작/일시정지, 길게 초기화, 휠 ±1분, 끝나면 지정 동작
    • 버튼·토글·타이머에는 전역 단축키를 붙일 수 있어요.

    ■ 편집 모드 (✎)
    • 빈 칸(+) 클릭 → 위젯 추가
    • 위젯 드래그 → 이동 (초록=가능, 빨강=겹침)
    • 오른쪽 아래 모서리 드래그 → 크기 조절
    • 위젯 클릭 → 편집 창, 오른쪽 클릭 → 복제/삭제

    ■ 동작 (한 줄에 하나, 위에서부터 순서대로)
    key cmd+shift+4        단축키 (여러 개: key cmd+k cmd+s)
    type 안녕하세요\\n반가워요  글자 입력 (\\n 줄바꿈)
    paste 긴 문장           클립보드 경유 붙여넣기(원래 클립보드 복원)
    open https://… | ~/Downloads | Safari | com.apple.Notes
    shell 명령              zsh로 창 없이 실행
    applescript 코드        애플스크립트 한 줄
    shortcut 이름           단축어 앱의 단축어 실행
    volume up|down|mute|50|+5|-5
    media play|next|prev
    brightness up|down
    darkmode toggle|on|off
    delay 0.5               대기(초)
    page 이름|next|prev|2    페이지 전환
    layout row|column|flip|undo  배치: 가로 한 줄 / 세로 한 줄 / 가로↔세로 / 되돌리기
    notify 메시지 · sound Glass · deck hide|show|toggle
    {value}                 노브/슬라이더 값 (0~100)
    # 으로 시작하는 줄은 주석

    ■ 배치 바꾸기 (≡ 메뉴 › 배치 바꾸기, 또는 설정 › 페이지)
    • 가로 한 줄 / 세로 한 줄 / 가로↔세로 바꾸기, 바로 되돌리기 가능
    • 상단 ⚙︎ 톱니바퀴 = 설정 창

    ■ 페이지
    • 여러 페이지를 만들고, 특정 앱이 앞에 오면 그 페이지로 자동 전환되게 할 수 있어요.

    ■ 파일
    ~/Library/Application Support/EasyDeck/
      profile.json  배치·동작 (직접 고치면 바로 반영)
      icons/        추가한 아이콘
      EasyDeck.log  실행 기록

    ■ 권한
    키 입력·글자 입력에는 '손쉬운 사용' 권한이 필요합니다.
    다크 모드 전환은 처음 한 번 'System Events' 제어 허용을 묻습니다.
    """
}
