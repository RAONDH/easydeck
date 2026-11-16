using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EasyDeck
{
    // 동작 스크립트 — 한 줄에 동작 하나, 위에서부터 순서대로. '#'으로 시작하면 주석.
    //
    //   key ctrl+shift+4         단축키 (여러 개: key ctrl+k ctrl+s)
    //   type 안녕하세요\n반가워요   글자 입력 (\n = 줄바꿈)
    //   paste 긴 문장             클립보드로 붙여넣기 (원래 클립보드 복원)
    //   open https://… | C:\폴더 | notepad | ~/Downloads
    //   shell 명령               cmd 명령을 창 없이 실행
    //   powershell 명령          파워셸 명령을 창 없이 실행
    //   volume up|down|mute|50|+5|-5
    //   media play|next|prev|stop
    //   brightness up|down|50    (노트북 내장 화면)
    //   darkmode toggle|on|off
    //   delay 0.5
    //   page 이름|next|prev|2
    //   layout row|column|flip|undo
    //   notify 메시지   sound Glass   deck hide|show|toggle
    //
    // {value} 는 노브/슬라이더 값(0~100)으로 바뀜.

    static class ActionRunner
    {
        public static readonly string[][] Templates =
        {
            new[] { "단축키 입력", "key ctrl+c" },
            new[] { "글자 입력", "type 안녕하세요" },
            new[] { "붙여넣기로 입력", "paste 긴 문장도 한 번에" },
            new[] { "웹사이트 열기", "open https://www.google.com" },
            new[] { "프로그램 열기", "open notepad" },
            new[] { "폴더 열기", "open ~/Downloads" },
            new[] { "명령 실행 (cmd)", "shell start calc" },
            new[] { "파워셸 명령", "powershell Start-Process calc" },
            new[] { "음량 올리기", "volume +5" },
            new[] { "음량 내리기", "volume -5" },
            new[] { "음소거", "volume mute" },
            new[] { "재생/일시정지", "media play" },
            new[] { "다음 곡", "media next" },
            new[] { "이전 곡", "media prev" },
            new[] { "밝기 올리기", "brightness up" },
            new[] { "밝기 내리기", "brightness down" },
            new[] { "다크 모드 전환", "darkmode toggle" },
            new[] { "대기", "delay 0.5" },
            new[] { "다음 페이지", "page next" },
            new[] { "가로↔세로 배치 바꾸기", "layout flip" },
            new[] { "알림", "notify 완료!" },
            new[] { "소리", "sound Glass" },
        };

        /// 백그라운드에서 순서대로 실행. 전역 단축키로 눌렀으면 손을 뗄 때까지 기다렸다가 보냄
        public static void Run(string script, Dictionary<string, string> vars = null, bool waitModifiers = false)
        {
            if (string.IsNullOrWhiteSpace(script)) return;
            var t = new Thread(() =>
            {
                if (waitModifiers) Input.WaitModifiersUp();
                foreach (var raw in script.Replace("\r\n", "\n").Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (vars != null) foreach (var kv in vars) line = line.Replace("{" + kv.Key + "}", kv.Value);
                    var (cmd, arg) = Split(line);
                    try { Exec(cmd.ToLowerInvariant(), arg); }
                    catch (Exception e)
                    {
                        Log.Write("동작 실패 '" + line + "': " + e.Message);
                        var msg = e.Message;
                        Ui.Post(() => Toast.Say("동작 실패: " + line + "\n" + msg));
                    }
                }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public enum Risk { Safe, Input, RunsCode }

        /// 가져온 프로필 검사용. 여기 안전 목록에 없는 동작은 모두 위험으로 봄 (Exec와 함께 관리)
        public static Risk RiskOf(string cmd)
        {
            switch ((cmd ?? "").ToLowerInvariant())
            {
                case "volume": case "vol": case "media": case "brightness": case "bright": case "darkmode": case "dark":
                case "delay": case "wait": case "sleep": case "page": case "layout": case "notify": case "toast":
                case "sound": case "deck":
                    return Risk.Safe;
                case "key": case "hotkey": case "type": case "text": case "paste": case "open":
                    return Risk.Input;
                default:
                    return Risk.RunsCode;
            }
        }

        public static (string, string) Split(string line)
        {
            int i = line.IndexOfAny(new[] { ' ', '\t' });
            return i < 0 ? (line, "") : (line.Substring(0, i), line.Substring(i).Trim());
        }

        public static void Exec(string cmd, string arg)
        {
            switch (cmd)
            {
                case "key": case "hotkey":
                    foreach (var combo in arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!KeyMap.TryParse(combo, out var c)) throw new Exception("알 수 없는 키: " + combo);
                        Input.Key(c);
                        Thread.Sleep(15);
                    }
                    break;
                case "type": case "text":
                    Input.Type(Unescape(arg));
                    break;
                case "paste":
                    Input.Paste(Unescape(arg));
                    break;
                case "open":
                    Open(arg);
                    break;
                case "shell": case "sh": case "run": case "cmd":
                    Shell("cmd.exe", "/c " + arg);
                    break;
                case "powershell": case "ps": case "pwsh":
                    Shell("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(arg)));
                    break;
                case "applescript": case "osascript": case "shortcut": case "shortcuts":
                    throw new Exception("맥 전용 동작입니다");
                case "volume": case "vol":
                    Volume(arg);
                    break;
                case "media":
                    switch (arg.ToLowerInvariant())
                    {
                        case "play": case "pause": case "playpause": case "toggle": Input.Vk(0xB3); break;
                        case "next": case "forward": Input.Vk(0xB0); break;
                        case "prev": case "previous": case "back": Input.Vk(0xB1); break;
                        case "stop": Input.Vk(0xB2); break;
                        default: throw new Exception("media play|next|prev|stop");
                    }
                    break;
                case "brightness": case "bright":
                    Brightness.Change(arg);
                    break;
                case "darkmode": case "dark":
                    DarkMode(arg);
                    break;
                case "delay": case "wait": case "sleep":
                    {
                        double s = double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.5;
                        Thread.Sleep((int)(Settings.Clamp(s, 0, 600) * 1000));
                        break;
                    }
                case "layout":
                    Ui.Post(() => DeckForm.Instance?.ChangeLayout(arg));
                    break;
                case "page":
                    Ui.Post(() => DeckForm.Instance?.SwitchPage(arg));
                    break;
                case "notify": case "toast":
                    {
                        var m = Unescape(arg);
                        Ui.Post(() => Toast.Say(m));
                        break;
                    }
                case "sound":
                    Sound(arg);
                    break;
                case "deck":
                    Ui.Post(() =>
                    {
                        var d = DeckForm.Instance;
                        if (d == null) return;
                        switch (arg.ToLowerInvariant())
                        {
                            case "hide": d.HideDeck(); break;
                            case "show": d.ShowDeck(); break;
                            default: d.ToggleVisible(); break;
                        }
                    });
                    break;
                default:
                    throw new Exception("알 수 없는 동작 '" + cmd + "'");
            }
        }

        public static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");

        static readonly string[] scriptExts = { ".exe", ".bat", ".cmd", ".lnk", ".msc", ".com", ".url", ".ps1", ".vbs" };

        public static void Open(string target)
        {
            var t = target.Trim().Trim('"');
            if (t.Length == 0) throw new Exception("열 대상이 비어 있습니다");
            if (t.StartsWith("~")) t = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + t.Substring(1);
            t = Environment.ExpandEnvironmentVariables(t);
            if (t.IndexOf("://", StringComparison.Ordinal) > 0 || t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
            {
                ShellOpen(t); return;
            }
            var local = t;
            try { if (Path.IsPathRooted(local)) local = local.Replace('/', '\\'); } catch { }
            if (File.Exists(local) || Directory.Exists(local)) { ShellOpen(local); return; }
            bool hasExt = scriptExts.Any(e => t.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (t.Contains(".") && !t.Contains(" ") && !t.Contains("\\") && !hasExt)
            {
                ShellOpen("https://" + t); return;
            }
            try { ShellOpen(t); }
            catch (Win32Exception) { throw new Exception("열 수 없음: " + target); }
        }

        public static void ShellOpen(string t)
        {
            using (Process.Start(new ProcessStartInfo(t) { UseShellExecute = true })) { }
        }

        static void Shell(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            using (var p = Process.Start(psi))
            {
                var err = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) err.AppendLine(e.Data); };
                p.OutputDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                p.WaitForExit();
                if (p.ExitCode != 0) Log.Write(exe + " 종료코드 " + p.ExitCode + ": " + args + "\n" + err);
            }
        }

        static void Volume(string arg)
        {
            var a = arg.Trim().ToLowerInvariant();
            switch (a)
            {
                case "up": Input.Vk(0xAF); return;
                case "down": Input.Vk(0xAE); return;
                case "mute": case "toggle": Input.Vk(0xAD); return;
            }
            if ((a.StartsWith("+") || a.StartsWith("-")) && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                var cur = Audio.Get() ?? throw new Exception("소리 장치를 찾을 수 없습니다");
                Audio.Set(cur + d);
            }
            else if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                if (!Audio.Set(v)) throw new Exception("소리 장치를 찾을 수 없습니다");
            }
            else throw new Exception("volume up|down|mute|숫자|+5|-5");
        }

        const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static bool IsDarkMode()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(ThemeKey))
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }

        static void DarkMode(string arg)
        {
            bool dark;
            switch (arg.Trim().ToLowerInvariant())
            {
                case "on": case "dark": dark = true; break;
                case "off": case "light": dark = false; break;
                default: dark = !IsDarkMode(); break;
            }
            using (var k = Registry.CurrentUser.CreateSubKey(ThemeKey))
            {
                k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            }
            Native.SendMessageTimeout((IntPtr)0xffff, Native.WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", 2, 200, out _);
        }

        static void Sound(string arg)
        {
            var name = arg.Trim();
            if (Icons.IsNetworkPath(name)) throw new Exception("네트워크 경로의 소리 파일은 열지 않습니다");
            try
            {
                if (name.Length > 0 && File.Exists(name)) { new SoundPlayer(name).Play(); return; }
                var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", name + ".wav");
                if (name.Length > 0 && !Icons.IsNetworkPath(media) && File.Exists(media)) { new SoundPlayer(media).Play(); return; }
            }
            catch (Exception e) { Log.Write("소리 재생 실패: " + e.Message); }
            switch (name.ToLowerInvariant())
            {
                case "beep": SystemSounds.Beep.Play(); break;
                case "exclamation": case "warning": case "basso": SystemSounds.Exclamation.Play(); break;
                case "hand": case "error": case "stop": case "sosumi": SystemSounds.Hand.Play(); break;
                case "question": SystemSounds.Question.Play(); break;
                default: SystemSounds.Asterisk.Play(); break;
            }
        }
    }

    // MARK: 키보드 입력 보내기

    static class Input
    {
        public static void Key(Combo c)
        {
            var mods = new List<int>();
            if (c.Ctrl) mods.Add(0xA2);
            if (c.Alt) mods.Add(0xA4);
            if (c.Shift) mods.Add(0xA0);
            if (c.Win) mods.Add(0x5B);
            foreach (var m in mods) Native.Send(Native.Key(m, false));
            if (mods.Count > 0) Thread.Sleep(8);
            Native.Send(Native.Key(c.Vk, false));
            Thread.Sleep(8);
            Native.Send(Native.Key(c.Vk, true));
            if (mods.Count > 0) Thread.Sleep(8);
            for (int i = mods.Count - 1; i >= 0; i--) Native.Send(Native.Key(mods[i], true));
        }

        public static void Vk(int vk)
        {
            Native.Send(Native.Key(vk, false));
            Thread.Sleep(8);
            Native.Send(Native.Key(vk, true));
        }

        /// 유니코드 글자를 그대로 입력 (한글 입력기 상태와 상관없음)
        public static void Type(string text)
        {
            var batch = new List<Native.INPUT>();
            void flush()
            {
                if (batch.Count == 0) return;
                Native.Send(batch.ToArray());
                batch.Clear();
                Thread.Sleep(6);
            }
            foreach (char ch in text)
            {
                if (ch == '\r') continue;
                if (ch == '\n' || ch == '\t')
                {
                    flush();
                    Vk(ch == '\n' ? 0x0D : 0x09);
                    Thread.Sleep(10);
                    continue;
                }
                batch.Add(Native.Unicode(ch, false));
                batch.Add(Native.Unicode(ch, true));
                if (batch.Count >= 32) flush();
            }
            flush();
        }

        /// 클립보드에 넣고 Ctrl+V 후 원래 클립보드 복원
        public static void Paste(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            DataObject saved = null;
            Ui.Send(() =>
            {
                try { saved = CopyClipboard(); } catch { }
                Retry(() => Clipboard.SetText(text));
            });
            Key(new Combo { Ctrl = true, Vk = 0x56 });
            Thread.Sleep(400);
            Ui.Send(() =>
            {
                try
                {
                    if (saved != null && saved.GetFormats().Length > 0) Retry(() => Clipboard.SetDataObject(saved, true));
                    else Retry(Clipboard.Clear);
                }
                catch (Exception e) { Log.Write("클립보드 복원 실패: " + e.Message); }
            });
        }

        static DataObject CopyClipboard()
        {
            var src = Clipboard.GetDataObject();
            if (src == null) return null;
            var dst = new DataObject();
            foreach (var f in src.GetFormats(false))
            {
                try { var d = src.GetData(f, false); if (d != null) dst.SetData(f, false, d); } catch { }
            }
            return dst;
        }

        static void Retry(Action a)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (ExternalException) when (i < 5) { Thread.Sleep(60); }
            }
        }

        /// 전역 단축키로 실행할 때: Ctrl·Alt·Shift·Win을 뗄 때까지 잠깐 기다림 (눌린 채로 키를 보내면 섞임)
        public static void WaitModifiersUp()
        {
            var until = DateTime.Now.AddMilliseconds(1500);
            while (DateTime.Now < until)
            {
                bool any = false;
                foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                    if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) any = true;
                if (!any) return;
                Thread.Sleep(20);
            }
        }
    }

    // MARK: 시스템 음량 (Core Audio)

    static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out int count);
            [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static IAudioEndpointVolume Endpoint()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0 || dev == null) return null;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out var o) != 0) return null;
                return o as IAudioEndpointVolume;
            }
            catch (Exception e) { Log.Write("음량 장치 열기 실패: " + e.Message); return null; }
        }

        /// 0~100, 장치가 없으면 null
        public static double? Get()
        {
            var ep = Endpoint();
            if (ep == null) return null;
            try { return ep.GetMasterVolumeLevelScalar(out var v) == 0 ? Math.Round(v * 100) : (double?)null; }
            finally { Marshal.ReleaseComObject(ep); }
        }

        public static bool Set(double v)
        {
            var ep = Endpoint();
            if (ep == null) return false;
            try
            {
                var g = Guid.Empty;
                if (v > 0) ep.SetMute(false, ref g);
                return ep.SetMasterVolumeLevelScalar((float)(Settings.Clamp(v, 0, 100) / 100.0), ref g) == 0;
            }
            finally { Marshal.ReleaseComObject(ep); }
        }
    }

    // MARK: 화면 밝기 (노트북 내장 화면, WMI)

    static class Brightness
    {
        public static int? Get()
        {
            using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                foreach (ManagementObject o in s.Get())
                    using (o) return Convert.ToInt32(o["CurrentBrightness"]);
            return null;
        }

        public static void Set(int v)
        {
            v = (int)Settings.Clamp(v, 0, 100);
            bool done = false;
            using (var c = new ManagementClass(@"root\WMI", "WmiMonitorBrightnessMethods", null))
                foreach (ManagementObject o in c.GetInstances())
                    using (o) { o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v }); done = true; }
            if (!done) throw new Exception("이 화면은 밝기 조절을 지원하지 않습니다 (노트북 내장 화면만 됩니다)");
        }

        public static void Change(string arg)
        {
            var a = arg.Trim().ToLowerInvariant();
            int? cur;
            try { cur = Get(); }
            catch (ManagementException) { cur = null; }
            if (cur == null) throw new Exception("이 화면은 밝기 조절을 지원하지 않습니다 (노트북 내장 화면만 됩니다)");
            if (a == "up" || a == "+") Set(cur.Value + 10);
            else if (a == "down" || a == "-") Set(cur.Value - 10);
            else if ((a.StartsWith("+") || a.StartsWith("-")) && int.TryParse(a, out var d)) Set(cur.Value + d);
            else if (int.TryParse(a, out var v)) Set(v);
            else throw new Exception("brightness up|down|숫자");
        }
    }

    // MARK: 가져온 프로필 검사

    static class ImportCheck
    {
        /// 키 입력·열기·명령 실행이 들어 있으면 전부 보여 주고 확인을 받음
        public static bool Confirm(IWin32Window owner, Profile p)
        {
            var danger = new List<string>();
            var input = new List<string>();
            foreach (var page in p.pages)
                foreach (var w in page.widgets)
                    foreach (var kv in w.actions)
                        foreach (var raw in (kv.Value ?? "").Split('\n'))
                        {
                            var line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#")) continue;
                            var r = ActionRunner.RiskOf(ActionRunner.Split(line).Item1);
                            if (r == ActionRunner.Risk.Safe) continue;
                            var item = "[" + Clean(page.name) + "] " + Clean(w.label) + " — " + Clean(line);
                            (r == ActionRunner.Risk.RunsCode ? danger : input).Add(item);
                        }
            if (danger.Count == 0 && input.Count == 0) return true;

            var sb = new StringBuilder();
            if (danger.Count > 0)
            {
                sb.AppendLine("⚠ 명령 실행 (컴퓨터를 바꿀 수 있음) — " + danger.Count + "개");
                foreach (var d in danger) sb.AppendLine("   " + d);
                sb.AppendLine();
            }
            if (input.Count > 0)
            {
                sb.AppendLine("⌨ 키 입력·열기 (명령 실행에 쓰일 수 있음) — " + input.Count + "개");
                foreach (var d in input) sb.AppendLine("   " + d);
            }

            using (var f = new Form
            {
                Text = "프로필 가져오기 확인", StartPosition = FormStartPosition.CenterParent, Size = new System.Drawing.Size(640, 480),
                Font = new System.Drawing.Font(Fonts.UiFamily, 9f), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            })
            {
                var top = new Label
                {
                    Dock = DockStyle.Top, Height = 54, Padding = new Padding(10, 10, 10, 0),
                    Text = "이 프로필에는 아래 동작이 들어 있습니다. 믿을 수 있는 사람에게 받은 파일만 가져오세요.\n가져오기 전에 지금 프로필은 자동으로 백업됩니다.",
                };
                var box = new TextBox
                {
                    Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                    Text = sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"), Font = new System.Drawing.Font("Consolas", 9.5f),
                };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                var ok = new Button { Text = "가져오기", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
                bar.Controls.Add(cancel);
                bar.Controls.Add(ok);
                f.Controls.Add(box);
                f.Controls.Add(top);
                f.Controls.Add(bar);
                f.AcceptButton = cancel;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        static string Clean(string s)
            => new string((s ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }
}
