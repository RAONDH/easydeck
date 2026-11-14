import AppKit
import Carbon

// MARK: - 키 이름 ↔ 키코드 (US 배열 기준 물리 키)

enum Keys {
    static let codes: [String: CGKeyCode] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9,
        "b": 11, "q": 12, "w": 13, "e": 14, "r": 15, "y": 16, "t": 17,
        "1": 18, "2": 19, "3": 20, "4": 21, "6": 22, "5": 23, "=": 24, "9": 25, "7": 26,
        "-": 27, "8": 28, "0": 29, "]": 30, "o": 31, "u": 32, "[": 33, "i": 34, "p": 35,
        "return": 36, "l": 37, "j": 38, "'": 39, "k": 40, ";": 41, "\\": 42, ",": 43,
        "/": 44, "n": 45, "m": 46, ".": 47, "tab": 48, "space": 49, "`": 50,
        "delete": 51, "esc": 53, "capslock": 57, "enter": 76,
        "f1": 122, "f2": 120, "f3": 99, "f4": 118, "f5": 96, "f6": 97, "f7": 98, "f8": 100,
        "f9": 101, "f10": 109, "f11": 103, "f12": 111, "f13": 105, "f14": 107, "f15": 113,
        "f16": 106, "f17": 64, "f18": 79, "f19": 80, "f20": 90,
        "home": 115, "pageup": 116, "forwarddelete": 117, "end": 119, "pagedown": 121,
        "left": 123, "right": 124, "down": 125, "up": 126,
    ]
    static let aliases: [String: String] = [
        "escape": "esc", "backspace": "delete", "del": "forwarddelete", "spacebar": "space",
        "plus": "=", "minus": "-", "comma": ",", "period": ".", "slash": "/", "←": "left",
        "→": "right", "↑": "up", "↓": "down", "⏎": "return", "↩": "return",
    ]
    static let names: [CGKeyCode: String] = {
        var m: [CGKeyCode: String] = [:]
        for (k, v) in codes { m[v] = k }
        return m
    }()

    struct Combo {
        var code: CGKeyCode
        var flags: CGEventFlags
    }

    /// "cmd+shift+4" → Combo
    static func parse(_ s: String) -> Combo? {
        var flags: CGEventFlags = []
        var key: CGKeyCode?
        var parts = s.lowercased().split(separator: "+", omittingEmptySubsequences: false).map {
            $0.trimmingCharacters(in: .whitespaces)
        }
        // "cmd++" 처럼 + 키 자체를 쓴 경우
        if s.hasSuffix("++") { parts = Array(parts.dropLast(2)) + ["="] }
        for p in parts where !p.isEmpty {
            switch p {
            case "cmd", "command", "⌘", "meta", "win": flags.insert(.maskCommand)
            case "shift", "⇧": flags.insert(.maskShift)
            case "opt", "option", "alt", "⌥": flags.insert(.maskAlternate)
            case "ctrl", "control", "⌃": flags.insert(.maskControl)
            case "fn": flags.insert(.maskSecondaryFn)
            default:
                let name = aliases[p] ?? p
                if let c = codes[name] { key = c } else { return nil }
            }
        }
        guard let k = key else { return nil }
        return Combo(code: k, flags: flags)
    }

    /// NSEvent → "cmd+shift+4"
    static func string(from e: NSEvent) -> String? {
        guard let name = names[CGKeyCode(e.keyCode)] else { return nil }
        var parts: [String] = []
        let f = e.modifierFlags
        if f.contains(.control) { parts.append("ctrl") }
        if f.contains(.option) { parts.append("opt") }
        if f.contains(.shift) { parts.append("shift") }
        if f.contains(.command) { parts.append("cmd") }
        parts.append(name)
        return parts.joined(separator: "+")
    }

    /// 표시용: "⌃⌥⇧⌘4"
    static func pretty(_ s: String) -> String {
        guard !s.isEmpty else { return "" }
        let parts = s.lowercased().split(separator: "+").map(String.init)
        var out = ""
        for p in parts {
            switch p {
            case "ctrl", "control": out += "⌃"
            case "opt", "option", "alt": out += "⌥"
            case "shift": out += "⇧"
            case "cmd", "command": out += "⌘"
            case "fn": out += "fn "
            default: out += p.count == 1 ? p.uppercased() : p.capitalized
            }
        }
        return out
    }

    static func carbonModifiers(_ f: CGEventFlags) -> UInt32 {
        var m: UInt32 = 0
        if f.contains(.maskCommand) { m |= UInt32(cmdKey) }
        if f.contains(.maskShift) { m |= UInt32(shiftKey) }
        if f.contains(.maskAlternate) { m |= UInt32(optionKey) }
        if f.contains(.maskControl) { m |= UInt32(controlKey) }
        return m
    }
}

// MARK: - 전역 단축키 (Carbon, 손쉬운 사용 권한 불필요)

final class HotKeys {
    static let shared = HotKeys()
    private var refs: [EventHotKeyRef] = []
    private var handlers: [UInt32: () -> Void] = [:]
    private var nextID: UInt32 = 1
    private var installed = false

    func removeAll() {
        for r in refs { UnregisterEventHotKey(r) }
        refs.removeAll(); handlers.removeAll(); nextID = 1
    }

    @discardableResult
    func register(_ combo: String, _ handler: @escaping () -> Void) -> Bool {
        installIfNeeded()
        guard let c = Keys.parse(combo) else { return false }
        let id = EventHotKeyID(signature: OSType(0x45444B31), id: nextID)
        var ref: EventHotKeyRef?
        let st = RegisterEventHotKey(UInt32(c.code), Keys.carbonModifiers(c.flags), id,
                                     GetApplicationEventTarget(), 0, &ref)
        guard st == noErr, let r = ref else {
            Log.write("전역 단축키 등록 실패: \(combo) (\(st))")
            return false
        }
        refs.append(r)
        handlers[nextID] = handler
        nextID += 1
        return true
    }

    private func installIfNeeded() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            let id = hk.id
            DispatchQueue.main.async { HotKeys.shared.handlers[id]?() }
            return noErr
        }, 1, &spec, nil, nil)
    }
}
