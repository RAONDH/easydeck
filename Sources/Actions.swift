import AppKit
import ApplicationServices

// MARK: - 동작 스크립트
//
// 한 줄에 동작 하나, 위에서부터 순서대로 실행합니다. '#'으로 시작하면 주석.
//
//   key cmd+shift+4          단축키 입력
//   type 안녕하세요\n반가워요    글자 입력 (\n = 줄바꿈)
//   paste 긴 문장             클립보드로 붙여넣기 (한글 입력기 상태와 무관, 원래 클립보드 복원)
//   open https://...         웹사이트 / 파일·폴더 경로 / 앱 이름 / 번들 ID
//   shell say hello          셸 명령을 창 없이 실행 (zsh, 끝날 때까지 기다림)
//   applescript ...          애플스크립트 한 줄 실행
//   shortcut 이름             단축어(Shortcuts) 앱의 단축어 실행
//   volume up|down|mute|50|+5|-5
//   media play|next|prev
//   brightness up|down
//   darkmode toggle|on|off
//   delay 0.5                초 단위 대기
//   page 이름|next|prev|2     덱 페이지 전환
//   layout row|column|flip|undo  덱 배치 바꾸기 (가로 한 줄 / 세로 한 줄 / 가로↔세로 / 되돌리기)
//   notify 메시지             덱 위에 알림 표시
//   sound Glass              시스템 사운드 재생
//   deck hide|show|toggle    덱 숨기기/보이기
//
// {value} 는 노브/슬라이더의 현재 값(0~100)으로 바뀝니다.

enum ActionCatalog {
    static let templates: [(String, String)] = [
        ("단축키 입력", "key cmd+c"),
        ("글자 입력", "type 안녕하세요"),
        ("붙여넣기로 입력", "paste 긴 문장도 한 번에"),
        ("웹사이트 열기", "open https://www.google.com"),
        ("앱 열기", "open Safari"),
        ("폴더 열기", "open ~/Downloads"),
        ("셸 명령", "shell open -a Calculator"),
        ("애플스크립트", "applescript display dialog \"hello\""),
        ("단축어 실행", "shortcut 단축어이름"),
        ("음량 올리기", "volume +5"),
        ("음량 내리기", "volume -5"),
        ("음소거", "volume mute"),
        ("재생/일시정지", "media play"),
        ("다음 곡", "media next"),
        ("이전 곡", "media prev"),
        ("밝기 올리기", "brightness up"),
        ("밝기 내리기", "brightness down"),
        ("다크 모드 전환", "darkmode toggle"),
        ("대기", "delay 0.5"),
        ("다음 페이지", "page next"),
        ("가로↔세로 배치 바꾸기", "layout flip"),
        ("알림", "notify 완료!"),
        ("소리", "sound Glass"),
    ]
}

enum ActionRunner {
    /// 백그라운드에서 순서대로 실행
    static func run(_ script: String?, vars: [String: String] = [:]) {
        guard let script = script, !script.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        DispatchQueue.global(qos: .userInitiated).async {
            for raw in script.components(separatedBy: .newlines) {
                var line = raw.trimmingCharacters(in: .whitespaces)
                if line.isEmpty || line.hasPrefix("#") { continue }
                for (k, v) in vars { line = line.replacingOccurrences(of: "{\(k)}", with: v) }
                let (cmd, arg) = split(line)
                do {
                    try exec(cmd.lowercased(), arg)
                } catch {
                    Log.write("동작 실패 '\(line)': \(error)")
                    DispatchQueue.main.async { Toast.show("동작 실패: \(line)") }
                }
            }
        }
    }

    enum Risk { case safe, input, runsCode }

    /// 가져온 프로필 검사용 분류. 여기 안전 목록에 없는 동작은 모두 위험으로 봄 (exec()와 함께 관리)
    static func risk(of cmd: String) -> Risk {
        switch cmd.lowercased() {
        case "volume", "vol", "media", "brightness", "bright", "darkmode", "dark", "delay", "wait", "sleep",
             "page", "layout", "notify", "toast", "sound", "deck":
            return .safe
        case "key", "hotkey", "type", "text", "paste", "open":
            return .input
        default:
            return .runsCode   // shell, applescript, shortcut, 알 수 없는 동작
        }
    }

    static func split(_ line: String) -> (String, String) {
        if let r = line.firstIndex(where: { $0 == " " || $0 == "\t" }) {
            return (String(line[..<r]), String(line[r...]).trimmingCharacters(in: .whitespaces))
        }
        return (line, "")
    }

