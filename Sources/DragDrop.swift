import AppKit
import UniformTypeIdentifiers

// MARK: - 끌어다 놓기로 덱 편집
//
// • Finder의 앱·파일·폴더, 브라우저의 링크, 선택한 글자를 덱에 놓으면 버튼이 생김
// • 이미지 파일을 버튼 위에 놓으면 그 버튼의 아이콘으로 바꿀 수 있음
// • 편집 모드의 팔레트에서 위젯 종류를 끌어다 놓아 추가
// • 빈 칸이 없으면 덱에 줄(또는 칸)을 늘려서 넣음

let kWidgetKindType = NSPasteboard.PasteboardType("org.easypastor.easydeck.kind")
private let kURLNameType = NSPasteboard.PasteboardType("public.url-name")

extension Widget {
    /// 새 위젯의 기본 모양
    static func template(_ k: WidgetKind) -> Widget {
        var w = Widget(kind: k, label: k.title)
        switch k {
        case .button: w.icon = "sf:star.fill"; w.color = "#3B5BDB"
        case .toggle: w.icon = "sf:power"; w.color = "#2B8A3E"
        case .knob: w.icon = "sf:dial.medium"; w.color = "#E8590C"
            w.actions = ["cw": "volume +3", "ccw": "volume -3"]
        case .slider: w.color = "#E8590C"
        case .timer: w.color = "#0B7285"
        }
        return w
    }
}

// MARK: - 빈 자리 찾기 / 덱 늘리기

extension Page {
    func fits(_ x: Int, _ y: Int, _ w: Int, _ h: Int, ignoring: Set<String> = []) -> Bool {
        if x < 0 || y < 0 || x + w > cols || y + h > rows { return false }
        for o in widgets where !ignoring.contains(o.id) {
            if x < o.x + o.w && o.x < x + w && y < o.y + o.h && o.y < y + h { return false }
        }
        return true
    }

    /// 위젯을 원하는 칸(없으면 첫 빈 자리)에 넣음. 자리가 없으면 덱을 늘림.
    mutating func place(_ w: Widget, at cell: (Int, Int)? = nil) -> Widget {
        var w = w
        w.w = min(w.w, max(cols, 1)); w.h = min(w.h, max(rows, 1))
        if let (x, y) = cell, fits(x, y, w.w, w.h) {
            w.x = x; w.y = y; widgets.append(w); return w
        }
        // 놓은 칸에 안 들어가면 너비 1칸으로라도
        if let (x, y) = cell, w.w > 1, fits(x, y, 1, w.h) {
            w.x = x; w.y = y; w.w = 1; widgets.append(w); return w
        }
        for y in 0..<rows {
            for x in 0..<cols where fits(x, y, w.w, w.h) {
                w.x = x; w.y = y; widgets.append(w); return w
            }
        }
        // 자리가 없으면: 가로 한 줄 덱은 오른쪽으로, 나머지는 아래로 늘림
        if rows == 1 && cols > 1 {
            w.x = cols; w.y = 0; w.h = 1; cols += w.w
        } else {
            w.x = 0; w.y = rows; rows += w.h
            if w.w > cols { w.w = cols }
        }
        widgets.append(w)
        return w
    }
}

// MARK: - 끌어온 것 → 버튼

enum DropMaker {
    static let readTypes: [NSPasteboard.PasteboardType] = [kWidgetKindType, .fileURL, .URL, .string]

    enum Item {
        case kind(WidgetKind)
        case file(URL)
        case web(URL, String?)
        case text(String)
    }

    static func items(from pb: NSPasteboard) -> [Item] {
        if let k = pb.string(forType: kWidgetKindType).flatMap(WidgetKind.init(rawValue:)) { return [.kind(k)] }
        let files = (pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
        if !files.isEmpty { return files.map { .file($0) } }
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: nil) as? [URL],
           let u = urls.first, let s = u.scheme?.lowercased(), s == "http" || s == "https" {
            return [.web(u, pb.string(forType: kURLNameType))]
        }
        if let s = pb.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty {
            if !s.contains(" "), !s.contains("\n"), let u = URL(string: s), let sc = u.scheme?.lowercased(),
               sc == "http" || sc == "https" {
                return [.web(u, nil)]
            }
            return [.text(s)]
        }
        return []
    }

