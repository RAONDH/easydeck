import AppKit

// MARK: - 아이콘 그리기

enum IconCache {
    static var images: [String: NSImage] = [:]

    static func image(for icon: String) -> NSImage? {
        if let i = images[icon] { return i }
        var img: NSImage?
        if icon.hasPrefix("sf:") {
            img = NSImage(systemSymbolName: String(icon.dropFirst(3)), accessibilityDescription: nil)
        } else if icon.hasPrefix("/") || icon.hasPrefix("~") {
            img = NSImage(contentsOfFile: (icon as NSString).expandingTildeInPath)
        } else if icon.contains(".") {
            let p = Store.shared.iconsDir.appendingPathComponent(icon).path
            if FileManager.default.fileExists(atPath: p) { img = NSImage(contentsOfFile: p) }
        }
        if let i = img { images[icon] = i }
        return img
    }

    static func draw(_ icon: String, in r: NSRect, tint: NSColor) {
        guard !icon.isEmpty, r.width > 2, r.height > 2 else { return }
        if icon.hasPrefix("sf:"), let base = image(for: icon) {
            let cfg = NSImage.SymbolConfiguration(pointSize: r.height * 0.8, weight: .semibold)
                .applying(.init(paletteColors: [tint]))
            if let img = base.withSymbolConfiguration(cfg) { fit(img, r) }
            return
        }
        if let img = image(for: icon) { fit(img, r); return }
        // 글자/이모지
        let font = NSFont.systemFont(ofSize: r.height * 0.82, weight: .bold)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: tint]
        let s = icon as NSString
        var sz = s.size(withAttributes: attrs)
        var a = attrs
        if sz.width > r.width * 1.05 {
            let scaled = NSFont.systemFont(ofSize: font.pointSize * r.width / sz.width, weight: .bold)
            a[.font] = scaled
            sz = s.size(withAttributes: a)
        }
        s.draw(at: NSPoint(x: r.midX - sz.width / 2, y: r.midY - sz.height / 2), withAttributes: a)
    }

    static func fit(_ img: NSImage, _ r: NSRect) {
        let s = img.size
        guard s.width > 0, s.height > 0 else { return }
        let k = min(r.width / s.width, r.height / s.height)
        let w = s.width * k, h = s.height * k
        img.draw(in: NSRect(x: r.midX - w / 2, y: r.midY - h / 2, width: w, height: h),
                 from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
    }
}

// MARK: - 위젯 뷰

final class WidgetView: NSView {
    let widgetID: String
    override var isFlipped: Bool { true }

    private var pressed = false { didSet { needsDisplay = true } }
    private var longTimer: Timer?
    private var longFired = false
    private var pendingTap: DispatchWorkItem?
    private var downPoint = NSPoint.zero
    private var dragged = false
    private var dragAccum: CGFloat = 0
    private var wheelAccum: CGFloat = 0
    private var flash: CGFloat = 0
    private var lastSend = Date.distantPast
    private var trailing: DispatchWorkItem?

    // 편집 모드
    private enum EditDrag { case none, move, resize }
    private var editDrag: EditDrag = .none
    private var proposed: (x: Int, y: Int, w: Int, h: Int)?

    init(widgetID: String) {
        self.widgetID = widgetID
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError() }

    var dc: DeckController { DeckController.shared }
    var w: Widget? { Store.shared.widget(widgetID) }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    // MARK: 그리기