    struct Failure: Error, CustomStringConvertible { let description: String }

    static func exec(_ cmd: String, _ arg: String) throws {
        switch cmd {
        case "key", "hotkey":
            try needAccessibility()
            for combo in arg.split(separator: " ") {
                guard let c = Keys.parse(String(combo)) else { throw Failure(description: "알 수 없는 키: \(combo)") }
                Input.key(c.code, c.flags)
                usleep(15_000)
            }
        case "type", "text":
            try needAccessibility()
            Input.type(unescape(arg))
        case "paste":
            try needAccessibility()
            Input.paste(unescape(arg))
        case "open":
            try open(arg)
        case "shell", "sh", "run":
            shell(arg)
        case "applescript", "osascript":
            try appleScript(arg)
        case "shortcut", "shortcuts":
            shell("/usr/bin/shortcuts run \(quote(arg))")
        case "volume", "vol":
            try volume(arg)
        case "media":
            switch arg.lowercased() {
            case "play", "pause", "playpause", "toggle": Input.aux(16)
            case "next", "forward": Input.aux(17)
            case "prev", "previous", "back": Input.aux(18)
            default: throw Failure(description: "media play|next|prev")
            }
        case "brightness", "bright":
            switch arg.lowercased() {
            case "up", "+": Input.aux(2)
            case "down", "-": Input.aux(3)
            default: throw Failure(description: "brightness up|down")
            }
        case "darkmode", "dark":
            let v: String
            switch arg.lowercased() {
            case "on": v = "true"
            case "off": v = "false"
            default: v = "not dark mode"
            }
            try appleScript("tell application \"System Events\" to tell appearance preferences to set dark mode to \(v)")
        case "delay", "wait", "sleep":
            let s = Double(arg) ?? 0.5
            usleep(useconds_t(max(0, min(s, 600)) * 1_000_000))
        case "layout":
            DispatchQueue.main.async { DeckController.shared.changeLayout(arg) }
        case "page":
            DispatchQueue.main.async { DeckController.shared.switchPage(arg) }
        case "notify", "toast":
            DispatchQueue.main.async { Toast.show(unescape(arg)) }
        case "sound":
            DispatchQueue.main.async { NSSound(named: NSSound.Name(arg.isEmpty ? "Glass" : arg))?.play() }
        case "deck":
            DispatchQueue.main.async {
                switch arg.lowercased() {
                case "hide": DeckController.shared.hide()
                case "show": DeckController.shared.show()
                default: DeckController.shared.toggleVisible()
                }
            }
        default:
            throw Failure(description: "알 수 없는 동작 '\(cmd)'")
        }
    }

    static func unescape(_ s: String) -> String {
        s.replacingOccurrences(of: "\\n", with: "\n").replacingOccurrences(of: "\\t", with: "\t")
    }

    static func quote(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    static func needAccessibility() throws {
        if !AXIsProcessTrusted() {
            DispatchQueue.main.async { Permissions.promptAccessibility() }
            throw Failure(description: "손쉬운 사용 권한 필요")
        }
    }

    static func open(_ target: String) throws {
        let t = (target as NSString).expandingTildeInPath
        if t.contains("://"), let url = URL(string: t) {
            NSWorkspace.shared.open(url); return
        }
        if FileManager.default.fileExists(atPath: t) {
            NSWorkspace.shared.open(URL(fileURLWithPath: t)); return
        }
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: t) {
            NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration(), completionHandler: nil); return
        }
        if t.contains("."), !t.contains(" "), let url = URL(string: "https://" + t) {
            NSWorkspace.shared.open(url); return
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-a", t]
        try p.run(); p.waitUntilExit()
        if p.terminationStatus != 0 { throw Failure(description: "열 수 없음: \(target)") }
    }