    static func short(_ s: String, _ n: Int = 12) -> String {
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "\n", with: " ")
        return t.count > n ? String(t.prefix(n)) + "…" : t
    }

    static func isImage(_ url: URL) -> Bool {
        guard let t = UTType(filenameExtension: url.pathExtension.lowercased()) else { return false }
        return t.conforms(to: .image)
    }

    /// 앱·파일 아이콘을 icons 폴더에 PNG로 저장하고 파일 이름을 돌려줌
    static func saveIcon(for url: URL) -> String? {
        let img = NSWorkspace.shared.icon(forFile: url.path)
        let side: CGFloat = 128
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(side), pixelsHigh: Int(side),
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        img.draw(in: NSRect(x: 0, y: 0, width: side, height: side))
        NSGraphicsContext.restoreGraphicsState()
        guard let png = rep.representation(using: .png, properties: [:]) else { return nil }
        let base = url.deletingPathExtension().lastPathComponent
            .components(separatedBy: CharacterSet(charactersIn: "/:\\?%*|\"<>")).joined(separator: "_")
        // 같은 파일은 늘 같은 이름 (FNV-1a 해시, 실행할 때마다 바뀌지 않음)
        var hash: UInt32 = 2166136261
        for b in url.path.utf8 { hash = (hash ^ UInt32(b)) &* 16777619 }
        let name = "\(base)-\(String(format: "%08x", hash)).png"
        let dst = Store.shared.iconsDir.appendingPathComponent(name)
        try? png.write(to: dst)
        IconCache.images[name] = nil
        return name
    }

    /// 끌어온 것 하나로 새 위젯을 만듦
    static func widget(for item: Item) -> Widget {
        switch item {
        case .kind(let k):
            return Widget.template(k)
        case .file(let url):
            var w = Widget(kind: .button)
            let isDir = (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false
            if url.pathExtension.lowercased() == "app" {
                w.label = short(FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: ""))
                let bid = Bundle(url: url)?.bundleIdentifier
                w.actions["tap"] = "open \(bid ?? url.path)"
                w.color = "#343A40"
            } else {
                w.label = short(url.lastPathComponent)
                w.actions["tap"] = "open \(url.path)"
                w.color = isDir ? "#1971C2" : "#495057"
            }
            w.icon = saveIcon(for: url) ?? (isDir ? "sf:folder.fill" : "sf:doc.fill")
            return w
        case .web(let url, let title):
            var w = Widget(kind: .button)
            let host = (url.host ?? url.absoluteString).replacingOccurrences(of: "www.", with: "")
            w.label = short(title?.isEmpty == false ? title! : host)
            w.icon = "sf:globe"
            w.color = "#1971C2"
            w.actions["tap"] = "open \(url.absoluteString)"
            return w
        case .text(let s):
            var w = Widget(kind: .button)
            w.label = short(s, 10)
            w.icon = "sf:text.cursor"
            w.color = "#2B8A3E"
            let esc = s.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\n", with: "\\n")
            // 여러 줄·긴 글은 붙여넣기가 더 안정적
            w.actions["tap"] = (s.contains("\n") || s.count > 40 ? "paste " : "type ") + esc
            return w
        }
    }
}

// MARK: - 덱이 끌어다 놓기를 받는 부분 (DeckContentView에서 호출)

extension DeckContentView {
    func dragCell(_ sender: NSDraggingInfo) -> (Int, Int)? {
        dc.cellAt(convert(sender.draggingLocation, from: nil))
    }

    func dragUpdate(_ sender: NSDraggingInfo) -> NSDragOperation {
        let items = DropMaker.items(from: sender.draggingPasteboard)
        guard !items.isEmpty else { ghost.isHidden = true; return [] }
        if let (x, y) = dragCell(sender) {
            if let w = widgetAt(x, y) {
                // 기존 위젯 위: 파란색 (바꾸기/아이콘)
                ghost.frame = dc.cellRect(w.x, w.y, w.w, w.h)
                ghost.mode = .swap
            } else {
                ghost.frame = dc.cellRect(x, y, 1, 1)
                ghost.mode = .ok
            }
            ghost.isHidden = false
            addSubview(ghost, positioned: .above, relativeTo: nil)
        } else {
            ghost.isHidden = true
        }
        if !mouseInside { mouseInside = true; updateOpacity() }
        return .copy
    }

    func dragEnded() {
        ghost.isHidden = true
    }

