// 宠物游戏守卫 PetGuard —— 打游戏/全屏时自动藏起 DSH 桌宠，其余时间保证它露脸。
//
// 原理：不碰 dsh-pet 插件本体（升级不会被覆盖），只按窗口句柄把桌宠那扇
// Chrome_WidgetWin_1 小窗 ShowWindow 隐藏/还原。桌宠的渲染进程照常跑，
// 只是画面上看不见；退出游戏立刻用 SWP_SHOWWINDOW|SWP_NOACTIVATE 还原，
// 不抢焦点、不打断当前应用。
//
// 判定「在打游戏」的三个信号（任一成立即算）：
//   ① 前台窗口所属进程在游戏目录里（Steam 库自动扫描 / Epic / Xbox / 额外目录）；
//   ② 前台窗口铺满整块显示器且无标题栏（无边框全屏，绝大多数现代游戏）；
//   ③ Windows 自己的免打扰状态 SHQueryUserNotificationState = BUSY / D3D_FULL_SCREEN。
// 误判防护：开始菜单、Alt-Tab、任务栏、桌面等外壳窗口一律不算；忽略名单里的进程不算。
// 抖动防护：连续 N 次判定一致才动手，避免加载画面的瞬间闪烁。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PetGuard
{
    // ---------------------------------------------------------------- Win32

    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int n);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int pid);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int GWL_STYLE = -16;
        public const int WS_CAPTION = 0x00C00000;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_HIDEWINDOW = 0x0080;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    }

    // ---------------------------------------------------------------- 配置

    internal sealed class Config
    {
        public bool Enabled = true;
        public int PollMs = 350;
        public int HideAfter = 2;
        public int ShowAfter = 2;
        public bool HideWhenFullscreen = true;
        public bool HideWhenGameProcess = true;
        public bool RescanGameDirs = true;
        public int RescanMinutes = 5;
        public bool DebugLog = false;
        public List<string> IgnoreProcesses = new List<string>();
        public List<string> ExtraGameDirs = new List<string>();
        public List<string> ExtraGameExes = new List<string>();

        public static Config Load(string path)
        {
            Config c = new Config();
            c.IgnoreProcesses.Add("explorer.exe");
            c.IgnoreProcesses.Add("petguard.exe");
            if (!File.Exists(path)) { c.SaveDefault(path); return c; }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                try
                {
                    switch (key)
                    {
                        case "enabled": c.Enabled = Bool(val, c.Enabled); break;
                        case "poll_ms": c.PollMs = Math.Max(100, Math.Min(5000, Int(val, c.PollMs))); break;
                        case "hide_after": c.HideAfter = Math.Max(1, Math.Min(20, Int(val, c.HideAfter))); break;
                        case "show_after": c.ShowAfter = Math.Max(1, Math.Min(20, Int(val, c.ShowAfter))); break;
                        case "hide_when_fullscreen": c.HideWhenFullscreen = Bool(val, c.HideWhenFullscreen); break;
                        case "hide_when_game_process": c.HideWhenGameProcess = Bool(val, c.HideWhenGameProcess); break;
                        case "scan_steam_libraries": c.RescanGameDirs = Bool(val, c.RescanGameDirs); break;
                        case "rescan_minutes": c.RescanMinutes = Math.Max(1, Math.Min(600, Int(val, c.RescanMinutes))); break;
                        case "debug_log": c.DebugLog = Bool(val, c.DebugLog); break;
                        case "ignore_processes": c.IgnoreProcesses.AddRange(SplitList(val)); break;
                        case "extra_game_dirs": c.ExtraGameDirs.AddRange(SplitList(val)); break;
                        case "extra_game_exes": c.ExtraGameExes.AddRange(SplitList(val)); break;
                    }
                }
                catch { /* 单行写坏不影响其它配置 */ }
            }
            return c;
        }

        public void SaveDefault(string path)
        {
            string text =
                "; 宠物游戏守卫配置 —— 改完保存，右键托盘图标选「重新加载配置」即生效\r\n" +
                "\r\n" +
                "; 总开关：0 = 完全停用（宠物一直显示）\r\n" +
                "enabled=1\r\n" +
                "\r\n" +
                "; 检测间隔（毫秒），越小反应越快，350 足够\r\n" +
                "poll_ms=350\r\n" +
                "; 连续几次判定为「游戏中」才隐藏 / 连续几次判定「不在游戏」才显示（防闪）\r\n" +
                "hide_after=2\r\n" +
                "show_after=2\r\n" +
                "\r\n" +
                "; 进了全屏应用（含全屏游戏、全屏视频）就隐藏\r\n" +
                "hide_when_fullscreen=1\r\n" +
                "; 前台是游戏程序就隐藏（覆盖窗口化运行的游戏）\r\n" +
                "hide_when_game_process=1\r\n" +
                "\r\n" +
                "; 自动扫描 Steam 库目录（含 libraryfolders.vdf 里的其它盘）\r\n" +
                "scan_steam_libraries=1\r\n" +
                "; 多久重新扫一次游戏目录（分钟）\r\n" +
                "rescan_minutes=5\r\n" +
                "\r\n" +
                "; 切换时写日志到 petguard.log（排查用，平时 0 就行）\r\n" +
                "debug_log=0\r\n" +
                "\r\n" +
                "; 这些进程前台时一律不隐藏（逗号分隔，写 exe 名即可）\r\n" +
                "ignore_processes=explorer.exe,DeepSeek Harness.exe\r\n" +
                "; 额外的游戏目录（逗号分隔，整棵目录都当成游戏）\r\n" +
                "extra_game_dirs=\r\n" +
                "; 额外的游戏进程名（逗号分隔）\r\n" +
                "extra_game_exes=\r\n";
            try { File.WriteAllText(path, text, Encoding.UTF8); } catch { }
        }

        static bool Bool(string v, bool dflt)
        {
            v = v.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return dflt;
        }

        static int Int(string v, int dflt)
        {
            int n;
            return int.TryParse(v, out n) ? n : dflt;
        }

        static IEnumerable<string> SplitList(string v)
        {
            List<string> outp = new List<string>();
            foreach (string p in v.Split(new char[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = p.Trim();
                if (t.Length > 0) outp.Add(t);
            }
            return outp;
        }
    }

    // ---------------------------------------------------------------- 游戏目录

    internal static class Games
    {
        static readonly object Gate = new object();
        static List<string> dirs = new List<string>();
        static HashSet<string> exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Refresh()
        {
            List<string> found = new List<string>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Program.Cfg.RescanGameDirs)
            {
                foreach (string steamRoot in SteamRoots())
                {
                    AddDir(found, Path.Combine(steamRoot, @"steamapps\common"));
                    foreach (string lib in LibraryRoots(steamRoot))
                        AddDir(found, Path.Combine(lib, @"steamapps\common"));
                }
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            AddDir(found, Path.Combine(pf, "Epic Games"));
            AddDir(found, Path.Combine(pf, "XboxGames"));
            AddDir(found, Path.Combine(pf, "Riot Games"));
            AddDir(found, Path.Combine(pf, "WeGameApps"));
            AddDir(found, Path.Combine(pf86, "Epic Games"));
            AddDir(found, Path.Combine(pf86, "GOG Galaxy\\Games"));
            AddDir(found, Path.Combine(pf86, "WeGameApps"));
            AddDir(found, @"C:\XboxGames");
            foreach (string extra in Program.Cfg.ExtraGameDirs) AddDir(found, extra);

            foreach (string e in Program.Cfg.ExtraGameExes) names.Add(Path.GetFileName(e.Trim()));

            lock (Gate)
            {
                dirs = found;
                exes = names;
            }
        }

        static void AddDir(List<string> list, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { if (!Directory.Exists(dir)) return; } catch { return; }
            string full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            if (!list.Contains(full)) list.Add(full);
        }

        static IEnumerable<string> SteamRoots()
        {
            List<string> roots = new List<string>();
            string[] probes = new string[]
            {
                @"HKEY_CURRENT_USER\Software\Valve\Steam|SteamPath",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam|InstallPath",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam|InstallPath",
            };
            foreach (string probe in probes)
            {
                try
                {
                    string[] parts = probe.Split('|');
                    object v = Registry.GetValue(parts[0], parts[1], null);
                    string s = v as string;
                    if (!string.IsNullOrEmpty(s) && Directory.Exists(s) && !roots.Contains(s)) roots.Add(s);
                }
                catch { }
            }
            string dflt = @"C:\Program Files (x86)\Steam";
            try { if (Directory.Exists(dflt) && !roots.Contains(dflt)) roots.Add(dflt); } catch { }
            return roots;
        }

        static IEnumerable<string> LibraryRoots(string steamRoot)
        {
            List<string> libs = new List<string>();
            try
            {
                string vdf = Path.Combine(steamRoot, @"steamapps\libraryfolders.vdf");
                if (!File.Exists(vdf)) return libs;
                string text = File.ReadAllText(vdf);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(p) && !libs.Contains(p)) libs.Add(p);
                }
            }
            catch { }
            return libs;
        }

        public static bool IsGameProcess(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            string name = Path.GetFileName(exePath);
            lock (Gate)
            {
                if (exes.Contains(name)) return true;
                string full = exePath.ToLowerInvariant();
                foreach (string d in dirs) if (full.StartsWith(d.ToLowerInvariant(), StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static int DirCount { get { lock (Gate) return dirs.Count; } }

        public static string[] Dirs { get { lock (Gate) return dirs.ToArray(); } }

        public static string[] ExeNames { get { lock (Gate) return new List<string>(exes).ToArray(); } }
    }

    // ---------------------------------------------------------------- 桌宠窗口

    internal static class Pets
    {
        // 桌宠窗口的特征：Chrome_WidgetWin_1 + 标题以 dsh-pet 开头（helper 的其它窗口无标题或类名不同）
        public static List<IntPtr> Find(bool visibleOnly)
        {
            List<IntPtr> outp = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (visibleOnly && !Native.IsWindowVisible(h)) return true;
                StringBuilder title = new StringBuilder(256);
                Native.GetWindowText(h, title, 256);
                string t = title.ToString();
                if (t.Length < 7 || !t.StartsWith("dsh-pet", StringComparison.OrdinalIgnoreCase)) return true;
                StringBuilder cls = new StringBuilder(64);
                Native.GetClassName(h, cls, 64);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                outp.Add(h);
                return true;
            }, IntPtr.Zero);
            return outp;
        }

        // 最近一次 Show/Hide 的失败原因（Access Denied = 权限不足，例如被低完整性沙箱启动）
        public static string LastError = "";

        public static int HideAll() { return Apply(false); }
        public static int ShowAll() { return Apply(true); }

        static int Apply(bool show)
        {
            int n = 0;
            LastError = "";
            foreach (IntPtr h in Find(false))
            {
                if (Native.IsWindowVisible(h) == show) continue;
                if (show)
                {
                    // SWP_SHOWWINDOW 显示、NOACTIVATE 不抢焦点、顺带把置顶重新钉一遍
                    Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                    if (!Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
                }
                else
                {
                    Native.ShowWindow(h, Native.SW_HIDE);
                    if (Native.IsWindowVisible(h))
                        Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_HIDEWINDOW);
                }
                if (Native.IsWindowVisible(h) == show) n++;
                else LastError = "err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            }
            return n;
        }

        public static int VisibleCount()
        {
            return Find(true).Count;
        }

        // 诊断用：n=总数 |hwnd|V 或 H|标题
        public static string Describe()
        {
            List<IntPtr> all = Find(false);
            StringBuilder sb = new StringBuilder();
            sb.Append("n=" + all.Count);
            foreach (IntPtr h in all)
            {
                StringBuilder t = new StringBuilder(256);
                Native.GetWindowText(h, t, 256);
                sb.Append(" |" + h.ToInt64() + (Native.IsWindowVisible(h) ? "V" : "H") + ":" + t);
            }
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- 判定

    internal static class Detect
    {
        public static string Reason = "";
        public static string Foreground = "";
        public static int QuietState;

        public static bool IsGaming()
        {
            Reason = "";
            Foreground = "";
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                string cls = ClassName(fg);
                string exe = ProcessPath(fg);
                string exeName = exe.Length > 0 ? Path.GetFileName(exe) : "";
                Foreground = exeName + " [" + cls + "]";
                if (IsShellOverlay(cls)) { Reason = "shell"; return false; }
                if (IsIgnored(exeName)) { Reason = "ignored " + exeName; return false; }

                if (Program.Cfg.HideWhenGameProcess && Games.IsGameProcess(exe))
                {
                    Reason = "game-process " + exeName;
                    return true;
                }
                if (Program.Cfg.HideWhenFullscreen && IsFullscreen(fg))
                {
                    Reason = "fullscreen " + exeName;
                    return true;
                }
            }
            if (Program.Cfg.HideWhenFullscreen)
            {
                int st;
                QuietState = Native.SHQueryUserNotificationState(out st) == 0 ? st : 0;
                // 只用 3（D3D 独占全屏）。实测 2（BUSY）在普通桌面开着模态对话框时也会返回，
                // 拿它当判据会把宠物在平时藏起来，直接弃用。
                if (QuietState == 3)
                {
                    Reason = "d3d-fullscreen";
                    return true;
                }
            }
            if (Reason.Length == 0) Reason = "idle";
            return false;
        }

        static bool IsIgnored(string exeName)
        {
            if (exeName.Length == 0) return false;
            string lower = exeName.ToLowerInvariant();
            string bare = lower.EndsWith(".exe") ? lower.Substring(0, lower.Length - 4) : lower;
            foreach (string ig in Program.Cfg.IgnoreProcesses)
            {
                string v = ig.Trim().ToLowerInvariant();
                if (v.Length == 0) continue;
                if (v == lower || v == bare) return true;
                if (v.EndsWith(".exe") && v == lower) return true;
            }
            return false;
        }

        // 开始菜单 / Alt-Tab / 任务栏 / 桌面等外壳窗口：长得像全屏但不是游戏
        static bool IsShellOverlay(string cls)
        {
            if (cls.Length == 0) return false;
            string[] exact = new string[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "ForegroundStaging", "MultitaskingViewFrame", "TaskSwitcherWnd", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow" };
            foreach (string e in exact) if (string.Equals(cls, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool IsFullscreen(IntPtr hwnd)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return false;

            IntPtr mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(mon, ref mi)) return false;
            Native.RECT m = mi.rcMonitor;

            // 四条边都贴到显示器边界（±2px 容差）：最大化窗口会在任务栏一侧露缝，天然被排除
            if (r.Left > m.Left + 2 || r.Top > m.Top + 2) return false;
            if (r.Right < m.Right - 2 || r.Bottom < m.Bottom - 2) return false;

            // 有标题栏的窗口不算全屏游戏（任务栏自动隐藏时最大化窗口也会铺满，靠这条排除）
            int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
            if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;
            return true;
        }

        static string ClassName(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(128);
            Native.GetClassName(h, sb, 128);
            return sb.ToString();
        }

        static string ProcessPath(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return "";
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                int size = 1024;
                StringBuilder sb = new StringBuilder(size);
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { Native.CloseHandle(h); }
        }
    }

    // ---------------------------------------------------------------- 主程序

    internal static class Program
    {
        public static Config Cfg;
        static NotifyIcon tray;
        static Mutex single;
        // 跨进程「请退出」信号：--quit 打开同名事件并 Set，常驻实例收到后自己收尾退出
        const string QuitEventName = "PetGuard-DSH-Pet-Quit";
        static volatile bool stop;
        static volatile bool paused;
        static volatile string status = "启动中";

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                // winexe 没有控制台，异常必须落盘，否则出问题完全看不见
                LogAlways("FATAL " + ex);
                try { Pets.ShowAll(); } catch { }
            }
        }

        static void Run(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            bool console = mode.Length > 0;
            if (console) Native.AttachConsole(-1);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Cfg = Config.Load(Path.Combine(baseDir, "config.ini"));
            Games.Refresh();
            if (mode.Length == 0) LogAlways("BOOT dirs=" + Games.DirCount + " enabled=" + Cfg.Enabled + " debug=" + Cfg.DebugLog);

            if (mode == "--once" || mode == "--selftest")
            {
                bool g = Detect.IsGaming();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("pet_windows_visible=" + Pets.VisibleCount());
                sb.AppendLine("game_dirs=" + Games.DirCount);
                sb.AppendLine("foreground=" + Detect.Foreground);
                sb.AppendLine("quiet_state=" + Detect.QuietState);
                sb.AppendLine("gaming=" + (g ? 1 : 0));
                sb.AppendLine("reason=" + Detect.Reason);
                string text = sb.ToString();
                try { File.WriteAllText(Path.Combine(baseDir, "selftest.txt"), text, Encoding.UTF8); } catch { }
                try { Console.Write(text); } catch { }
                return;
            }
            if (mode == "--hide") { Console.WriteLine("hidden=" + Pets.HideAll()); return; }
            if (mode == "--dirs")
            {
                StringBuilder sb = new StringBuilder();
                foreach (string d in Games.Dirs) sb.AppendLine(d);
                foreach (string e in Games.ExeNames) sb.AppendLine("exe:" + e);
                try { File.WriteAllText(Path.Combine(baseDir, "selftest.txt"), sb.ToString(), Encoding.UTF8); } catch { }
                try { Console.Write(sb.ToString()); } catch { }
                return;
            }
            if (mode == "--restore") { Console.WriteLine("shown=" + Pets.ShowAll()); return; }
            if (mode == "--status") { Console.WriteLine(status); return; }
            if (mode == "--quit")
            {
                // 让正在跑的那一份自己退出（插件/脚本停守卫走这条路，不硬杀进程）
                EventWaitHandle evt;
                bool found = false;
                try { found = EventWaitHandle.TryOpenExisting(QuitEventName, out evt); } catch { evt = null; }
                if (found && evt != null)
                {
                    try { evt.Set(); } catch { }
                    try { evt.Dispose(); } catch { }
                }
                try { Console.Write("quit=" + (found ? 1 : 0) + Environment.NewLine); } catch { }
                return;
            }
            if (mode == "--list")
            {
                string d = Pets.Describe();
                try { File.WriteAllText(Path.Combine(baseDir, "selftest.txt"), d, Encoding.UTF8); } catch { }
                try { Console.Write(d); } catch { }
                return;
            }

            if (!Cfg.Enabled) { Pets.ShowAll(); return; }

            bool created;
            // 不用 Global\ 前缀：普通用户没有 SeCreateGlobalPrivilege，建全局对象会抛异常
            single = new Mutex(true, "PetGuard-DSH-Pet", out created);
            if (!created) return;   // 已经有一份在跑

            // 启动即还原：上一份守卫如果在隐藏状态下被杀，宠物不会永远消失
            Pets.ShowAll();
            Log("START watcher");

            // 监听 --quit：等到信号就把宠物放出来并退出进程
            try
            {
                EventWaitHandle quitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
                Thread quitWatcher = new Thread(delegate ()
                {
                    try
                    {
                        while (true)
                        {
                            if (!quitEvt.WaitOne()) continue;
                            Log("QUIT signal");
                            Exit();
                            return;
                        }
                    }
                    catch { }
                });
                quitWatcher.IsBackground = true;
                quitWatcher.Start();
            }
            catch { }

            // 父进程（DSH 宿主）退出后自己也退出：宿主被强杀时不留下孤立的托盘进程
            try
            {
                string rawParent = Environment.GetEnvironmentVariable("PETGUARD_PARENT_PID");
                int parentPid;
                if (int.TryParse(rawParent, out parentPid) && parentPid > 0)
                {
                    Thread parentWatcher = new Thread(delegate ()
                    {
                        try
                        {
                            Process parent = Process.GetProcessById(parentPid);
                            parent.WaitForExit();
                            LogAlways("PARENT exit");
                            Exit();
                        }
                        catch { }
                    });
                    parentWatcher.IsBackground = true;
                    parentWatcher.Start();
                }
            }
            catch { }

            tray = new NotifyIcon();
            tray.Icon = BuildIcon();
            tray.Text = "宠物游戏守卫";
            tray.ContextMenuStrip = BuildMenu();
            tray.Visible = true;
            tray.DoubleClick += delegate { Restore(); };

            Thread worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Start();

            Application.Run();
        }

        static ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem head = new ToolStripMenuItem("宠物游戏守卫");
            head.Enabled = false;
            menu.Items.Add(head);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem pause = new ToolStripMenuItem("暂停监视（宠物一直显示）");
            pause.Click += delegate { Paused = !Paused; };
            pause.Name = "pause";
            menu.Items.Add(pause);

            ToolStripMenuItem restore = new ToolStripMenuItem("立即显示宠物");
            restore.Click += delegate { Restore(); };
            menu.Items.Add(restore);

            ToolStripMenuItem reload = new ToolStripMenuItem("重新加载配置");
            reload.Click += delegate
            {
                // 原地重载：不重启进程，避免重启瞬间抢不到互斥体而直接退出
                try
                {
                    Cfg = Config.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini"));
                    Games.Refresh();
                    LogAlways("RELOAD 配置已重载");
                    paused = !Cfg.Enabled;
                }
                catch (Exception ex) { LogAlways("RELOAD 失败 " + ex.Message); }
            };
            menu.Items.Add(reload);

            ToolStripMenuItem open = new ToolStripMenuItem("打开配置文件");
            open.Click += delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini")); }
                catch { }
            };
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quit = new ToolStripMenuItem("退出（宠物恢复正常显示）");
            quit.Click += delegate { Exit(); };
            menu.Items.Add(quit);

            menu.Opening += delegate
            {
                ToolStripItem[] found = menu.Items.Find("pause", false);
                if (found.Length > 0) ((ToolStripMenuItem)found[0]).Checked = paused;
            };
            return menu;
        }

        public static bool Paused
        {
            get { return paused; }
            set
            {
                paused = value;
                if (paused) { Pets.ShowAll(); status = "已暂停"; }
                else status = "监视中";
            }
        }

        static void Restore()
        {
            Pets.ShowAll();
            status = "已手动显示";
        }

        static readonly object LogGate = new object();

        public static void Log(string msg)
        {
            if (!Cfg.DebugLog) return;
            LogAlways(msg);
        }

        public static void LogAlways(string msg)
        {
            try
            {
                lock (LogGate)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "petguard.log");
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        static void Exit()
        {
            stop = true;
            Pets.ShowAll();
            Log("EXIT");
            try { tray.Visible = false; tray.Dispose(); } catch { }
            Application.Exit();
        }

        static void WorkerLoop()
        {
            bool applied = false;      // 当前是否处于「已隐藏」
            bool lastRaw = false;
            int same = 0;
            int tick = 0;
            int rescanTicks = Math.Max(1, (Program.Cfg.RescanMinutes * 60 * 1000) / Math.Max(100, Program.Cfg.PollMs));

            while (!stop)
            {
                try
                {
                    Thread.Sleep(Math.Max(100, Cfg.PollMs));
                    tick++;

                    if (Cfg.RescanGameDirs && tick % rescanTicks == 0) Games.Refresh();

                    if (paused)
                    {
                        if (applied) { Pets.ShowAll(); applied = false; }
                        status = "已暂停";
                        continue;
                    }

                    bool raw = Detect.IsGaming();
                    if (raw == lastRaw) same++;
                    else { lastRaw = raw; same = 1; }

                    int need = raw ? Cfg.HideAfter : Cfg.ShowAfter;
                    if (raw != applied && same >= need)
                    {
                        if (raw)
                        {
                            int left = Pets.VisibleCount();
                            int n = Pets.HideAll();
                            applied = true;
                            Log("HIDE(" + n + ") " + Detect.Reason + " | " + Detect.Foreground + " || " + Pets.Describe());
                            if (left > 0 && n == 0)
                                LogAlways("WARN 隐藏失败 " + Pets.LastError + "（桌宠窗口可见却没权限操作，通常是守卫被低权限/沙箱环境启动）");
                        }
                        else
                        {
                            int n = Pets.ShowAll();
                            applied = false;
                            Log("SHOW(" + n + ") " + Detect.Reason + " | " + Detect.Foreground + " || " + Pets.Describe());
                        }
                    }

                    // 自愈：游戏期间桌宠 helper 重启 / 被别的东西显示出来，下一拍再按下去
                    if (applied && Pets.VisibleCount() > 0) Pets.HideAll();

                    status = applied ? ("游戏中：" + Detect.Reason) : ("空闲：" + Detect.Reason);
                    UpdateTray(applied);
                    if (Cfg.DebugLog && tick % 20 == 0)
                        Log("tick " + tick + " gaming=" + (raw ? 1 : 0) + " reason=" + Detect.Reason +
                            " fg=" + Detect.Foreground + " pet=" + Pets.VisibleCount());
                }
                catch (Exception ex)
                {
                    // 任何一次判定异常都不许弄死守卫：还原宠物，继续下一拍
                    LogAlways("ERR " + ex.GetType().Name + ": " + ex.Message);
                    try { if (applied) { Pets.ShowAll(); applied = false; } } catch { }
                    Thread.Sleep(1000);
                }
            }
        }

        static void UpdateTray(bool hidden)
        {
            try
            {
                string text = hidden ? "宠物游戏守卫：游戏中，宠物已隐藏" : "宠物游戏守卫：监视中";
                if (paused) text = "宠物游戏守卫：已暂停";
                if (text.Length > 63) text = text.Substring(0, 63);
                if (tray != null && tray.Text != text) tray.Text = text;
            }
            catch { }
        }

        static Icon BuildIcon()
        {
            try
            {
                string logo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @".dsh\profiles\desktop\node_modules\dsh-pet\assets\logo.png");
                if (File.Exists(logo))
                {
                    using (Bitmap src = new Bitmap(logo))
                    using (Bitmap small = new Bitmap(32, 32))
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, 32, 32);
                        }
                        return Icon.FromHandle(small.GetHicon());
                    }
                }
            }
            catch { }
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(Color.FromArgb(74, 156, 255))) g.FillEllipse(b, 1, 1, 14, 14);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
