import AppKit
import Foundation

// MARK: - 데이터 모델 (profile.json)

enum WidgetKind: String, Codable, CaseIterable {
    case button, toggle, knob, slider, timer

    var title: String {
        switch self {
        case .button: return "버튼"
        case .toggle: return "토글"
        case .knob: return "노브"
        case .slider: return "슬라이더"
        case .timer: return "타이머"
        }
    }
}

extension KeyedDecodingContainer {
    func v<T: Decodable>(_ key: K, _ def: T) -> T {
        (try? decodeIfPresent(T.self, forKey: key)) ?? def
    }
}

struct Widget: Codable, Identifiable, Equatable {
    var id = UUID().uuidString
    var kind: WidgetKind = .button
    var x = 0, y = 0, w = 1, h = 1
    var label = ""
    var icon = ""
    var color = "#2C313C"
    var textColor = "#FFFFFF"
    /// 동작 스크립트: tap/double/long, on/off, cw/ccw/change, end
    var actions: [String: String] = [:]
    var hotkey = ""          // 전역 단축키(버튼/토글/타이머)
    var isOn = false         // 토글 상태
    var value: Double = 50   // 노브/슬라이더 값 0~100
    var bind = ""            // "" | "volume"
    var infinite = true      // 노브: 무한(한 칸씩) / 유한(0~100)
    var vertical = false     // 슬라이더 방향
    var duration: Double = 300 // 타이머 초

    init(kind: WidgetKind = .button, x: Int = 0, y: Int = 0, w: Int = 1, h: Int = 1,
         label: String = "", icon: String = "", color: String = "#2C313C",
         actions: [String: String] = [:]) {
        self.kind = kind; self.x = x; self.y = y; self.w = w; self.h = h
        self.label = label; self.icon = icon; self.color = color; self.actions = actions
    }

    enum CodingKeys: String, CodingKey {
        case id, kind, x, y, w, h, label, icon, color, textColor, actions, hotkey
        case isOn, value, bind, infinite, vertical, duration
    }

    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        id = c.v(.id, UUID().uuidString)
        kind = c.v(.kind, .button)
        x = c.v(.x, 0); y = c.v(.y, 0); w = max(1, c.v(.w, 1)); h = max(1, c.v(.h, 1))
        label = c.v(.label, ""); icon = c.v(.icon, "")
        color = c.v(.color, "#2C313C"); textColor = c.v(.textColor, "#FFFFFF")
        actions = c.v(.actions, [:]); hotkey = c.v(.hotkey, "")
        isOn = c.v(.isOn, false); value = c.v(.value, 50); bind = c.v(.bind, "")
        infinite = c.v(.infinite, true); vertical = c.v(.vertical, false)
        duration = c.v(.duration, 300)
    }

    var rect: (x: Int, y: Int, w: Int, h: Int) { (x, y, w, h) }
}

struct Page: Codable, Identifiable, Equatable {
    var id = UUID().uuidString
    var name = "페이지"
    var cols = 5
    var rows = 3
    var widgets: [Widget] = []
    /// 이 앱들이 앞으로 오면 자동으로 이 페이지로 전환 (번들 ID)
    var apps: [String] = []

    init(name: String, cols: Int = 5, rows: Int = 3, widgets: [Widget] = [], apps: [String] = []) {
        self.name = name; self.cols = cols; self.rows = rows; self.widgets = widgets; self.apps = apps
    }

    enum CodingKeys: String, CodingKey { case id, name, cols, rows, widgets, apps }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        id = c.v(.id, UUID().uuidString); name = c.v(.name, "페이지")
        cols = max(1, c.v(.cols, 5)); rows = max(1, c.v(.rows, 3))
        widgets = c.v(.widgets, []); apps = c.v(.apps, [])
    }
}

// MARK: - 배치 변환

extension Page {
    /// 읽는 순서(위→아래, 왼→오른쪽)
    private var ordered: [Widget] { widgets.sorted { ($0.y, $0.x) < ($1.y, $1.x) } }

    /// 모든 위젯을 가로 한 줄로 (높이 1칸)
    func asSingleRow() -> Page {
        var p = self, cursor = 0
        p.widgets = ordered.map { var w = $0; w.x = cursor; w.y = 0; w.h = 1; cursor += w.w; return w }
        p.cols = max(1, cursor); p.rows = 1
        return p
    }

    /// 모든 위젯을 세로 한 줄로 (너비 1칸)
    func asSingleColumn() -> Page {
        var p = self, cursor = 0
        p.widgets = ordered.map { var w = $0; w.y = cursor; w.x = 0; w.w = 1; cursor += w.h; return w }
        p.rows = max(1, cursor); p.cols = 1
        return p
    }