    func performDrop(_ sender: NSDraggingInfo) -> Bool {
        ghost.isHidden = true
        let items = DropMaker.items(from: sender.draggingPasteboard)
        guard !items.isEmpty else { return false }
        let cell = dragCell(sender)
        let target = cell.flatMap { widgetAt($0.0, $0.1) }

        // 기존 위젯 위에 하나만 놓았으면 무엇을 할지 묻기 (드롭이 끝난 뒤 메뉴 표시)
        var isKind = false
        if case .kind = items[0] { isKind = true }
        if let t = target, items.count == 1, !isKind {
            let loc = convert(sender.draggingLocation, from: nil)
            DispatchQueue.main.async { self.askDropOnWidget(t, items[0], at: loc) }
            return true
        }
        add(items, at: target == nil ? cell : nil)
        return true
    }

    /// 새 위젯들을 추가 (첫 번째는 놓은 칸에, 나머지는 빈 자리에)
    func add(_ items: [DropMaker.Item], at cell: (Int, Int)?) {
        let made = items.map(DropMaker.widget(for:))
        var placed: [Widget] = []
        Store.shared.mutate { p in
            var page = p.pages[p.currentPage]
            for (i, w) in made.enumerated() { placed.append(page.place(w, at: i == 0 ? cell : nil)) }
            p.pages[p.currentPage] = page
        }
        if case .kind = items[0] {
            Toast.show("\(made[0].kind.title)를 추가했어요 · 눌러서 동작을 정하세요")
        } else {
            Toast.show(made.count == 1 ? "‘\(made[0].label)’ 버튼을 만들었어요" : "버튼 \(made.count)개를 만들었어요")
        }
        if dc.editing, placed.count == 1, case .kind = items[0] { AppDelegate.shared.openInspector(placed[0].id) }
    }

    private func askDropOnWidget(_ t: Widget, _ item: DropMaker.Item, at loc: NSPoint) {
        let m = NSMenu()
        func add(_ title: String, _ f: @escaping () -> Void) {
            let it = NSMenuItem(title: title, action: #selector(DropMenuTarget.run(_:)), keyEquivalent: "")
            let target = DropMenuTarget(f)
            it.target = target
            it.representedObject = target   // 메뉴가 떠 있는 동안 유지
            m.addItem(it)
        }
        let name = t.label.isEmpty ? t.kind.title : t.label
        if case .file(let url) = item, DropMaker.isImage(url) {
            add("‘\(name)’ 아이콘으로 쓰기") {
                let dst = Store.shared.iconsDir.appendingPathComponent(url.lastPathComponent)
                if dst.path != url.path {
                    try? FileManager.default.removeItem(at: dst)
                    try? FileManager.default.copyItem(at: url, to: dst)
                }
                IconCache.images[url.lastPathComponent] = nil
                Store.shared.updateWidget(t.id) { $0.icon = url.lastPathComponent }
            }
        }
        let made = DropMaker.widget(for: item)
        if t.kind == .button, let action = made.actions["tap"] {
            add("‘\(name)’ 클릭 동작을 이것으로 바꾸기") {
                Store.shared.updateWidget(t.id) { $0.actions["tap"] = action }
                Toast.show("‘\(name)’ 동작을 바꿨어요")
            }
            add("‘\(name)’ 클릭 동작 끝에 덧붙이기") {
                Store.shared.updateWidget(t.id) {
                    let cur = $0.actions["tap"] ?? ""
                    $0.actions["tap"] = cur.isEmpty ? action : cur + "\n" + action
                }
                Toast.show("‘\(name)’에 동작을 덧붙였어요")
            }
        }
        add("새 버튼으로 추가") { [weak self] in self?.add([item], at: nil) }
        m.addItem(.separator())
        add("취소") {}
        m.popUp(positioning: nil, at: loc, in: self)
    }
}

final class DropMenuTarget: NSObject {
    let f: () -> Void
    init(_ f: @escaping () -> Void) { self.f = f }
    @objc func run(_ sender: Any?) { f() }
}

// MARK: - 편집 모드 위젯 팔레트

final class PaletteController {
    static let shared = PaletteController()
    private var panel: NSPanel?

    func show() {
        let p = panel ?? make()
        panel = p
        position()
        p.orderFrontRegardless()
    }

    func hide() { panel?.orderOut(nil) }

    func position() {
        guard let p = panel else { return }
        let deck = DeckController.shared.panel.frame
        let vf = DeckController.shared.panel.screen?.visibleFrame ?? NSScreen.main?.visibleFrame ?? .zero
        let size = p.frame.size
        var x = deck.maxX + 8
        if x + size.width > vf.maxX { x = deck.minX - size.width - 8 }
        let y = max(vf.minY, min(deck.maxY - size.height, vf.maxY - size.height))
        p.setFrameOrigin(NSPoint(x: x, y: y))
    }

