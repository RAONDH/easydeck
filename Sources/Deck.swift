import AppKit

// MARK: - 패널 (클릭해도 작업 중인 앱의 포커스를 빼앗지 않음)

final class DeckPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

// MARK: - 런타임 상태 (덱을 다시 그려도 유지)

final class TimerRun {
    var remaining: Double
    var running = false
    var endDate: Date?
    var ticker: Timer?
    init(_ d: Double) { remaining = d }
}

// MARK: - 덱 컨트롤러

final class DeckController: NSObject, NSWindowDelegate {
    static let shared = DeckController()

    let panel: DeckPanel
    private let blur = NSVisualEffectView()
    private(set) var content: DeckContentView!
    private(set) var editing = false
    private var manualPage = 0
    private var autoSwitched = false
    var lastFrontApp: NSRunningApplication?
    var timers: [String: TimerRun] = [:]
    var knobAngles: [String: CGFloat] = [:]

    let bezelH: CGFloat = 30
    let pad: CGFloat = 10

    private override init() {
        panel = DeckPanel(contentRect: NSRect(x: 200, y: 200, width: 400, height: 300),
                          styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init()
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.delegate = self
        panel.acceptsMouseMovedEvents = true

        blur.material = .hudWindow
        blur.blendingMode = .behindWindow
        blur.state = .active
        blur.wantsLayer = true
        blur.layer?.cornerRadius = 14
        blur.layer?.masksToBounds = true
        blur.layer?.borderWidth = 0.5
        blur.layer?.borderColor = NSColor.white.withAlphaComponent(0.12).cgColor
        panel.contentView = blur

        Store.shared.onStructureChange = { [weak self] in self?.rebuild() }
        manualPage = Store.shared.profile.currentPage

        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(appActivated(_:)),
                                                          name: NSWorkspace.didActivateApplicationNotification, object: nil)
        // rebuild()는 shared 초기화가 끝난 뒤 start()에서 호출 (재진입 방지)
    }