    /// 가로 ↔ 세로 (행과 열을 맞바꿈, 슬라이더 방향도 같이)
    func transposed() -> Page {
        var p = self
        p.cols = rows; p.rows = cols
        p.widgets = widgets.map {
            var w = $0
            (w.x, w.y, w.w, w.h) = ($0.y, $0.x, $0.h, $0.w)
            if w.kind == .slider { w.vertical = w.h > w.w }
            return w
        }
        return p
    }
}

struct Settings: Codable, Equatable {
    var cellSize: Double = 76
    var gap: Double = 8
    var fontScale: Double = 1.0
    var idleOpacity: Double = 0.55
    var alwaysOnTop = true
    var locked = false
    var hotkey = "opt+cmd+d"
    var autoSwitch = true
    var frameX: Double? = nil
    var frameY: Double? = nil

    init() {}
    enum CodingKeys: String, CodingKey {
        case cellSize, gap, fontScale, idleOpacity, alwaysOnTop, locked, hotkey, autoSwitch, frameX, frameY
    }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        cellSize = c.v(.cellSize, 76); gap = c.v(.gap, 8); fontScale = c.v(.fontScale, 1.0)
        idleOpacity = c.v(.idleOpacity, 0.55); alwaysOnTop = c.v(.alwaysOnTop, true)
        locked = c.v(.locked, false); hotkey = c.v(.hotkey, "opt+cmd+d")
        autoSwitch = c.v(.autoSwitch, true)
        frameX = try? c.decodeIfPresent(Double.self, forKey: .frameX)
        frameY = try? c.decodeIfPresent(Double.self, forKey: .frameY)
    }
}

struct Profile: Codable, Equatable {
    var version = 1
    var settings = Settings()
    var pages: [Page] = []
    var currentPage = 0