    private func make() -> NSPanel {
        let tile: CGFloat = 58, gap: CGFloat = 6, pad: CGFloat = 8, head: CGFloat = 22, foot: CGFloat = 58
        let kinds = WidgetKind.allCases
        let w = tile + pad * 2 + 30
        let h = head + CGFloat(kinds.count) * (tile + gap) - gap + pad + foot
        let p = DeckPanel(contentRect: NSRect(x: 0, y: 0, width: w, height: h),
                          styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false; p.backgroundColor = .clear; p.hasShadow = true
        p.level = .floating
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let bg = NSVisualEffectView(frame: NSRect(x: 0, y: 0, width: w, height: h))
        bg.material = .hudWindow; bg.blendingMode = .behindWindow; bg.state = .active
        bg.wantsLayer = true; bg.layer?.cornerRadius = 12; bg.layer?.masksToBounds = true
        p.contentView = bg

        let title = NSTextField(labelWithString: "끌어서 추가")
        title.font = .systemFont(ofSize: 10.5, weight: .semibold)
        title.textColor = NSColor.systemYellow
        title.alignment = .center
        title.frame = NSRect(x: 0, y: h - head + 2, width: w, height: 16)
        bg.addSubview(title)

        for (i, k) in kinds.enumerated() {
            let y = h - head - CGFloat(i + 1) * (tile + gap) + gap
            let t = PaletteTile(kind: k)
            t.frame = NSRect(x: (w - tile) / 2, y: y, width: tile, height: tile)
            bg.addSubview(t)
        }

        let hint = NSTextField(wrappingLabelWithString: "앱·파일·링크·글자도 덱에 바로 놓으세요")
        hint.font = .systemFont(ofSize: 9.5)
        hint.textColor = NSColor.white.withAlphaComponent(0.6)
        hint.alignment = .center
        hint.frame = NSRect(x: 4, y: 4, width: w - 8, height: foot - 10)
        bg.addSubview(hint)
        return p
    }
}

final class PaletteTile: NSView, NSDraggingSource {
    let kind: WidgetKind
    private var down = NSPoint.zero
    private var dragging = false

    init(kind: WidgetKind) {
        self.kind = kind
        super.init(frame: .zero)
        toolTip = "\(kind.title): 덱으로 끌어다 놓거나 클릭하면 빈 자리에 추가"
    }
    required init?(coder: NSCoder) { fatalError() }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        let t = Widget.template(kind)
        NSColor(hex: t.color).setFill()
        NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 10, yRadius: 10).fill()
        let sym: String
        switch kind {
        case .button: sym = "sf:square.fill"
        case .toggle: sym = "sf:switch.2"
        case .knob: sym = "sf:dial.medium"
        case .slider: sym = "sf:slider.horizontal.3"
        case .timer: sym = "sf:timer"
        }
        let s = bounds.width * 0.36
        IconCache.draw(sym, in: NSRect(x: bounds.midX - s / 2, y: bounds.midY - s / 2 + 6, width: s, height: s), tint: .white)
        let p = NSMutableParagraphStyle(); p.alignment = .center
        (kind.title as NSString).draw(in: NSRect(x: 0, y: 5, width: bounds.width, height: 14),
                                      withAttributes: [.font: NSFont.systemFont(ofSize: 10, weight: .semibold),
                                                       .foregroundColor: NSColor.white, .paragraphStyle: p])
    }

    override func mouseDown(with event: NSEvent) { down = event.locationInWindow; dragging = false }

    override func mouseDragged(with event: NSEvent) {
        guard !dragging, hypot(event.locationInWindow.x - down.x, event.locationInWindow.y - down.y) > 3 else { return }
        dragging = true
        let item = NSPasteboardItem()
        item.setString(kind.rawValue, forType: kWidgetKindType)
        let di = NSDraggingItem(pasteboardWriter: item)
        let img = NSImage(size: bounds.size)
        if let rep = bitmapImageRepForCachingDisplay(in: bounds) {
            cacheDisplay(in: bounds, to: rep); img.addRepresentation(rep)
        }
        di.setDraggingFrame(bounds, contents: img)
        beginDraggingSession(with: [di], event: event, source: self)
    }

    override func mouseUp(with event: NSEvent) {
        if !dragging { DeckController.shared.content?.add([.kind(kind)], at: nil) }
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        context == .withinApplication ? .copy : []
    }
}

// MARK: - 개발용 자체 테스트: EASYDECK_SELFTEST=1 로 실행하면 결과를 출력하고 종료