    func start() {
        rebuild()
        show()
        // 개발용: EASYDECK_SNAPSHOT=/경로.png 로 실행하면 덱 화면을 PNG로 저장
        if let path = ProcessInfo.processInfo.environment["EASYDECK_SNAPSHOT"] {
            if ProcessInfo.processInfo.environment["EASYDECK_SNAPSHOT_EDIT"] != nil { setEditing(true) }
            if let l = ProcessInfo.processInfo.environment["EASYDECK_SNAPSHOT_LAYOUT"] { changeLayout(l) }
            let env = ProcessInfo.processInfo.environment
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                if env["EASYDECK_SNAPSHOT_INSPECTOR"] != nil, let w = Store.shared.page.widgets.first(where: { $0.kind == .button }) {
                    AppDelegate.shared.openInspector(w.id)
                } else if env["EASYDECK_SNAPSHOT_SETTINGS"] != nil {
                    AppDelegate.shared.openSettings(tab: Int(env["EASYDECK_SNAPSHOT_SETTINGS"]!) ?? 0)
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                    let win = NSApp.windows.first { $0.isVisible && $0 !== self.panel && !($0 is NSPanel) && $0.contentView?.bounds.width ?? 0 > 300 }
                    self.snapshot(to: path, view: win?.contentView)
                }
            }
        }
    }

    func snapshot(to path: String, view: NSView? = nil) {
        guard let v = view ?? panel.contentView, let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { return }
        let bg = NSImage(size: v.bounds.size)
        v.cacheDisplay(in: v.bounds, to: rep)
        bg.lockFocus()
        NSColor(white: 0.13, alpha: 1).setFill()
        NSBezierPath(roundedRect: v.bounds, xRadius: 14, yRadius: 14).fill()
        rep.draw(in: v.bounds)
        bg.unlockFocus()
        if let tiff = bg.tiffRepresentation, let out = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
            try? out.write(to: URL(fileURLWithPath: path))
        }
    }

    var s: Settings { Store.shared.settings }

    // MARK: 레이아웃

    func cellRect(_ x: Int, _ y: Int, _ w: Int, _ h: Int) -> NSRect {
        let c = CGFloat(s.cellSize), g = CGFloat(s.gap)
        return NSRect(x: pad + CGFloat(x) * (c + g), y: bezelH + CGFloat(y) * (c + g),
                      width: CGFloat(w) * c + CGFloat(w - 1) * g, height: CGFloat(h) * c + CGFloat(h - 1) * g)
    }

    func cellAt(_ p: NSPoint) -> (Int, Int)? {
        let c = CGFloat(s.cellSize), g = CGFloat(s.gap)
        let gx = Int(floor((p.x - pad) / (c + g))), gy = Int(floor((p.y - bezelH) / (c + g)))
        let page = Store.shared.page
        guard gx >= 0, gy >= 0, gx < page.cols, gy < page.rows else { return nil }
        return (gx, gy)
    }

    var deckSize: NSSize {
        let page = Store.shared.page
        let c = CGFloat(s.cellSize), g = CGFloat(s.gap)
        return NSSize(width: pad * 2 + CGFloat(page.cols) * c + CGFloat(page.cols - 1) * g,
                      height: bezelH + pad + CGFloat(page.rows) * c + CGFloat(page.rows - 1) * g)
    }

    func rebuild() {
        let size = deckSize
        // 왼쪽 위 모서리를 기준으로 크기 변경
        var topLeft: NSPoint
        if panel.isVisible || content != nil {
            topLeft = NSPoint(x: panel.frame.minX, y: panel.frame.maxY)
        } else if let x = s.frameX, let y = s.frameY {
            topLeft = NSPoint(x: x, y: y)
        } else {
            let vf = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
            topLeft = NSPoint(x: vf.maxX - size.width - 30, y: vf.maxY - 30)
        }
        topLeft = clampToScreen(topLeft, size)
        panel.setFrame(NSRect(x: topLeft.x, y: topLeft.y - size.height, width: size.width, height: size.height), display: true)

        content?.removeFromSuperview()
        content = DeckContentView(frame: NSRect(origin: .zero, size: size))
        content.autoresizingMask = [.width, .height]
        blur.addSubview(content)
        content.build()

        panel.level = s.alwaysOnTop ? .floating : .normal
        if !editing && !content.mouseInside { panel.alphaValue = CGFloat(max(0.06, s.idleOpacity)) }
        registerHotKeys()
    }

    func redraw(widget id: String) {
        content?.widgetViews.first(where: { $0.widgetID == id })?.needsDisplay = true
    }

    private func clampToScreen(_ tl: NSPoint, _ size: NSSize) -> NSPoint {
        let screens = NSScreen.screens.map(\.visibleFrame)
        let r = NSRect(x: tl.x, y: tl.y - size.height, width: size.width, height: size.height)
        if screens.contains(where: { $0.intersects(r.insetBy(dx: 40, dy: 40)) }) { return tl }
        let vf = NSScreen.main?.visibleFrame ?? .zero
        return NSPoint(x: vf.maxX - size.width - 30, y: vf.maxY - 30)
    }

    func windowDidMove(_ notification: Notification) {
        let f = panel.frame
        Store.shared.mutateSilently { $0.settings.frameX = f.minX; $0.settings.frameY = f.maxY }
    }

    // MARK: 표시

    func show() {
        panel.orderFrontRegardless()
        panel.alphaValue = 1
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in self?.content?.updateOpacity() }
    }
    func hide() { panel.orderOut(nil) }
    func toggleVisible() { panel.isVisible ? hide() : show() }

    func setEditing(_ on: Bool) {
        editing = on
        content?.applyEditing()
        panel.alphaValue = 1
        if !on { content?.updateOpacity() }
    }

    func setAlwaysOnTop(_ on: Bool) {
        Store.shared.mutate { $0.settings.alwaysOnTop = on }
    }

    // MARK: 페이지

    func switchPage(_ arg: String) {
        let store = Store.shared
        let n = store.profile.pages.count
        var idx = store.profile.currentPage
        let a = arg.trimmingCharacters(in: .whitespaces)
        switch a.lowercased() {
        case "next", "+": idx = (idx + 1) % n
        case "prev", "previous", "-": idx = (idx - 1 + n) % n
        default:
            if let i = store.profile.pages.firstIndex(where: { $0.name == a }) { idx = i }
            else if let i = Int(a), i >= 1, i <= n { idx = i - 1 }
            else { Toast.show("페이지 '\(a)' 없음"); return }
        }
        goToPage(idx, manual: true)
    }

    // MARK: 배치 바꾸기 (한 줄 / 세로 한 줄 / 가로↔세로)

    /// 되돌리기용: 바꾸기 전 모든 페이지의 배치(칸 수·위치·크기)
    private var layoutUndo: [Page]?

    /// 모든 페이지에 같은 배치 변환을 적용 (페이지를 넘겨도 같은 모양 유지)
    func changeLayout(_ arg: String) {
        let store = Store.shared
        let transform: (Page) -> Page
        switch arg.lowercased() {
        case "row", "한줄", "가로한줄": transform = { $0.asSingleRow() }
        case "column", "col", "세로한줄": transform = { $0.asSingleColumn() }
        case "flip", "rotate", "뒤집기": transform = { $0.transposed() }
        case "undo", "되돌리기":
            guard let old = layoutUndo else { Toast.show("되돌릴 배치가 없어요"); return }
            layoutUndo = nil
            store.mutate { p in
                for i in p.pages.indices {
                    guard let o = old.first(where: { $0.id == p.pages[i].id }) else { continue }
                    p.pages[i].cols = o.cols; p.pages[i].rows = o.rows
                    for j in p.pages[i].widgets.indices {
                        guard let ow = o.widgets.first(where: { $0.id == p.pages[i].widgets[j].id }) else { continue }
                        p.pages[i].widgets[j].x = ow.x; p.pages[i].widgets[j].y = ow.y
                        p.pages[i].widgets[j].w = ow.w; p.pages[i].widgets[j].h = ow.h
                        p.pages[i].widgets[j].vertical = ow.vertical
                    }
                }
            }
            Toast.show("모든 페이지를 이전 배치로 되돌렸어요")
            return
        default:
            Toast.show("layout row|column|flip|undo"); return
        }
        layoutUndo = store.profile.pages
        store.mutate { p in p.pages = p.pages.map(transform) }
        let cur = store.page
        Toast.show("모든 페이지를 바꿨어요 (\(cur.cols)×\(cur.rows)) · 메뉴 › 배치 바꾸기 › 되돌리기")
    }

    func goToPage(_ idx: Int, manual: Bool) {
        if manual { manualPage = idx; autoSwitched = false }
        guard idx != Store.shared.profile.currentPage else { return }
        Store.shared.mutate { $0.currentPage = idx }
    }

    @objc private func appActivated(_ n: Notification) {
        guard let app = n.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
        if app.bundleIdentifier == Bundle.main.bundleIdentifier { return }
        lastFrontApp = app
        guard s.autoSwitch, !editing, let bid = app.bundleIdentifier else { return }
        if let i = Store.shared.profile.pages.firstIndex(where: { $0.apps.contains(bid) }) {
            if !autoSwitched { manualPage = Store.shared.profile.currentPage }
            autoSwitched = true
            goToPage(i, manual: false)
        } else if autoSwitched {
            autoSwitched = false
            goToPage(min(manualPage, Store.shared.profile.pages.count - 1), manual: false)
        }
    }

    // MARK: 전역 단축키

    func registerHotKeys() {
        HotKeys.shared.removeAll()
        if !s.hotkey.isEmpty { HotKeys.shared.register(s.hotkey) { [weak self] in self?.toggleVisible() } }
        for page in Store.shared.profile.pages {
            for w in page.widgets where !w.hotkey.isEmpty {
                let id = w.id
                HotKeys.shared.register(w.hotkey) { [weak self] in self?.triggerByHotkey(id) }
            }
        }
    }

    private func triggerByHotkey(_ id: String) {
        guard let w = Store.shared.widget(id) else { return }
        switch w.kind {
        case .toggle: toggle(id)
        case .timer: timerStartPause(id)
        default: ActionRunner.run(w.actions["tap"])
        }
        redraw(widget: id)
    }

    // MARK: 위젯 동작 (뷰와 무관하게 상태 유지)

    func toggle(_ id: String) {
        guard let w = Store.shared.widget(id) else { return }
        let on = !w.isOn
        Store.shared.updateWidget(id, silent: true) { $0.isOn = on }
        ActionRunner.run(w.actions[on ? "on" : "off"])
        redraw(widget: id)
    }

    func timerRun(_ w: Widget) -> TimerRun {
        if let t = timers[w.id] { return t }
        let t = TimerRun(w.duration)
        timers[w.id] = t
        return t
    }

    func timerStartPause(_ id: String) {
        guard let w = Store.shared.widget(id) else { return }
        let t = timerRun(w)
        if t.running {
            t.remaining = max(0, t.endDate?.timeIntervalSinceNow ?? t.remaining)
            t.running = false
            t.ticker?.invalidate()
        } else {
            if t.remaining <= 0 { t.remaining = w.duration }
            t.running = true
            t.endDate = Date().addingTimeInterval(t.remaining)
            t.ticker?.invalidate()
            t.ticker = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
                self?.timerTick(id)
            }
        }
        redraw(widget: id)
    }

    func timerReset(_ id: String) {
        guard let w = Store.shared.widget(id) else { return }
        let t = timerRun(w)
        t.ticker?.invalidate()
        t.running = false
        t.remaining = w.duration
        redraw(widget: id)
    }

    func timerAdjust(_ id: String, by sec: Double) {
        guard let w = Store.shared.widget(id) else { return }
        let t = timerRun(w)
        if t.running {
            t.endDate = t.endDate?.addingTimeInterval(sec)
        } else {
            let nd = max(60, min(24 * 3600, w.duration + sec))
            Store.shared.updateWidget(id, silent: true) { $0.duration = nd }
            t.remaining = nd
        }
        redraw(widget: id)
    }

    private func timerTick(_ id: String) {
        guard let t = timers[id], t.running, let end = t.endDate else { return }
        t.remaining = max(0, end.timeIntervalSinceNow)
        if t.remaining <= 0 {
            t.running = false
            t.ticker?.invalidate()
            let w = Store.shared.widget(id)
            let script = w?.actions["end"] ?? ""
            if script.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                Toast.show("⏰ \(w?.label.isEmpty == false ? w!.label : "타이머") 종료")
                NSSound(named: "Glass")?.play()
            } else {
                ActionRunner.run(script)
            }
        }
        redraw(widget: id)
    }

    /// 덱에 마우스가 들어오면 음량에 묶인 위젯 값을 실제 음량과 맞춤
    func syncBoundValues() {
        let bound = Store.shared.page.widgets.filter { $0.bind == "volume" }
        guard !bound.isEmpty else { return }
        DispatchQueue.global().async {
            guard let v = ActionRunner.currentVolume() else { return }
            DispatchQueue.main.async {
                for w in bound where abs(w.value - v) > 0.5 {
                    Store.shared.updateWidget(w.id, silent: true) { $0.value = v }
                    self.redraw(widget: w.id)
                }
            }
        }
    }

    // MARK: 메뉴

    func mainMenu() -> NSMenu {
        let m = NSMenu()
        func item(_ t: String, _ sel: Selector, _ key: String = "") -> NSMenuItem {
            let i = NSMenuItem(title: t, action: sel, keyEquivalent: key)
            i.target = AppDelegate.shared
            return i
        }
        let pages = NSMenuItem(title: "페이지", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for (i, p) in Store.shared.profile.pages.enumerated() {
            let it = NSMenuItem(title: "\(i + 1). \(p.name)", action: #selector(AppDelegate.pickPage(_:)), keyEquivalent: "")
            it.target = AppDelegate.shared
            it.tag = i
            it.state = i == Store.shared.profile.currentPage ? .on : .off
            sub.addItem(it)
        }
        pages.submenu = sub
        m.addItem(pages)
        let layout = NSMenuItem(title: "배치 바꾸기", action: nil, keyEquivalent: "")
        let lsub = NSMenu()
        for (t, a) in [("가로 한 줄로", "row"), ("세로 한 줄로", "column"), ("가로 ↔ 세로 바꾸기", "flip"), ("되돌리기", "undo")] {
            let it = NSMenuItem(title: t, action: #selector(AppDelegate.layoutMenu(_:)), keyEquivalent: "")
            it.target = AppDelegate.shared
            it.representedObject = a
            if a == "undo" { lsub.addItem(.separator()) }
            lsub.addItem(it)
        }
        layout.submenu = lsub
        m.addItem(layout)
        m.addItem(.separator())
        let e = item(editing ? "편집 끝내기" : "편집 모드", #selector(AppDelegate.toggleEdit))
        m.addItem(e)
        m.addItem(item("설정 및 페이지 관리…", #selector(AppDelegate.openSettingsMenu), ","))
        let top = item("항상 위에 표시", #selector(AppDelegate.toggleTop))
        top.state = s.alwaysOnTop ? .on : .off
        m.addItem(top)
        let lock = item("위치 고정", #selector(AppDelegate.toggleLock))
        lock.state = s.locked ? .on : .off
        m.addItem(lock)
        m.addItem(.separator())
        m.addItem(item("profile.json 직접 편집", #selector(AppDelegate.editJSON)))
        m.addItem(item("설정 폴더 열기", #selector(AppDelegate.openFolder)))
        m.addItem(item("손쉬운 사용 권한 확인", #selector(AppDelegate.checkPermission)))
        m.addItem(item("사용법", #selector(AppDelegate.showHelp)))
        m.addItem(.separator())
        m.addItem(item("덱 숨기기", #selector(AppDelegate.hideDeck)))
        m.addItem(item("EasyDeck 종료", #selector(AppDelegate.quit), "q"))
        return m
    }
}

// MARK: - 덱 내용

final class DeckContentView: NSView {
    override var isFlipped: Bool { true }
    var widgetViews: [WidgetView] = []
    var mouseInside = false
    private var bezel: BezelView!
    let ghost = GhostView()
    private var tracking: NSTrackingArea?

    var dc: DeckController { DeckController.shared }

    func build() {
        subviews.forEach { $0.removeFromSuperview() }
        widgetViews.removeAll()
        bezel = BezelView(frame: NSRect(x: 0, y: 0, width: bounds.width, height: dc.bezelH))
        bezel.autoresizingMask = [.width]
        addSubview(bezel)
        bezel.build()
        for w in Store.shared.page.widgets {
            let v = WidgetView(widgetID: w.id)
            v.frame = dc.cellRect(w.x, w.y, w.w, w.h)
            addSubview(v)
            widgetViews.append(v)
        }
        ghost.isHidden = true
        addSubview(ghost)
        applyEditing()
    }

    func applyEditing() {
        needsDisplay = true
        bezel?.build()
        widgetViews.forEach { $0.needsDisplay = true }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = tracking { removeTrackingArea(t) }
        tracking = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                  owner: self, userInfo: nil)
        addTrackingArea(tracking!)
    }

    override func mouseEntered(with event: NSEvent) {
        mouseInside = true
        updateOpacity()
        dc.syncBoundValues()
    }

    override func mouseExited(with event: NSEvent) {
        mouseInside = false
        updateOpacity()
    }

    func updateOpacity() {
        let target: CGFloat = (mouseInside || dc.editing) ? 1 : CGFloat(max(0.06, dc.s.idleOpacity))
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.18
            window?.animator().alphaValue = target
        }
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        guard dc.editing else { return }
        let page = Store.shared.page
        NSColor.white.withAlphaComponent(0.18).setStroke()
        for y in 0..<page.rows {
            for x in 0..<page.cols {
                let p = NSBezierPath(roundedRect: dc.cellRect(x, y, 1, 1).insetBy(dx: 1, dy: 1), xRadius: 10, yRadius: 10)
                p.lineWidth = 1
                p.setLineDash([4, 4], count: 2, phase: 0)
                p.stroke()
            }
        }
        let plus = "+" as NSString
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 18, weight: .light),
                                                    .foregroundColor: NSColor.white.withAlphaComponent(0.35)]
        for y in 0..<page.rows {
            for x in 0..<page.cols where widgetAt(x, y) == nil {
                let r = dc.cellRect(x, y, 1, 1)
                let sz = plus.size(withAttributes: attrs)
                plus.draw(at: NSPoint(x: r.midX - sz.width / 2, y: r.midY - sz.height / 2), withAttributes: attrs)
            }
        }
    }

    func widgetAt(_ x: Int, _ y: Int) -> Widget? {
        Store.shared.page.widgets.first { x >= $0.x && x < $0.x + $0.w && y >= $0.y && y < $0.y + $0.h }
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        if dc.editing, let (x, y) = dc.cellAt(p), widgetAt(x, y) == nil {
            let m = NSMenu()
            for k in WidgetKind.allCases {
                let it = NSMenuItem(title: "\(k.title) 추가", action: #selector(addWidget(_:)), keyEquivalent: "")
                it.target = self
                it.representedObject = [k.rawValue, x, y] as [Any]
                m.addItem(it)
            }
            NSMenu.popUpContextMenu(m, with: event, for: self)
        }
    }

    override func rightMouseDown(with event: NSEvent) {
        NSMenu.popUpContextMenu(dc.mainMenu(), with: event, for: self)
    }

    @objc func addWidget(_ sender: NSMenuItem) {
        guard let a = sender.representedObject as? [Any], let k = WidgetKind(rawValue: a[0] as! String) else { return }
        let x = a[1] as! Int, y = a[2] as! Int
        var w = Widget(kind: k, x: x, y: y, label: k.title)
        switch k {
        case .button: w.icon = "sf:star.fill"; w.color = "#3B5BDB"
        case .toggle: w.icon = "sf:power"; w.color = "#2B8A3E"
        case .knob: w.icon = "sf:dial.medium"; w.color = "#E8590C"
        case .slider:
            w.color = "#E8590C"
            if x + 1 < Store.shared.page.cols && widgetAt(x + 1, y) == nil { w.w = 2 }
        case .timer: w.color = "#0B7285"
        }
        Store.shared.mutate { $0.pages[$0.currentPage].widgets.append(w) }
        AppDelegate.shared.openInspector(w.id)
    }

    override func scrollWheel(with event: NSEvent) {
        // ctrl + 휠: 덱 크기 조절
        if event.modifierFlags.contains(.control) {
            let d = event.scrollingDeltaY * (event.hasPreciseScrollingDeltas ? 0.25 : 3)
            let ns = max(40, min(180, dc.s.cellSize + Double(d)))
            if abs(ns - dc.s.cellSize) >= 1 { Store.shared.mutate { $0.settings.cellSize = ns.rounded() } }
            return
        }
        super.scrollWheel(with: event)
    }
}

// MARK: - 이동/크기 조절 미리보기

final class GhostView: NSView {
    var ok = true { didSet { needsDisplay = true } }
    override func draw(_ dirtyRect: NSRect) {
        let c: NSColor = ok ? .systemGreen : .systemRed
        c.withAlphaComponent(0.25).setFill()
        c.setStroke()
        let p = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 10, yRadius: 10)
        p.lineWidth = 2
        p.fill(); p.stroke()
    }
}

// MARK: - 상단 베젤

final class BezelView: NSView {
    override var isFlipped: Bool { true }
    private let title = NSTextField(labelWithString: "")
    var dc: DeckController { DeckController.shared }

    func build() {
        subviews.forEach { $0.removeFromSuperview() }
        let store = Store.shared
        let page = store.page
        let n = store.profile.pages.count
        title.stringValue = n > 1 ? "\(page.name)  \(store.profile.currentPage + 1)/\(n)" : page.name
        if dc.editing { title.stringValue = "✎ 편집 중 · " + title.stringValue }
        title.font = .systemFont(ofSize: 11, weight: .semibold)
        title.textColor = dc.editing ? .systemYellow : NSColor.white.withAlphaComponent(0.7)
        title.lineBreakMode = .byTruncatingTail
        title.frame = NSRect(x: 12, y: 8, width: max(40, bounds.width - 150), height: 16)
        addSubview(title)

        // (심볼, 설명, 동작, 강조, 우선순위: 덱이 좁으면 숫자가 큰 것부터 숨김)
        var buttons: [(String, String, Selector, Bool, Int)] = [
            ("chevron.left", "이전 페이지", #selector(prev), false, 3),
            ("chevron.right", "다음 페이지", #selector(next), false, 3),
            (dc.editing ? "checkmark.circle.fill" : "pencil", dc.editing ? "편집 끝" : "편집", #selector(edit), dc.editing, 0),
            (dc.s.alwaysOnTop ? "pin.fill" : "pin", "항상 위", #selector(pin), dc.s.alwaysOnTop, 2),
            ("gearshape.fill", "설정", #selector(settings), false, 0),
            ("line.3.horizontal", "메뉴 (배치 바꾸기 등)", #selector(menu(_:)), false, 1),
            ("minus", "숨기기", #selector(hideDeck), false, 0),
        ]
        if n < 2 { buttons.removeAll { $0.2 == #selector(prev) || $0.2 == #selector(next) } }
        // 버튼이 들어갈 자리가 모자라면 덜 중요한 것부터 뺌 (오른쪽 클릭 메뉴로 모두 쓸 수 있음)
        var level = 3
        while CGFloat(buttons.count) * 22 + 16 > bounds.width && level > 0 {
            buttons.removeAll { $0.4 >= level }
            level -= 1
        }
        title.isHidden = bounds.width - CGFloat(buttons.count) * 22 - 16 < 44
        var x = bounds.width - 8
        for (sym, tip, sel, active, _) in buttons.reversed() {
            x -= 22
            let b = NSButton(frame: NSRect(x: x, y: 5, width: 20, height: 20))
            b.isBordered = false
            b.bezelStyle = .regularSquare
            b.image = NSImage(systemSymbolName: sym, accessibilityDescription: tip)?
                .withSymbolConfiguration(.init(pointSize: 11, weight: .semibold))
            b.contentTintColor = active ? .systemYellow : NSColor.white.withAlphaComponent(0.65)
            b.toolTip = tip
            b.target = self
            b.action = sel
            addSubview(b)
        }
        title.frame.size.width = max(30, x - 16)
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { AppDelegate.shared.toggleEdit(); return }
        if !dc.s.locked { window?.performDrag(with: event) }
    }

    override func rightMouseDown(with event: NSEvent) {
        NSMenu.popUpContextMenu(dc.mainMenu(), with: event, for: self)
    }

    override func scrollWheel(with event: NSEvent) {
        let d = abs(event.scrollingDeltaX) > abs(event.scrollingDeltaY) ? event.scrollingDeltaX : event.scrollingDeltaY
        if event.phase == .ended || event.momentumPhase != [] { return }
        if abs(d) > 3 { dc.switchPage(d > 0 ? "prev" : "next") }
    }

    @objc func prev() { dc.switchPage("prev") }
    @objc func next() { dc.switchPage("next") }
    @objc func edit() { AppDelegate.shared.toggleEdit() }
    @objc func pin() { dc.setAlwaysOnTop(!dc.s.alwaysOnTop) }
    @objc func settings() { AppDelegate.shared.openSettings(tab: 0) }
    @objc func hideDeck() { dc.hide(); Toast.show("메뉴 막대 아이콘 또는 \(Keys.pretty(dc.s.hotkey))로 다시 열 수 있어요", atScreen: true) }
    @objc func menu(_ sender: NSButton) {
        dc.mainMenu().popUp(positioning: nil, at: NSPoint(x: 0, y: sender.bounds.height + 4), in: sender)
    }
}

// MARK: - 토스트 알림

enum Toast {
    private static var panel: NSPanel?
    private static var hideWork: DispatchWorkItem?

    static func show(_ text: String, atScreen: Bool = false) {
        let p = panel ?? {
            let p = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            p.isOpaque = false; p.backgroundColor = .clear; p.level = .statusBar; p.hasShadow = true
            p.ignoresMouseEvents = true
            p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            panel = p
            return p
        }()
        let label = NSTextField(wrappingLabelWithString: text)
        label.font = .systemFont(ofSize: 13, weight: .medium)
        label.textColor = .white
        label.alignment = .center
        label.preferredMaxLayoutWidth = 320
        let size = label.fittingSize
        let w = min(360, size.width + 32), h = size.height + 20
        let bg = NSVisualEffectView(frame: NSRect(x: 0, y: 0, width: w, height: h))
        bg.material = .hudWindow; bg.state = .active; bg.blendingMode = .behindWindow
        bg.wantsLayer = true; bg.layer?.cornerRadius = 10; bg.layer?.masksToBounds = true
        label.frame = NSRect(x: 16, y: 10, width: w - 32, height: size.height)
        bg.addSubview(label)
        p.contentView = bg

        let deck = DeckController.shared.panel
        var origin: NSPoint
        if !atScreen, deck.isVisible {
            origin = NSPoint(x: deck.frame.midX - w / 2, y: deck.frame.minY - h - 8)
            if let vf = deck.screen?.visibleFrame, origin.y < vf.minY { origin.y = deck.frame.maxY + 8 }
        } else {
            let vf = NSScreen.main?.visibleFrame ?? .zero
            origin = NSPoint(x: vf.midX - w / 2, y: vf.maxY - h - 40)
        }
        p.setFrame(NSRect(origin: origin, size: NSSize(width: w, height: h)), display: true)
        p.alphaValue = 1
        p.orderFrontRegardless()
        hideWork?.cancel()
        let work = DispatchWorkItem {
            NSAnimationContext.runAnimationGroup({ $0.duration = 0.3; p.animator().alphaValue = 0 },
                                                 completionHandler: { p.orderOut(nil) })
        }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + max(1.8, Double(text.count) * 0.06), execute: work)
    }
}