    override func draw(_ dirtyRect: NSRect) {
        guard let w = w else { return }
        let scale = CGFloat(dc.s.fontScale)
        let base = NSColor(hex: w.color)
        let fg = NSColor(hex: w.textColor)
        var fill = base
        if w.kind == .toggle && !w.isOn { fill = base.blended(withFraction: 0.65, of: NSColor(white: 0.16, alpha: 1)) ?? base }
        if pressed || flash > 0 { fill = fill.blended(withFraction: 0.25, of: .white) ?? fill }

        let r = bounds.insetBy(dx: 0.5, dy: 0.5)
        let bg = NSBezierPath(roundedRect: r, xRadius: 11, yRadius: 11)
        fill.setFill(); bg.fill()
        // 윗부분 살짝 밝게 (입체감)
        NSGraphicsContext.saveGraphicsState()
        bg.addClip()
        NSGradient(starting: NSColor.white.withAlphaComponent(0.10), ending: NSColor.black.withAlphaComponent(0.10))?
            .draw(in: r, angle: 90)
        NSGraphicsContext.restoreGraphicsState()

        let labelFont = NSFont.systemFont(ofSize: 11 * scale, weight: .semibold)
        let labelH: CGFloat = w.label.isEmpty ? 0 : labelFont.pointSize + 6

        switch w.kind {
        case .button, .toggle:
            let side = min(bounds.width, bounds.height - labelH) * 0.48 * scale
            let iconRect = NSRect(x: bounds.midX - side / 2, y: (bounds.height - labelH) / 2 - side / 2 + (labelH > 0 ? 4 : 0),
                                  width: side, height: side)
            IconCache.draw(w.icon, in: iconRect, tint: fg.withAlphaComponent(w.kind == .toggle && !w.isOn ? 0.55 : 1))
            if w.kind == .toggle {
                let dot = NSRect(x: bounds.maxX - 14, y: 7, width: 7, height: 7)
                (w.isOn ? NSColor.systemGreen : NSColor.white.withAlphaComponent(0.25)).setFill()
                NSBezierPath(ovalIn: dot).fill()
            }
        case .knob:
            drawKnob(w, labelH: labelH, fg: fg, scale: scale)
        case .slider:
            drawSlider(w, labelH: labelH, fg: fg, scale: scale)
        case .timer:
            drawTimer(w, labelH: labelH, fg: fg, scale: scale)
        }

        if !w.label.isEmpty {
            drawCentered(w.label, y: bounds.height - labelH - 2, font: labelFont, color: fg.withAlphaComponent(0.92))
        }

        if !w.hotkey.isEmpty && bounds.width >= 60 {
            let hk = Keys.pretty(w.hotkey) as NSString
            let a: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 8.5, weight: .medium),
                                                    .foregroundColor: fg.withAlphaComponent(0.55)]
            hk.draw(at: NSPoint(x: 6, y: 4), withAttributes: a)
        }

        if dc.editing {
            NSColor.systemYellow.withAlphaComponent(0.9).setStroke()
            let b = NSBezierPath(roundedRect: r.insetBy(dx: 1, dy: 1), xRadius: 10, yRadius: 10)
            b.lineWidth = 1.5
            b.setLineDash([5, 3], count: 2, phase: 0)
            b.stroke()
            // 크기 조절 손잡이
            let g = NSBezierPath()
            for i in 0..<3 {
                let o = CGFloat(i) * 4
                g.move(to: NSPoint(x: bounds.maxX - 4 - o, y: bounds.maxY - 3))
                g.line(to: NSPoint(x: bounds.maxX - 3, y: bounds.maxY - 4 - o))
            }
            g.lineWidth = 1.2
            g.stroke()
        }
    }

    private func drawCentered(_ s: String, y: CGFloat, font: NSFont, color: NSColor) {
        let p = NSMutableParagraphStyle()
        p.alignment = .center
        p.lineBreakMode = .byTruncatingTail
        let a: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: p]
        (s as NSString).draw(in: NSRect(x: 4, y: y, width: bounds.width - 8, height: font.pointSize + 5), withAttributes: a)
    }

    private func drawKnob(_ w: Widget, labelH: CGFloat, fg: NSColor, scale: CGFloat) {
        let side = min(bounds.width, bounds.height - labelH) * 0.70
        let c = NSPoint(x: bounds.midX, y: (bounds.height - labelH) / 2 + (labelH > 0 ? 3 : 0))
        let rad = side / 2
        let ring = NSBezierPath(ovalIn: NSRect(x: c.x - rad, y: c.y - rad, width: side, height: side))
        NSColor.black.withAlphaComponent(0.25).setFill(); ring.fill()
        fg.withAlphaComponent(0.25).setStroke(); ring.lineWidth = 3; ring.stroke()

        if w.infinite {
            let ang = dc.knobAngles[w.id] ?? -90
            for i in 0..<12 {
                let a = (CGFloat(i) * 30 + ang) * .pi / 180
                let p1 = NSPoint(x: c.x + cos(a) * (rad - 3), y: c.y + sin(a) * (rad - 3))
                let p2 = NSPoint(x: c.x + cos(a) * (rad - (i == 0 ? 10 : 6)), y: c.y + sin(a) * (rad - (i == 0 ? 10 : 6)))
                let l = NSBezierPath(); l.move(to: p1); l.line(to: p2)
                l.lineWidth = i == 0 ? 3 : 1.2
                fg.withAlphaComponent(i == 0 ? 1 : 0.4).setStroke(); l.stroke()
            }
            let isz = side * 0.36
            IconCache.draw(w.icon, in: NSRect(x: c.x - isz / 2, y: c.y - isz / 2, width: isz, height: isz), tint: fg)
        } else {
            // 135° ~ 405° (flipped 좌표계: 시계방향)
            let start: CGFloat = 135, sweep: CGFloat = 270
            let arc = NSBezierPath()
            arc.appendArc(withCenter: c, radius: rad - 2, startAngle: start, endAngle: start + sweep * CGFloat(w.value / 100))
            arc.lineWidth = 4; arc.lineCapStyle = .round
            fg.setStroke(); arc.stroke()
            let v = "\(Int(w.value.rounded()))" as NSString
            let a: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: side * 0.28, weight: .bold),
                                                    .foregroundColor: fg]
            let sz = v.size(withAttributes: a)
            v.draw(at: NSPoint(x: c.x - sz.width / 2, y: c.y - sz.height / 2), withAttributes: a)
        }
    }

    private func drawSlider(_ w: Widget, labelH: CGFloat, fg: NSColor, scale: CGFloat) {
        let t = CGFloat(w.value / 100)
        let area = NSRect(x: 0, y: 0, width: bounds.width, height: bounds.height - labelH)
        let track: NSRect
        if w.vertical {
            let tw: CGFloat = max(10, min(22, area.width * 0.3))
            track = NSRect(x: area.midX - tw / 2, y: 12, width: tw, height: area.height - 24)
        } else {
            let th: CGFloat = max(10, min(22, area.height * 0.3))
            track = NSRect(x: 14, y: area.midY - th / 2 + 2, width: area.width - 28, height: th)
        }
        NSColor.black.withAlphaComponent(0.3).setFill()
        NSBezierPath(roundedRect: track, xRadius: track.width.minDimension(track.height) / 2,
                     yRadius: track.width.minDimension(track.height) / 2).fill()
        var filled = track
        if w.vertical { filled.size.height = track.height * t; filled.origin.y = track.maxY - filled.height }
        else { filled.size.width = track.width * t }
        fg.withAlphaComponent(0.9).setFill()
        NSBezierPath(roundedRect: filled, xRadius: track.width.minDimension(track.height) / 2,
                     yRadius: track.width.minDimension(track.height) / 2).fill()

        let v = "\(Int(w.value.rounded()))" as NSString
        let a: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 10 * scale, weight: .bold),
                                                .foregroundColor: fg.withAlphaComponent(0.85)]
        let sz = v.size(withAttributes: a)
        if w.vertical { v.draw(at: NSPoint(x: area.midX - sz.width / 2, y: 0), withAttributes: a) }
        else { v.draw(at: NSPoint(x: area.maxX - sz.width - 8, y: 5), withAttributes: a) }
    }

    private func drawTimer(_ w: Widget, labelH: CGFloat, fg: NSColor, scale: CGFloat) {
        let t = dc.timers[w.id]
        let remaining = t?.remaining ?? w.duration
        let running = t?.running ?? false
        let frac = w.duration > 0 ? CGFloat(remaining / w.duration) : 0
        let secs = Int(ceil(remaining))
        let str = secs >= 3600 ? String(format: "%d:%02d:%02d", secs / 3600, secs / 60 % 60, secs % 60)
                               : String(format: "%02d:%02d", secs / 60, secs % 60)
        let area = NSRect(x: 0, y: 0, width: bounds.width, height: bounds.height - labelH)
        // 진행 막대
        let bar = NSRect(x: 10, y: area.maxY - 8, width: area.width - 20, height: 4)
        NSColor.black.withAlphaComponent(0.3).setFill(); NSBezierPath(roundedRect: bar, xRadius: 2, yRadius: 2).fill()
        var f = bar; f.size.width *= max(0, min(1, frac))
        (running ? fg : fg.withAlphaComponent(0.5)).setFill(); NSBezierPath(roundedRect: f, xRadius: 2, yRadius: 2).fill()

        let fs = min(area.height * 0.42, area.width / CGFloat(str.count) * 1.5)
        let a: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: fs, weight: .bold),
                                                .foregroundColor: running ? fg : fg.withAlphaComponent(0.7)]
        let s = str as NSString
        let sz = s.size(withAttributes: a)
        s.draw(at: NSPoint(x: area.midX - sz.width / 2, y: area.midY - sz.height / 2 - 2), withAttributes: a)
        if !running && remaining > 0 && remaining < w.duration {
            let p = "일시정지" as NSString
            let pa: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 8.5, weight: .medium),
                                                     .foregroundColor: fg.withAlphaComponent(0.6)]
            p.draw(at: NSPoint(x: bounds.maxX - p.size(withAttributes: pa).width - 6, y: 4), withAttributes: pa)
        }
    }

    // MARK: 마우스

    override func mouseDown(with event: NSEvent) {
        guard let w = w else { return }
        downPoint = convert(event.locationInWindow, from: nil)
        dragged = false
        dragAccum = 0
        if dc.editing {
            let corner = NSRect(x: bounds.maxX - 18, y: bounds.maxY - 18, width: 18, height: 18)
            editDrag = corner.contains(downPoint) ? .resize : .move
            proposed = nil
            return
        }
        pressed = true
        longFired = false
        if w.kind == .slider { setSliderValue(from: downPoint, w) }
        let hasLong = w.kind == .timer || !(w.actions["long"] ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        if hasLong && w.kind != .slider {
            longTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: false) { [weak self] _ in
                guard let self = self, !self.dragged else { return }
                self.longFired = true
                self.pressed = false
                if w.kind == .timer { self.dc.timerReset(self.widgetID) }
                else { ActionRunner.run(w.actions["long"]) }
                self.blink()
            }
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard let w = w else { return }
        let p = convert(event.locationInWindow, from: nil)
        if hypot(p.x - downPoint.x, p.y - downPoint.y) > 4 { dragged = true; longTimer?.invalidate() }
        if dc.editing { editDragged(p, w); return }
        switch w.kind {
        case .slider:
            setSliderValue(from: p, w)
        case .knob:
            // 위로 끌면 시계방향
            let d = -(event.deltaY) + event.deltaX * 0.5
            if w.infinite {
                dragAccum += d
                while dragAccum >= 12 { dragAccum -= 12; knobStep(+1, w) }
                while dragAccum <= -12 { dragAccum += 12; knobStep(-1, w) }
            } else {
                setValue((Store.shared.widget(widgetID)?.value ?? w.value) + Double(d) * 0.6)
            }
        default: break
        }
    }

    override func mouseUp(with event: NSEvent) {
        longTimer?.invalidate()
        pressed = false
        guard let w = w else { return }
        if dc.editing { editEnded(w); return }
        if longFired || (dragged && (w.kind == .knob || w.kind == .slider)) { return }
        if w.kind == .slider { return }
        if dragged { return }

        switch w.kind {
        case .toggle:
            dc.toggle(widgetID)
        case .timer:
            dc.timerStartPause(widgetID)
        default:
            let double = (w.actions["double"] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if double.isEmpty {
                ActionRunner.run(w.actions["tap"])
            } else if event.clickCount >= 2 {
                pendingTap?.cancel(); pendingTap = nil
                ActionRunner.run(double)
            } else {
                let tap = w.actions["tap"]
                let work = DispatchWorkItem { ActionRunner.run(tap) }
                pendingTap = work
                DispatchQueue.main.asyncAfter(deadline: .now() + min(0.3, NSEvent.doubleClickInterval), execute: work)
            }
        }
        blink()
    }

    override func scrollWheel(with event: NSEvent) {
        guard let w = w, !dc.editing, !event.modifierFlags.contains(.control) else { super.scrollWheel(with: event); return }
        let raw = abs(event.scrollingDeltaY) >= abs(event.scrollingDeltaX) ? event.scrollingDeltaY : -event.scrollingDeltaX
        let precise = event.hasPreciseScrollingDeltas
        // 자연스러운 스크롤 보정: 휠을 위로 = 증가
        let d = (event.isDirectionInvertedFromDevice ? -raw : raw) * (precise ? 1 : 10)
        switch w.kind {
        case .knob where w.infinite:
            wheelAccum += d
            while wheelAccum >= 10 { wheelAccum -= 10; knobStep(+1, w) }
            while wheelAccum <= -10 { wheelAccum += 10; knobStep(-1, w) }
        case .knob, .slider:
            setValue((Store.shared.widget(widgetID)?.value ?? w.value) + Double(d) * 0.2)
        case .timer:
            wheelAccum += d
            while wheelAccum >= 10 { wheelAccum -= 10; dc.timerAdjust(widgetID, by: 60) }
            while wheelAccum <= -10 { wheelAccum += 10; dc.timerAdjust(widgetID, by: -60) }
        default:
            super.scrollWheel(with: event)
        }
    }

    override func rightMouseDown(with event: NSEvent) {
        let m = NSMenu()
        func add(_ t: String, _ sel: Selector) {
            let i = NSMenuItem(title: t, action: sel, keyEquivalent: ""); i.target = self; m.addItem(i)
        }
        add("편집…", #selector(openInspector))
        add("테스트 실행", #selector(testRun))
        add("복제", #selector(duplicate))
        add("삭제", #selector(remove))
        m.addItem(.separator())
        let more = NSMenuItem(title: "덱 메뉴", action: nil, keyEquivalent: "")
        more.submenu = dc.mainMenu()
        m.addItem(more)
        NSMenu.popUpContextMenu(m, with: event, for: self)
    }

    @objc func openInspector() { AppDelegate.shared.openInspector(widgetID) }
    @objc func testRun() {
        guard let w = w else { return }
        switch w.kind {
        case .toggle: dc.toggle(widgetID)
        case .timer: dc.timerStartPause(widgetID)
        default: ActionRunner.run(w.actions["tap"])
        }
    }
    @objc func duplicate() {
        guard var c = w else { return }
        c.id = UUID().uuidString
        let page = Store.shared.page
        // 빈 자리 찾기
        outer: for y in 0..<page.rows {
            for x in 0..<page.cols where fits(x, y, c.w, c.h, page, ignoring: nil) {
                c.x = x; c.y = y
                Store.shared.mutate { $0.pages[$0.currentPage].widgets.append(c) }
                break outer
            }
        }
    }
    @objc func remove() {
        let id = widgetID
        Store.shared.mutate { p in p.pages[p.currentPage].widgets.removeAll { $0.id == id } }
    }

    // MARK: 값 조절

    private func blink() {
        flash = 1
        needsDisplay = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { [weak self] in self?.flash = 0; self?.needsDisplay = true }
    }

    private func knobStep(_ dir: Int, _ w: Widget) {
        dc.knobAngles[widgetID] = (dc.knobAngles[widgetID] ?? -90) + CGFloat(dir) * 15
        ActionRunner.run(w.actions[dir > 0 ? "cw" : "ccw"])
        needsDisplay = true
    }

    private func setSliderValue(from p: NSPoint, _ w: Widget) {
        let labelH: CGFloat = w.label.isEmpty ? 0 : 11 * CGFloat(dc.s.fontScale) + 6
        let v: CGFloat
        if w.vertical {
            let top: CGFloat = 12, bottom = bounds.height - labelH - 12
            v = 1 - (p.y - top) / max(1, bottom - top)
        } else {
            v = (p.x - 14) / max(1, bounds.width - 28)
        }
        setValue(Double(max(0, min(1, v))) * 100)
    }

    private func setValue(_ raw: Double) {
        let v = max(0, min(100, raw))
        guard let cur = Store.shared.widget(widgetID), abs(cur.value - v) > 0.01 else { return }
        Store.shared.updateWidget(widgetID, silent: true) { $0.value = v }
        needsDisplay = true
        // 너무 자주 실행하지 않도록 (최대 초당 ~15회 + 마지막 값 보장)
        trailing?.cancel()
        let send = { [weak self] in
            guard let self = self, let w = Store.shared.widget(self.widgetID) else { return }
            self.lastSend = Date()
            let val = w.value
            if w.bind == "volume" { DispatchQueue.global().async { ActionRunner.setVolume(val) } }
            ActionRunner.run(w.actions["change"], vars: ["value": "\(Int(val.rounded()))"])
        }
        if Date().timeIntervalSince(lastSend) > 0.07 {
            send()
        } else {
            let work = DispatchWorkItem(block: send)
            trailing = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.08, execute: work)
        }
    }

    // MARK: 편집 모드 드래그

    private func fits(_ x: Int, _ y: Int, _ ww: Int, _ hh: Int, _ page: Page, ignoring: String?) -> Bool {
        if x < 0 || y < 0 || x + ww > page.cols || y + hh > page.rows { return false }
        for o in page.widgets where o.id != ignoring {
            if x < o.x + o.w && o.x < x + ww && y < o.y + o.h && o.y < y + hh { return false }
        }
        return true
    }

    /// 겹친 위젯이 하나뿐이고 서로 자리를 바꿀 수 있으면 그 위젯을 돌려줌
    private func swapPartner(_ r: (x: Int, y: Int, w: Int, h: Int), _ w: Widget) -> Widget? {
        let page = Store.shared.page
        let hits = page.widgets.filter { o in
            o.id != w.id && r.x < o.x + o.w && o.x < r.x + r.w && r.y < o.y + o.h && o.y < r.y + r.h
        }
        guard hits.count == 1, let o = hits.first else { return nil }
        // 끌던 위젯은 상대 자리로, 상대는 끌던 위젯의 원래 자리로
        let ignore: Set<String> = [w.id, o.id]
        let a = (x: o.x, y: o.y), b = (x: w.x, y: w.y)
        guard page.fits(a.x, a.y, w.w, w.h, ignoring: ignore), page.fits(b.x, b.y, o.w, o.h, ignoring: ignore) else { return nil }
        let overlap = a.x < b.x + o.w && b.x < a.x + w.w && a.y < b.y + o.h && b.y < a.y + w.h
        return overlap ? nil : o
    }

    private func editDragged(_ p: NSPoint, _ w: Widget) {
        guard editDrag != .none, dragged else { return }
        let step = CGFloat(dc.s.cellSize + dc.s.gap)
        let dx = Int(((p.x - downPoint.x) / step).rounded())
        let dy = Int(((p.y - downPoint.y) / step).rounded())
        var r = (x: w.x, y: w.y, w: w.w, h: w.h)
        if editDrag == .move { r.x += dx; r.y += dy } else { r.w = max(1, w.w + dx); r.h = max(1, w.h + dy) }
        proposed = r
        guard let content = superview as? DeckContentView else { return }
        var ghostRect = dc.cellRect(r.x, r.y, r.w, r.h)
        if fits(r.x, r.y, r.w, r.h, Store.shared.page, ignoring: w.id) {
            content.ghost.mode = .ok
        } else if editDrag == .move, let o = swapPartner(r, w) {
            content.ghost.mode = .swap          // 파란색: 놓으면 자리 바꾸기
            ghostRect = dc.cellRect(o.x, o.y, w.w, w.h)
        } else {
            content.ghost.mode = .bad
        }
        content.ghost.frame = ghostRect
        content.ghost.isHidden = false
        content.addSubview(content.ghost, positioned: .above, relativeTo: nil)
        // 끌고 있는 위젯이 마우스를 따라 움직이게
        if editDrag == .move {
            let home = dc.cellRect(w.x, w.y, w.w, w.h)
            frame.origin = NSPoint(x: home.minX + (p.x - downPoint.x), y: home.minY + (p.y - downPoint.y))
            alphaValue = 0.85
            content.addSubview(self, positioned: .above, relativeTo: nil)
            content.addSubview(content.ghost, positioned: .below, relativeTo: self)
        }
    }

    private func editEnded(_ w: Widget) {
        (superview as? DeckContentView)?.ghost.isHidden = true
        alphaValue = 1
        let home = dc.cellRect(w.x, w.y, w.w, w.h)
        defer { editDrag = .none; proposed = nil }
        if !dragged { AppDelegate.shared.openInspector(widgetID); return }
        guard let r = proposed else { frame = home; return }
        if fits(r.x, r.y, r.w, r.h, Store.shared.page, ignoring: w.id) {
            Store.shared.updateWidget(widgetID) { $0.x = r.x; $0.y = r.y; $0.w = r.w; $0.h = r.h }
        } else if editDrag == .move, let o = swapPartner(r, w) {
            let (wx, wy, ox, oy) = (w.x, w.y, o.x, o.y)
            Store.shared.mutate { p in
                guard let i = p.pages[p.currentPage].widgets.firstIndex(where: { $0.id == w.id }),
                      let j = p.pages[p.currentPage].widgets.firstIndex(where: { $0.id == o.id }) else { return }
                p.pages[p.currentPage].widgets[i].x = ox; p.pages[p.currentPage].widgets[i].y = oy
                p.pages[p.currentPage].widgets[j].x = wx; p.pages[p.currentPage].widgets[j].y = wy
            }
        } else {
            NSAnimationContext.runAnimationGroup { $0.duration = 0.15; animator().frame = home }
        }
    }
}

private extension CGFloat {
    func minDimension(_ o: CGFloat) -> CGFloat { Swift.min(self, o) }
}