    init() {}
    enum CodingKeys: String, CodingKey { case version, settings, pages, currentPage }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        version = c.v(.version, 1); settings = c.v(.settings, Settings())
        pages = c.v(.pages, []); currentPage = c.v(.currentPage, 0)
        if pages.isEmpty { pages = [Page(name: "기본")] }
        currentPage = min(max(0, currentPage), pages.count - 1)
    }

    static func makeDefault() -> Profile {
        var p = Profile()
        let main = Page(name: "기본", cols: 5, rows: 3, widgets: [
            Widget(kind: .button, x: 0, y: 0, label: "복사", icon: "sf:doc.on.doc", color: "#3B5BDB",
                   actions: ["tap": "key cmd+c", "long": "key cmd+x"]),
            Widget(kind: .button, x: 1, y: 0, label: "붙여넣기", icon: "sf:doc.on.clipboard", color: "#3B5BDB",
                   actions: ["tap": "key cmd+v", "double": "key cmd+shift+opt+v"]),
            Widget(kind: .button, x: 2, y: 0, label: "캡처", icon: "sf:camera.viewfinder", color: "#5F3DC4",
                   actions: ["tap": "key cmd+shift+4", "long": "key cmd+shift+5"]),
            Widget(kind: .button, x: 3, y: 0, label: "인사말", icon: "👋", color: "#2B8A3E",
                   actions: ["tap": "type 안녕하세요! 좋은 하루 보내세요 :)"]),
            Widget(kind: .button, x: 4, y: 0, label: "구글", icon: "sf:globe", color: "#1971C2",
                   actions: ["tap": "open https://www.google.com"]),
            Widget(kind: .button, x: 0, y: 1, label: "재생", icon: "sf:playpause.fill", color: "#C2255C",
                   actions: ["tap": "media play", "double": "media next", "long": "media prev"]),
            Widget(kind: .knob, x: 1, y: 1, label: "볼륨", icon: "sf:speaker.wave.2.fill", color: "#E8590C",
                   actions: ["tap": "volume mute"]),
            Widget(kind: .toggle, x: 2, y: 1, label: "다크모드", icon: "sf:moon.fill", color: "#495057",
                   actions: ["on": "darkmode on", "off": "darkmode off"]),
            Widget(kind: .timer, x: 3, y: 1, w: 2, h: 1, label: "집중 타이머", icon: "", color: "#0B7285",
                   actions: ["end": "notify 타이머 종료!\nsound Glass"]),
            Widget(kind: .slider, x: 0, y: 2, w: 3, h: 1, label: "볼륨", icon: "", color: "#E8590C"),
            Widget(kind: .button, x: 3, y: 2, label: "밝게", icon: "sf:sun.max.fill", color: "#F08C00",
                   actions: ["tap": "brightness up"]),
            Widget(kind: .button, x: 4, y: 2, label: "다음 페이지", icon: "sf:arrow.right.circle", color: "#343A40",
                   actions: ["tap": "page next"]),
        ])
        var page1 = main
        if let i = page1.widgets.firstIndex(where: { $0.kind == .slider }) { page1.widgets[i].bind = "volume" }
        if let i = page1.widgets.firstIndex(where: { $0.kind == .knob }) {
            page1.widgets[i].actions["cw"] = "volume +3"
            page1.widgets[i].actions["ccw"] = "volume -3"
        }

        let browser = Page(name: "브라우저", cols: 5, rows: 3, widgets: [
            Widget(kind: .button, x: 0, y: 0, label: "새 탭", icon: "sf:plus.square", color: "#1971C2", actions: ["tap": "key cmd+t"]),
            Widget(kind: .button, x: 1, y: 0, label: "탭 닫기", icon: "sf:xmark.square", color: "#C92A2A", actions: ["tap": "key cmd+w", "long": "key cmd+shift+t"]),
            Widget(kind: .button, x: 2, y: 0, label: "새로고침", icon: "sf:arrow.clockwise", color: "#2B8A3E", actions: ["tap": "key cmd+r", "long": "key cmd+shift+r"]),
            Widget(kind: .knob, x: 3, y: 0, label: "탭 이동", icon: "sf:arrow.left.arrow.right", color: "#5F3DC4",
                   actions: ["cw": "key ctrl+tab", "ccw": "key ctrl+shift+tab"]),
            Widget(kind: .button, x: 4, y: 0, label: "주소창", icon: "sf:magnifyingglass", color: "#343A40", actions: ["tap": "key cmd+l"]),
            Widget(kind: .button, x: 0, y: 1, label: "뒤로", icon: "sf:chevron.left", color: "#343A40", actions: ["tap": "key cmd+["]),
            Widget(kind: .button, x: 1, y: 1, label: "앞으로", icon: "sf:chevron.right", color: "#343A40", actions: ["tap": "key cmd+]"]),
            Widget(kind: .knob, x: 2, y: 1, label: "확대/축소", icon: "sf:plus.magnifyingglass", color: "#E8590C",
                   actions: ["cw": "key cmd+=", "ccw": "key cmd+-", "tap": "key cmd+0"]),
            Widget(kind: .button, x: 4, y: 2, label: "이전 페이지", icon: "sf:arrow.left.circle", color: "#343A40", actions: ["tap": "page prev"]),
        ], apps: ["com.google.Chrome", "com.apple.Safari", "company.thebrowser.Browser", "com.microsoft.edgemac"])

        p.pages = [page1, browser]
        return p
    }
}

// MARK: - 색상

extension NSColor {
    convenience init(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "#", with: "")
        if s.count == 3 { s = s.map { "\($0)\($0)" }.joined() }
        var v: UInt64 = 0
        Scanner(string: s).scanHexInt64(&v)
        if s.count == 8 {
            self.init(srgbRed: CGFloat((v >> 24) & 0xFF) / 255, green: CGFloat((v >> 16) & 0xFF) / 255,
                      blue: CGFloat((v >> 8) & 0xFF) / 255, alpha: CGFloat(v & 0xFF) / 255)
        } else {
            self.init(srgbRed: CGFloat((v >> 16) & 0xFF) / 255, green: CGFloat((v >> 8) & 0xFF) / 255,
                      blue: CGFloat(v & 0xFF) / 255, alpha: 1)
        }
    }

    var hexString: String {
        let c = usingColorSpace(.sRGB) ?? self
        return String(format: "#%02X%02X%02X", Int(round(c.redComponent * 255)),
                      Int(round(c.greenComponent * 255)), Int(round(c.blueComponent * 255)))
    }
}

// MARK: - 저장소

final class Store: ObservableObject {
    static let shared = Store()

    @Published private(set) var profile: Profile
    /// 구조가 바뀌어 덱을 다시 그려야 할 때 호출
    var onStructureChange: (() -> Void)?

    let dir: URL
    var fileURL: URL { dir.appendingPathComponent("profile.json") }
    var iconsDir: URL { dir.appendingPathComponent("icons") }
    var logURL: URL { dir.appendingPathComponent("EasyDeck.log") }

    private var saveWork: DispatchWorkItem?
    private var watcher: DispatchSourceFileSystemObject?
    private var lastWritten: Data?