    static func shell(_ cmd: String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/zsh")
        p.arguments = ["-lc", cmd]
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = FileHandle.nullDevice
        let err = Pipe()
        p.standardError = err
        do {
            try p.run()
            p.waitUntilExit()
            if p.terminationStatus != 0 {
                let msg = String(data: err.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
                Log.write("shell 종료코드 \(p.terminationStatus): \(cmd)\n\(msg)")
            }
        } catch {
            Log.write("shell 실행 실패: \(error)")
        }
    }

    @discardableResult
    static func appleScript(_ src: String) throws -> String? {
        var result: String?
        var failure: String?
        let work = {
            var err: NSDictionary?
            let out = NSAppleScript(source: src)?.executeAndReturnError(&err)
            if let e = err { failure = (e[NSAppleScript.errorMessage] as? String) ?? "\(e)" }
            result = out?.stringValue
        }
        if Thread.isMainThread { work() } else { DispatchQueue.main.sync(execute: work) }
        if let f = failure { throw Failure(description: f) }
        return result
    }

    // MARK: 음량

    static func currentVolume() -> Double? {
        guard let s = try? appleScript("output volume of (get volume settings)"), let v = Double(s) else { return nil }
        return v
    }

    static func setVolume(_ v: Double) {
        _ = try? appleScript("set volume output volume \(Int(max(0, min(100, v))))")
    }

    static func volume(_ arg: String) throws {
        let a = arg.lowercased()
        switch a {
        case "up": Input.aux(0)
        case "down": Input.aux(1)
        case "mute", "toggle": Input.aux(7)
        default:
            if a.hasPrefix("+") || a.hasPrefix("-"), let d = Double(a) {
                let cur = currentVolume() ?? 50
                setVolume(cur + d)
            } else if let v = Double(a) {
                setVolume(v)
            } else {
                throw Failure(description: "volume up|down|mute|숫자|+5|-5")
            }
        }
    }
}

// MARK: - 키보드/미디어 입력 보내기

enum Input {
    static let src = CGEventSource(stateID: .hidSystemState)

    static func key(_ code: CGKeyCode, _ flags: CGEventFlags) {
        let down = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: true)
        let up = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: false)
        down?.flags = flags
        up?.flags = flags
        down?.post(tap: .cghidEventTap)
        usleep(8_000)
        up?.post(tap: .cghidEventTap)
    }

    /// 유니코드 문자열을 그대로 입력 (한글 가능)
    static func type(_ text: String) {
        for line in text.components(separatedBy: "\n").enumerated() {
            if line.offset > 0 { key(36, []); usleep(10_000) }
            let units = Array(line.element.utf16)
            var i = 0
            while i < units.count {
                let n = min(16, units.count - i)
                var chunk = Array(units[i..<(i + n)])
                let down = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: true)
                let up = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: false)
                down?.keyboardSetUnicodeString(stringLength: n, unicodeString: &chunk)
                up?.keyboardSetUnicodeString(stringLength: n, unicodeString: &chunk)
                down?.post(tap: .cghidEventTap)
                up?.post(tap: .cghidEventTap)
                usleep(6_000)
                i += n
            }
        }
    }

    /// 클립보드에 넣고 ⌘V 후 원래 클립보드 복원
    static func paste(_ text: String) {
        let pb = NSPasteboard.general
        var saved: [NSPasteboardItem] = []
        DispatchQueue.main.sync {
            saved = (pb.pasteboardItems ?? []).map { item in
                let copy = NSPasteboardItem()
                for t in item.types { if let d = item.data(forType: t) { copy.setData(d, forType: t) } }
                return copy
            }
            pb.clearContents()
            pb.setString(text, forType: .string)
        }
        key(9, .maskCommand)
        usleep(350_000)
        DispatchQueue.main.sync {
            pb.clearContents()
            if !saved.isEmpty { pb.writeObjects(saved) }
        }
    }

    /// 시스템 특수키 (NX_KEYTYPE_*): 0 볼륨+, 1 볼륨-, 2 밝기+, 3 밝기-, 7 음소거, 16 재생, 17 다음, 18 이전
    static func aux(_ key: Int) {
        func post(_ down: Bool) {
            let flags = NSEvent.ModifierFlags(rawValue: down ? 0xA00 : 0xB00)
            let data1 = (key << 16) | ((down ? 0xA : 0xB) << 8)
            let ev = NSEvent.otherEvent(with: .systemDefined, location: .zero, modifierFlags: flags,
                                        timestamp: 0, windowNumber: 0, context: nil,
                                        subtype: 8, data1: data1, data2: -1)
            ev?.cgEvent?.post(tap: .cghidEventTap)
        }
        post(true)
        post(false)
    }
}

// MARK: - 권한

enum Permissions {
    private static var prompted = false
    static func promptAccessibility() {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: !prompted] as CFDictionary
        prompted = true
        if !AXIsProcessTrustedWithOptions(opts) {
            Toast.show("시스템 설정 › 개인정보 보호 및 보안 › 손쉬운 사용에서\nEasyDeck을 허용해 주세요")
        }
    }

    static func openAccessibilitySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }
}
