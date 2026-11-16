using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace EasyDeck
{
    struct Combo
    {
        public int Vk;
        public bool Ctrl, Alt, Shift, Win;
    }

    /// "ctrl+shift+4" 같은 글자 ↔ 가상 키 코드. 맥 프로필의 cmd·opt도 읽어서 Ctrl·Alt로 바꿈
    static class KeyMap
    {
        static readonly Dictionary<string, int> names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["return"] = 0x0D, ["enter"] = 0x0D, ["tab"] = 0x09, ["space"] = 0x20, ["spacebar"] = 0x20,
            ["backspace"] = 0x08, ["bksp"] = 0x08, ["delete"] = 0x08,           // 맥의 delete = 백스페이스
            ["del"] = 0x2E, ["forwarddelete"] = 0x2E, ["fwddelete"] = 0x2E,
            ["escape"] = 0x1B, ["esc"] = 0x1B,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["leftarrow"] = 0x25, ["uparrow"] = 0x26, ["rightarrow"] = 0x27, ["downarrow"] = 0x28,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
            ["insert"] = 0x2D, ["ins"] = 0x2D, ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["print"] = 0x2C,
            ["capslock"] = 0x14, ["pause"] = 0x13, ["menu"] = 0x5D, ["apps"] = 0x5D, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
            ["minus"] = 0xBD, ["equal"] = 0xBB, ["equals"] = 0xBB, ["plus"] = 0xBB,
            ["comma"] = 0xBC, ["period"] = 0xBE, ["slash"] = 0xBF, ["backslash"] = 0xDC, ["semicolon"] = 0xBA,
            ["quote"] = 0xDE, ["grave"] = 0xC0, ["backtick"] = 0xC0, ["leftbracket"] = 0xDB, ["rightbracket"] = 0xDD,
            ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["mute"] = 0xAD,
            ["play"] = 0xB3, ["playpause"] = 0xB3, ["next"] = 0xB0, ["prev"] = 0xB1, ["previous"] = 0xB1, ["stop"] = 0xB2,
            ["hangul"] = 0x15, ["hanja"] = 0x19,
            ["num0"] = 0x60, ["num1"] = 0x61, ["num2"] = 0x62, ["num3"] = 0x63, ["num4"] = 0x64,
            ["num5"] = 0x65, ["num6"] = 0x66, ["num7"] = 0x67, ["num8"] = 0x68, ["num9"] = 0x69,
            ["nummultiply"] = 0x6A, ["numplus"] = 0x6B, ["numminus"] = 0x6D, ["numdecimal"] = 0x6E, ["numdivide"] = 0x6F,
        };

        static readonly Dictionary<char, int> punct = new Dictionary<char, int>
        {
            ['-'] = 0xBD, ['='] = 0xBB, ['['] = 0xDB, [']'] = 0xDD, ['\\'] = 0xDC, [';'] = 0xBA,
            ['\''] = 0xDE, [','] = 0xBC, ['.'] = 0xBE, ['/'] = 0xBF, ['`'] = 0xC0,
        };

        /// 녹화·표시용 이름 (가상 키 → 이름)
        static readonly Dictionary<int, string> canonical = new Dictionary<int, string>
        {
            [0x0D] = "enter", [0x09] = "tab", [0x20] = "space", [0x08] = "backspace", [0x2E] = "del", [0x1B] = "esc",
            [0x25] = "left", [0x26] = "up", [0x27] = "right", [0x28] = "down", [0x24] = "home", [0x23] = "end",
            [0x21] = "pageup", [0x22] = "pagedown", [0x2D] = "insert", [0x2C] = "printscreen", [0x14] = "capslock",
            [0x13] = "pause", [0x5D] = "menu", [0x90] = "numlock", [0x91] = "scrolllock",
            [0xBD] = "-", [0xBB] = "=", [0xDB] = "[", [0xDD] = "]", [0xDC] = "\\", [0xBA] = ";", [0xDE] = "'",
            [0xBC] = ",", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
            [0xAF] = "volumeup", [0xAE] = "volumedown", [0xAD] = "mute", [0xB3] = "play", [0xB0] = "next", [0xB1] = "prev", [0xB2] = "stop",
            [0x15] = "hangul", [0x19] = "hanja",
            [0x6A] = "nummultiply", [0x6B] = "numplus", [0x6D] = "numminus", [0x6E] = "numdecimal", [0x6F] = "numdivide",
        };

        public static bool TryParse(string spec, out Combo c)
        {
            c = default(Combo);
            var s = (spec ?? "").Trim();
            if (s.Length == 0) return false;
            string key;
            string[] mods;
            if (s.Length > 1 && s.EndsWith("++"))
            {
                key = "plus";
                mods = s.Substring(0, s.Length - 2).Split('+');
            }
            else if (s == "+")
            {
                key = "plus"; mods = new string[0];
            }
            else
            {
                var parts = s.Split('+');
                key = parts[parts.Length - 1];
                mods = parts.Take(parts.Length - 1).ToArray();
            }
            foreach (var raw in mods)
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "cmd": case "command": case "ctrl": case "control": case "ctl": case "⌘": case "⌃":
                        c.Ctrl = true; break;
                    case "opt": case "option": case "alt": case "⌥":
                        c.Alt = true; break;
                    case "shift": case "⇧":
                        c.Shift = true; break;
                    case "win": case "windows": case "super": case "meta": case "start":
                        c.Win = true; break;
                    case "fn": case "":
                        break;
                    default:
                        return false;
                }
            }
            int vk = Vk(key.Trim());
            if (vk == 0) return false;
            c.Vk = vk;
            return true;
        }

        static int Vk(string key)
        {
            if (key.Length == 1)
            {
                char ch = char.ToLowerInvariant(key[0]);
                if (ch >= 'a' && ch <= 'z') return 0x41 + (ch - 'a');
                if (ch >= '0' && ch <= '9') return 0x30 + (ch - '0');
                if (punct.TryGetValue(ch, out var p)) return p;
                return 0;
            }
            if (names.TryGetValue(key, out var v)) return v;
            var lower = key.ToLowerInvariant();
            if (lower.Length >= 2 && lower[0] == 'f' && int.TryParse(lower.Substring(1), out var fn) && fn >= 1 && fn <= 24)
                return 0x70 + fn - 1;
            return 0;
        }

        public static string KeyName(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)('a' + vk - 0x41)).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)('0' + vk - 0x30)).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "num" + (vk - 0x60);
            if (vk >= 0x70 && vk <= 0x87) return "f" + (vk - 0x70 + 1);
            return canonical.TryGetValue(vk, out var n) ? n : null;
        }

        /// 키 녹화: 눌린 키 → "ctrl+shift+a" (수정 키만 눌렸으면 null)
        public static string SpecFromKeys(Keys keyData)
        {
            var code = keyData & Keys.KeyCode;
            switch (code)
            {
                case Keys.ControlKey: case Keys.ShiftKey: case Keys.Menu: case Keys.LWin: case Keys.RWin:
                case Keys.LControlKey: case Keys.RControlKey: case Keys.LShiftKey: case Keys.RShiftKey:
                case Keys.LMenu: case Keys.RMenu: case Keys.None:
                    return null;
            }
            var name = KeyName((int)code);
            if (name == null) return null;
            var parts = new List<string>();
            if ((keyData & Keys.Control) != 0) parts.Add("ctrl");
            if ((keyData & Keys.Alt) != 0) parts.Add("alt");
            if ((keyData & Keys.Shift) != 0) parts.Add("shift");
            if ((Control.ModifierKeys & (Keys.LWin | Keys.RWin)) != 0) parts.Add("win");
            parts.Add(name);
            return string.Join("+", parts);
        }

        public static string Pretty(Combo c)
        {
            var parts = new List<string>();
            if (c.Ctrl) parts.Add("Ctrl");
            if (c.Alt) parts.Add("Alt");
            if (c.Shift) parts.Add("Shift");
            if (c.Win) parts.Add("Win");
            var n = KeyName(c.Vk) ?? ("0x" + c.Vk.ToString("X2"));
            parts.Add(n.Length == 1 ? n.ToUpperInvariant() : char.ToUpperInvariant(n[0]) + n.Substring(1));
            return string.Join("+", parts);
        }

        public static string PrettySpec(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return "";
            return TryParse(spec, out var c) ? Pretty(c) : spec;
        }

        /// RegisterHotKey용 수정 키 값
        public static uint Mods(Combo c)
            => (c.Alt ? 1u : 0) | (c.Ctrl ? 2u : 0) | (c.Shift ? 4u : 0) | (c.Win ? 8u : 0);

        public static bool IsExtended(int vk)
            => (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x2C || vk == 0x5B || vk == 0x5C
               || vk == 0x5D || vk == 0x6F || vk == 0x90 || vk == 0xA3 || vk == 0xA5 || (vk >= 0xAD && vk <= 0xB7);
    }
}