    private init() {
        dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("EasyDeck", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try? FileManager.default.createDirectory(at: dir.appendingPathComponent("icons"), withIntermediateDirectories: true)
        profile = Profile.makeDefault()
        if let data = try? Data(contentsOf: fileURL) {
            do {
                profile = try JSONDecoder().decode(Profile.self, from: data)
                lastWritten = data
            } catch {
                let bad = dir.appendingPathComponent("profile.broken-\(Int(Date().timeIntervalSince1970)).json")
                try? FileManager.default.copyItem(at: fileURL, to: bad)
                Log.write("profile.json 읽기 실패, 기본값 사용 (원본은 \(bad.lastPathComponent)로 보관): \(error)")
                saveNow()
            }
        } else {
            saveNow()
        }
        startWatching()
    }

    var page: Page { profile.pages[profile.currentPage] }
    var settings: Settings { profile.settings }

    // 구조 변경 (덱 다시 그림)
    func mutate(_ f: (inout Profile) -> Void) {
        f(&profile)
        normalize()
        scheduleSave()
        onStructureChange?()
    }

    // 값만 변경 (드래그 중 등, 다시 그리지 않음)
    func mutateSilently(_ f: (inout Profile) -> Void) {
        f(&profile)
        scheduleSave()
    }

    func updateWidget(_ id: String, silent: Bool = false, _ f: (inout Widget) -> Void) {
        let body: (inout Profile) -> Void = { p in
            for pi in p.pages.indices {
                if let wi = p.pages[pi].widgets.firstIndex(where: { $0.id == id }) {
                    f(&p.pages[pi].widgets[wi]); return
                }
            }
        }
        silent ? mutateSilently(body) : mutate(body)
    }

    func widget(_ id: String) -> Widget? {
        for p in profile.pages { if let w = p.widgets.first(where: { $0.id == id }) { return w } }
        return nil
    }

    private func normalize() {
        if profile.pages.isEmpty { profile.pages = [Page(name: "기본")] }
        profile.currentPage = min(max(0, profile.currentPage), profile.pages.count - 1)
    }

    func replaceProfile(_ p: Profile) {
        profile = p
        normalize()
        saveNow()
        onStructureChange?()
    }

    func resetToDefault() {
        backup(suffix: "backup")
        replaceProfile(Profile.makeDefault())
    }

    func backup(suffix: String) {
        let f = DateFormatter(); f.dateFormat = "yyyyMMdd-HHmmss"
        let dst = dir.appendingPathComponent("profile.\(suffix)-\(f.string(from: Date())).json")
        try? FileManager.default.copyItem(at: fileURL, to: dst)
    }

    private func scheduleSave() {
        saveWork?.cancel()
        let w = DispatchWorkItem { [weak self] in self?.saveNow() }
        saveWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4, execute: w)
    }

    func saveNow() {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        guard let data = try? enc.encode(profile) else { return }
        lastWritten = data
        try? data.write(to: fileURL, options: .atomic)
        startWatching() // atomic 쓰기는 파일을 교체하므로 감시를 다시 붙임
    }

    /// 외부 편집기로 profile.json을 고치면 자동으로 다시 읽음
    private func startWatching() {
        watcher?.cancel()
        let fd = open(fileURL.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let src = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd,
                                                            eventMask: [.write, .delete, .rename], queue: .main)
        src.setEventHandler { [weak self] in
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { self?.reloadFromDisk() }
        }
        src.setCancelHandler { close(fd) }
        src.resume()
        watcher = src
    }

    func reloadFromDisk() {
        guard let data = try? Data(contentsOf: fileURL) else { startWatching(); return }
        if data == lastWritten { startWatching(); return }
        do {
            let p = try JSONDecoder().decode(Profile.self, from: data)
            lastWritten = data
            profile = p
            normalize()
            onStructureChange?()
            Log.write("profile.json 변경 감지 → 다시 불러옴")
            Toast.show("설정 파일을 다시 불러왔습니다")
        } catch {
            Log.write("profile.json 형식 오류: \(error)")
            Toast.show("profile.json 형식 오류 – 로그 확인")
        }
        startWatching()
    }
}

// MARK: - 로그

enum Log {
    private static let q = DispatchQueue(label: "easydeck.log")
    private static let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("EasyDeck/EasyDeck.log")
    static func write(_ s: String) {
        let line = "[\(ISO8601DateFormatter().string(from: Date()))] \(s)\n"
        q.async {
            if let h = try? FileHandle(forWritingTo: url) {
                h.seekToEndOfFile(); h.write(line.data(using: .utf8)!); try? h.close()
            } else {
                try? line.data(using: .utf8)!.write(to: url)
            }
        }
    }
}
