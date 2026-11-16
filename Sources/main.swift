import AppKit
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    static let shared = AppDelegate()

    private var statusItem: NSStatusItem!
    private var inspectorWindow: NSWindow?
    private var settingsWindow: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        Log.write("EasyDeck 시작")
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "square.grid.3x2.fill", accessibilityDescription: "EasyDeck")
        statusItem.button?.target = self
        statusItem.button?.action = #selector(statusClicked(_:))
        statusItem.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])

        if ProcessInfo.processInfo.environment["EASYDECK_SELFTEST"] != nil {
            DispatchQueue.main.async { SelfTest.run() }
            return
        }
        DeckController.shared.start()

        // open EasyDeck.app --args --enable-login-item : 로그인 시 자동 실행 등록
        if CommandLine.arguments.contains("--enable-login-item") && !LoginItem.enabled {
            LoginItem.set(true)
            Log.write("로그인 항목 등록: \(LoginItem.enabled)")
        }

        if !AXIsProcessTrusted() {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { Permissions.promptAccessibility() }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        DeckController.shared.show(); return true
    }

    func applicationWillTerminate(_ notification: Notification) { Store.shared.saveNow() }

    @objc private func statusClicked(_ sender: NSStatusBarButton) {
        let menu = DeckController.shared.mainMenu()
        let toggle = NSMenuItem(title: DeckController.shared.panel.isVisible ? "덱 숨기기" : "덱 보이기",
                                action: #selector(toggleDeck), keyEquivalent: "")
        toggle.target = self
        menu.insertItem(toggle, at: 0)
        menu.insertItem(.separator(), at: 1)
        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        statusItem.menu = nil
    }

    // MARK: 메뉴 동작

    @objc func toggleDeck() { DeckController.shared.toggleVisible() }
    @objc func hideDeck() { DeckController.shared.hide() }
    @objc func toggleEdit() {
        let dc = DeckController.shared
        dc.setEditing(!dc.editing)
        if dc.editing { Toast.show("편집 모드: 빈 칸(+) 클릭=추가 · 드래그=이동 · 모서리=크기 · 클릭=편집") }
    }
    @objc func layoutMenu(_ sender: NSMenuItem) {
        if let a = sender.representedObject as? String { DeckController.shared.changeLayout(a) }
    }
    @objc func toggleTop() { DeckController.shared.setAlwaysOnTop(!Store.shared.settings.alwaysOnTop) }
    @objc func toggleLock() { Store.shared.mutate { $0.settings.locked.toggle() } }
    @objc func pickPage(_ sender: NSMenuItem) { DeckController.shared.goToPage(sender.tag, manual: true) }
    @objc func editJSON() {
        Store.shared.saveNow()
        NSWorkspace.shared.open([Store.shared.fileURL],
                                withApplicationAt: URL(fileURLWithPath: "/System/Applications/TextEdit.app"),
                                configuration: NSWorkspace.OpenConfiguration(), completionHandler: nil)
    }
    @objc func openFolder() { NSWorkspace.shared.activateFileViewerSelecting([Store.shared.fileURL]) }
    @objc func checkPermission() {
        if AXIsProcessTrusted() { Toast.show("✅ 손쉬운 사용 권한이 허용되어 있습니다") }
        else { Permissions.promptAccessibility(); Permissions.openAccessibilitySettings() }
    }
    @objc func showHelp() { openSettings(tab: 2) }
    @objc func quit() { NSApp.terminate(nil) }

    // MARK: 창

    func openInspector(_ id: String) {
        guard let w = Store.shared.widget(id) else { return }
        inspectorWindow?.close()
        let model = InspectorModel(w)
        let view = InspectorView(m: model) { [weak self] in self?.inspectorWindow?.close() }
        let win = makeWindow(title: "위젯 편집 – \(w.kind.title)", view: view)
        inspectorWindow = win
        present(win)
    }

    @objc func openSettingsMenu() { openSettings(tab: 0) }

    func openSettings(tab: Int) {
        if let w = settingsWindow, w.isVisible { present(w); return }
        let model = SettingsModel()
        let view = SettingsView(m: model) { [weak self] in self?.settingsWindow?.close() }
        let win = makeWindow(title: "EasyDeck 설정", view: view)
        settingsWindow = win
        present(win)
    }

    private func makeWindow<V: View>(title: String, view: V) -> NSWindow {
        let win = NSWindow(contentViewController: NSHostingController(rootView: view))
        win.title = title
        win.styleMask = [.titled, .closable, .miniaturizable]
        win.isReleasedWhenClosed = false
        win.level = .floating
        return win
    }

    private func present(_ win: NSWindow) {
        // 덱 왼쪽(공간이 없으면 오른쪽)에 띄움
        let deck = DeckController.shared.panel.frame
        let size = win.frame.size
        let vf = DeckController.shared.panel.screen?.visibleFrame ?? NSScreen.main!.visibleFrame
        var x = deck.minX - size.width - 12
        if x < vf.minX { x = min(deck.maxX + 12, vf.maxX - size.width) }
        let y = max(vf.minY, min(deck.maxY - size.height, vf.maxY - size.height))
        win.setFrameOrigin(NSPoint(x: x, y: y))
        NSApp.activate(ignoringOtherApps: true)
        win.makeKeyAndOrderFront(nil)
    }
}

let app = NSApplication.shared
app.delegate = AppDelegate.shared
app.setActivationPolicy(.accessory)
app.run()