enum SelfTest {
    static func run() {
        var fails = 0
        func check(_ c: Bool, _ msg: String) { print(c ? "✅" : "❌", msg); if !c { fails += 1 } }

        let calc = URL(fileURLWithPath: "/System/Applications/Calculator.app")
        let a = DropMaker.widget(for: .file(calc))
        print("  앱:", a.label, a.icon, a.actions["tap"] ?? "")
        check(a.actions["tap"] == "open com.apple.calculator" && a.icon.hasSuffix(".png"), "앱 → 번들 ID로 열기 + 앱 아이콘")
        let dl = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads")
        let d = DropMaker.widget(for: .file(dl))
        print("  폴더:", d.label, d.actions["tap"] ?? "")
        check(d.actions["tap"] == "open \(dl.path)", "폴더 → 경로 열기")
        let u = DropMaker.widget(for: .web(URL(string: "https://www.youtube.com/watch?v=1")!, "유튜브 영상 제목이 아주 깁니다"))
        print("  링크:", u.label, u.actions["tap"] ?? "")
        check(u.actions["tap"] == "open https://www.youtube.com/watch?v=1" && u.label.hasSuffix("…"), "링크 → 열기, 긴 제목 줄임")
        let t1 = DropMaker.widget(for: .text("안녕하세요"))
        let t2 = DropMaker.widget(for: .text("첫 줄\n둘째 줄"))
        print("  글자:", t1.actions["tap"] ?? "", "|", t2.actions["tap"] ?? "")
        check(t1.actions["tap"] == "type 안녕하세요", "짧은 글 → type")
        check(t2.actions["tap"] == "paste 첫 줄\\n둘째 줄", "여러 줄 → paste, 줄바꿈 보존")

        let pb = NSPasteboard(name: NSPasteboard.Name("easydeck.selftest"))
        pb.clearContents(); pb.setString("https://example.com", forType: .string)
        if case .web = DropMaker.items(from: pb).first { check(true, "주소 글자 → 링크로 인식") } else { check(false, "주소 글자 → 링크로 인식") }
        pb.clearContents(); pb.writeObjects([calc as NSURL])
        if case .file = DropMaker.items(from: pb).first { check(true, "파일 끌기 인식") } else { check(false, "파일 끌기 인식") }
        pb.clearContents(); pb.setString("knob", forType: kWidgetKindType)
        if case .kind(.knob) = DropMaker.items(from: pb).first { check(true, "팔레트 위젯 인식") } else { check(false, "팔레트 위젯 인식") }

        // 자리 찾기
        var col = Page(name: "t", cols: 1, rows: 2, widgets: [Widget(x: 0, y: 0), Widget(x: 0, y: 1)])
        let n1 = col.place(Widget())
        check(col.rows == 3 && n1.y == 2, "세로 한 줄이 꽉 차면 아래로 한 칸 늘림")
        var row = Page(name: "t", cols: 2, rows: 1, widgets: [Widget(x: 0, y: 0), Widget(x: 1, y: 0)])
        let n2 = row.place(Widget())
        check(row.cols == 3 && n2.x == 2, "가로 한 줄이 꽉 차면 오른쪽으로 늘림")
        var grid = Page(name: "t", cols: 3, rows: 2, widgets: [Widget(x: 0, y: 0)])
        let n3 = grid.place(Widget(), at: (2, 1))
        check(n3.x == 2 && n3.y == 1 && grid.rows == 2, "놓은 빈 칸에 그대로 넣음")
        let n4 = grid.place(Widget(), at: (0, 0))
        check(!(n4.x == 0 && n4.y == 0) && grid.rows == 2, "놓은 칸이 차 있으면 다른 빈 자리")
        var wide = Page(name: "t", cols: 3, rows: 1, widgets: [Widget(x: 1, y: 0)])
        var big = Widget(); big.w = 2
        let n5 = wide.place(big, at: (0, 0))
        check(n5.x == 0 && n5.w == 1, "넓은 위젯이 안 들어가면 1칸으로")

        // 팔레트 모양 저장
        PaletteController.shared.show()
        if let path = ProcessInfo.processInfo.environment["EASYDECK_SELFTEST_PNG"],
           let v = NSApp.windows.first(where: { $0.isVisible && $0 !== DeckController.shared.panel && $0.frame.height > 300 })?.contentView,
           let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) {
            v.cacheDisplay(in: v.bounds, to: rep)
            try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
        }
        print(fails == 0 ? "모두 통과" : "실패 \(fails)개")
        exit(fails == 0 ? 0 : 1)
    }
}
