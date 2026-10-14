using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseWheelGuard
{
    /// <summary>
    /// MouseWheelGuard - Zero-footprint Win32 Low-Level Hardware Mouse Wheel Filter & Debounce Engine.
    /// Eliminates unprompted wheel bouncing, mechanical encoder contact chatter, and packet bursts at the OS level.
    /// </summary>
    static class Program
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;

        // Default Filter Parameters (Overridable via CLI args)
        private static int s_thresholdDelta = 400; // Human single physical notch is ±120/±240; >=400 is hardware burst
        private static int s_chatterTimeMs = 80;   // Reversals under 80ms are mechanical contact bounce (sub-human physiological threshold)

        private static LowLevelMouseProc s_proc = HookCallback;
        private static IntPtr s_hookID = IntPtr.Zero;
        private static NotifyIcon s_trayIcon;
        private static int s_interceptCount = 0;
        private static int s_maxDelta = 0;
        private static MenuItem s_countItem;
        private static MenuItem s_autoStartItem;
        private static Control s_uiSyncControl; // Marshals all Win32 hooks and OS power events onto the UI message loop thread

        private static string s_baseDir;
        private static string s_logFilePath;
        private static string s_statusFilePath;

        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "MouseWheelGuard";

        // Finite-State Machine for Chatter / Contact Reversal Detection
        private static int s_lastDelta = 0;
        private static uint s_lastTime = 0;
        private static int s_consecutiveSameDir = 0;

        [STAThread]
        static void Main(string[] args)
        {
            ParseArguments(args);

            InitStorageDirectories();
            InitStatsFromLog();

            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                try {
                    File.AppendAllText(s_logFilePath, "\n[FATAL ERROR] " + e.ExceptionObject.ToString() + "\n", Encoding.UTF8);
                } catch { }
            };

            bool createdNew;
            using (var mutex = new Mutex(true, "Global_MouseWheelGuard_SingleInstance_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    // Already running; avoid multiple hooks competing on the low-level queue
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Create hidden control to anchor message pump thread
                s_uiSyncControl = new Control();
                IntPtr forceHandle = s_uiSyncControl.Handle;

                // Listen to Windows power events: Sleep / Suspend -> Resume
                SystemEvents.PowerModeChanged += (s, e) => {
                    if (e.Mode == PowerModes.Resume)
                    {
                        if (s_uiSyncControl != null && s_uiSyncControl.IsHandleCreated)
                        {
                            s_uiSyncControl.BeginInvoke(new Action(() => {
                                try
                                {
                                    Unhook();
                                    s_hookID = SetHook(s_proc);
                                    File.AppendAllText(
                                        s_logFilePath,
                                        string.Format("[{0}] [SYSTEM] System resumed from sleep. Low-level hook successfully restored (HookID={1}).\n",
                                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), s_hookID),
                                        Encoding.UTF8
                                    );
                                    SaveStatusJson(null, 0, null, false);
                                }
                                catch (Exception ex)
                                {
                                    File.AppendAllText(
                                        s_logFilePath,
                                        string.Format("[{0}] [ERROR] Failed to restore hook on resume: {1}\n",
                                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), ex.Message),
                                        Encoding.UTF8
                                    );
                                }
                            }));
                        }
                    }
                };

                // Initialize System Tray Menu
                ContextMenu contextMenu = new ContextMenu();
                s_countItem = new MenuItem(string.Format("🛡️ Blocked: {0} times (Peak: {1})", s_interceptCount, s_maxDelta));
                s_countItem.Enabled = false;
                contextMenu.MenuItems.Add(s_countItem);
                contextMenu.MenuItems.Add("-");

                MenuItem viewLogItem = new MenuItem("📄 View Audit Log", (s, e) => OpenLogFile());
                contextMenu.MenuItems.Add(viewLogItem);

                MenuItem clearLogItem = new MenuItem("🧹 Clear Audit Log", (s, e) => ClearLogFile());
                contextMenu.MenuItems.Add(clearLogItem);

                contextMenu.MenuItems.Add("-");

                s_autoStartItem = new MenuItem("Start on Windows Boot", ToggleAutoStart);
                s_autoStartItem.Checked = IsAutoStartEnabled();
                contextMenu.MenuItems.Add(s_autoStartItem);

                contextMenu.MenuItems.Add("Exit MouseWheelGuard", (s, e) => {
                    Unhook();
                    UpdateStatusOnExit();
                    if (s_trayIcon != null)
                    {
                        s_trayIcon.Visible = false;
                        s_trayIcon.Dispose();
                    }
                    Application.Exit();
                });

                s_trayIcon = new NotifyIcon
                {
                    Icon = SystemIcons.Shield,
                    ContextMenu = contextMenu,
                    Text = string.Format("MouseWheelGuard (Blocked: {0})", s_interceptCount),
                    Visible = true
                };

                s_trayIcon.DoubleClick += (s, e) => OpenLogFile();

                // Initial Hook Registration
                s_hookID = SetHook(s_proc);

                AppendLogHeader();
                SaveStatusJson(null, 0, null, false);

                // Run message loop
                Application.Run(new ApplicationContext());

                Unhook();
                UpdateStatusOnExit();
            }
        }

        private static void ParseArguments(string[] args)
        {
            if (args == null || args.Length == 0) return;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--threshold" || args[i] == "-t") && i + 1 < args.Length)
                {
                    int val;
                    if (int.TryParse(args[i + 1], out val) && val > 0)
                    {
                        s_thresholdDelta = val;
                        i++;
                    }
                }
                else if ((args[i] == "--chatter" || args[i] == "-c") && i + 1 < args.Length)
                {
                    int val;
                    if (int.TryParse(args[i + 1], out val) && val > 0)
                    {
                        s_chatterTimeMs = val;
                        i++;
                    }
                }
            }
        }

        private static void InitStorageDirectories()
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string localLogs = Path.Combine(exeDir, "logs");

            try
            {
                if (!Directory.Exists(localLogs)) Directory.CreateDirectory(localLogs);
                s_baseDir = localLogs;
            }
            catch
            {
                // Fallback to LocalAppData if running from Program Files or write-protected path
                s_baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MouseWheelGuard", "logs");
                if (!Directory.Exists(s_baseDir)) Directory.CreateDirectory(s_baseDir);
            }

            s_logFilePath = Path.Combine(s_baseDir, "mouse_wheel_guard.log");
            s_statusFilePath = Path.Combine(s_baseDir, "mouse_wheel_status.json");
        }

        private static void InitStatsFromLog()
        {
            try
            {
                if (File.Exists(s_logFilePath))
                {
                    string content = File.ReadAllText(s_logFilePath, Encoding.UTF8);
                    int count = 0;
                    int idx = 0;
                    while ((idx = content.IndexOf("[INTERCEPT]", idx)) != -1)
                    {
                        count++;
                        idx += 11;
                    }
                    if (count > 0) s_interceptCount = count;

                    string[] lines = content.Split('\n');
                    foreach (string l in lines)
                    {
                        if (l.Contains("Delta:"))
                        {
                            string[] parts = l.Split(':');
                            if (parts.Length > 1)
                            {
                                int d;
                                if (int.TryParse(parts[1].Trim(), out d))
                                {
                                    if (Math.Abs(d) > s_maxDelta) s_maxDelta = Math.Abs(d);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void AppendLogHeader()
        {
            try
            {
                string header = string.Format(
                    "================================================================================\n" +
                    "  MouseWheelGuard - Hardware Debounce & OS Low-Level Message Filter\n" +
                    "  Started At   : {0}\n" +
                    "  Hook ID      : {1}\n" +
                    "  Thresholds   : Burst >= {2} Delta | Chatter <= {3} ms\n" +
                    "  Log File     : {4}\n" +
                    "================================================================================\n\n",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    s_hookID,
                    s_thresholdDelta,
                    s_chatterTimeMs,
                    s_logFilePath
                );
                File.AppendAllText(s_logFilePath, header, Encoding.UTF8);
            }
            catch { }
        }

        private static void SaveStatusJson(string anomalyType, int delta, string targetApp, bool isInjected)
        {
            try
            {
                string nowIso = DateTime.UtcNow.ToString("o");
                string json;
                if (!string.IsNullOrEmpty(anomalyType))
                {
                    json = string.Format(
                        "{{\n" +
                        "  \"isRunning\": true,\n" +
                        "  \"pid\": {0},\n" +
                        "  \"hookId\": {1},\n" +
                        "  \"thresholdDelta\": {2},\n" +
                        "  \"chatterTimeMs\": {3},\n" +
                        "  \"updatedAt\": \"{4}\",\n" +
                        "  \"interceptCount\": {5},\n" +
                        "  \"maxDelta\": {6},\n" +
                        "  \"lastIntercept\": {{\n" +
                        "    \"time\": \"{7}\",\n" +
                        "    \"anomalyType\": \"{8}\",\n" +
                        "    \"delta\": {9},\n" +
                        "    \"app\": \"{10}\",\n" +
                        "    \"isInjected\": {11}\n" +
                        "  }}\n" +
                        "}}",
                        Process.GetCurrentProcess().Id,
                        s_hookID,
                        s_thresholdDelta,
                        s_chatterTimeMs,
                        nowIso,
                        s_interceptCount,
                        s_maxDelta,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        anomalyType,
                        delta,
                        targetApp ?? "Unknown",
                        isInjected ? "true" : "false"
                    );
                }
                else
                {
                    json = string.Format(
                        "{{\n" +
                        "  \"isRunning\": true,\n" +
                        "  \"pid\": {0},\n" +
                        "  \"hookId\": {1},\n" +
                        "  \"thresholdDelta\": {2},\n" +
                        "  \"chatterTimeMs\": {3},\n" +
                        "  \"updatedAt\": \"{4}\",\n" +
                        "  \"interceptCount\": {5},\n" +
                        "  \"maxDelta\": {6},\n" +
                        "  \"lastIntercept\": null\n" +
                        "}}",
                        Process.GetCurrentProcess().Id,
                        s_hookID,
                        s_thresholdDelta,
                        s_chatterTimeMs,
                        nowIso,
                        s_interceptCount,
                        s_maxDelta
                    );
                }
                File.WriteAllText(s_statusFilePath, json, Encoding.UTF8);
            }
            catch { }
        }

        private static void UpdateStatusOnExit()
        {
            try
            {
                string json = string.Format(
                    "{{\n" +
                    "  \"isRunning\": false,\n" +
                    "  \"pid\": null,\n" +
                    "  \"hookId\": null,\n" +
                    "  \"updatedAt\": \"{0}\",\n" +
                    "  \"interceptCount\": {1},\n" +
                    "  \"maxDelta\": {2}\n" +
                    "}}",
                    DateTime.UtcNow.ToString("o"),
                    s_interceptCount,
                    s_maxDelta
                );
                File.WriteAllText(s_statusFilePath, json, Encoding.UTF8);
            }
            catch { }
        }

        private static void OpenLogFile()
        {
            try
            {
                if (!File.Exists(s_logFilePath))
                {
                    File.WriteAllText(s_logFilePath, "No intercept logs recorded yet.\n", Encoding.UTF8);
                }
                Process.Start("notepad.exe", s_logFilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open log: " + ex.Message);
            }
        }

        private static void ClearLogFile()
        {
            try
            {
                File.WriteAllText(s_logFilePath, string.Format("Log cleared at {0}.\n\n", DateTime.Now), Encoding.UTF8);
                s_interceptCount = 0;
                s_maxDelta = 0;
                UpdateTrayStatus(0);
                SaveStatusJson(null, 0, null, false);
                MessageBox.Show("Audit log has been cleared successfully.", "MouseWheelGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to clear log: " + ex.Message);
            }
        }

        private static void UpdateTrayStatus(int delta)
        {
            if (s_countItem != null)
            {
                s_countItem.Text = string.Format("🛡️ Blocked: {0} times (Peak: {1})", s_interceptCount, s_maxDelta);
            }
            if (s_trayIcon != null)
            {
                s_trayIcon.Text = string.Format("MouseWheelGuard (Blocked: {0})", s_interceptCount);
            }
        }

        private static IntPtr SetHook(LowLevelMouseProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private static void Unhook()
        {
            if (s_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(s_hookID);
                s_hookID = IntPtr.Zero;
            }
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
            {
                MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                short delta = (short)((hookStruct.mouseData >> 16) & 0xffff);
                uint currentTime = hookStruct.time;
                bool isInjected = (hookStruct.flags & 1) != 0; // LLMHF_INJECTED: 1=synthetic/software, 0=genuine physical hardware

                bool shouldBlock = false;
                string blockReason = null;
                string anomalyType = null;

                // Layer 1: Hardware Packet Burst Filter (|delta| >= s_thresholdDelta, e.g. 400)
                if (Math.Abs((int)delta) >= s_thresholdDelta)
                {
                    shouldBlock = true;
                    anomalyType = "Hardware Packet Burst";
                    blockReason = string.Format(
                        "Single-packet wheel report was {0} (standard notch is ±120). Caused by encoder switch chatter or wireless buffer stacking.",
                        delta
                    );
                }
                // Layer 2: Mechanical Contact Chatter / Reverse Bounce (< s_chatterTimeMs)
                else if (s_consecutiveSameDir >= 1 && ((s_lastDelta > 0 && delta < 0) || (s_lastDelta < 0 && delta > 0)))
                {
                    uint diffTime = currentTime >= s_lastTime ? (currentTime - s_lastTime) : 0;
                    if (diffTime < (uint)s_chatterTimeMs)
                    {
                        shouldBlock = true;
                        anomalyType = "Encoder Contact Chatter";
                        blockReason = string.Format(
                            "Immediate reverse spike {0} occurred within {1} ms of scrolling. Physically impossible for human finger; matches contact bounce.",
                            delta, diffTime
                        );
                    }
                }

                // Update Finite-State Machine
                if (!shouldBlock)
                {
                    if ((s_lastDelta > 0 && delta > 0) || (s_lastDelta < 0 && delta < 0))
                    {
                        s_consecutiveSameDir++;
                    }
                    else
                    {
                        s_consecutiveSameDir = 1;
                    }
                    s_lastDelta = delta;
                    s_lastTime = currentTime;
                }
                else
                {
                    // Intercept & Audit
                    s_interceptCount++;
                    if (Math.Abs((int)delta) > s_maxDelta) s_maxDelta = Math.Abs((int)delta);

                    UpdateTrayStatus(delta);

                    string processName = "Unknown";
                    string windowTitle = "Unknown";
                    try
                    {
                        IntPtr hWnd = GetForegroundWindow();
                        if (hWnd != IntPtr.Zero)
                        {
                            uint pid;
                            GetWindowThreadProcessId(hWnd, out pid);
                            if (pid > 0)
                            {
                                Process proc = Process.GetProcessById((int)pid);
                                processName = proc.ProcessName;
                            }
                            StringBuilder sb = new StringBuilder(256);
                            if (GetWindowText(hWnd, sb, 256) > 0)
                            {
                                windowTitle = sb.ToString();
                            }
                        }
                    }
                    catch { }

                    SaveStatusJson(anomalyType, (int)delta, processName, isInjected);

                    try
                    {
                        string logEntry = string.Format(
                            "[{0}] [INTERCEPT] Type: {1}\n" +
                            "  - Delta Value: {2}\n" +
                            "  - Target Process: {3} (Window: \"{4}\")\n" +
                            "  - Hardware Origin: {5}\n" +
                            "  - Root Cause: {6}\n" +
                            "  - Action Taken: Dropped low-level message (return IntPtr.Zero+1)\n\n",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                            anomalyType,
                            delta,
                            processName,
                            windowTitle,
                            isInjected ? "Software/Synthetic (Injected)" : "Genuine Physical Hardware (Non-Injected)",
                            blockReason
                        );
                        File.AppendAllText(s_logFilePath, logEntry, Encoding.UTF8);
                    }
                    catch { }

                    // Returning non-zero consumes the message, stopping it from propagating to Windows and target applications
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(s_hookID, nCode, wParam, lParam);
        }

        #region Windows Boot Auto-Start Registration

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(APP_NAME);
                        return val != null;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void ToggleAutoStart(object sender, EventArgs e)
        {
            try
            {
                bool currentState = IsAutoStartEnabled();
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (key != null)
                    {
                        if (currentState)
                        {
                            key.DeleteValue(APP_NAME, false);
                            s_autoStartItem.Checked = false;
                            MessageBox.Show("Auto-start disabled.", "MouseWheelGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            string exePath = Assembly.GetExecutingAssembly().Location;
                            key.SetValue(APP_NAME, "\"" + exePath + "\"");
                            s_autoStartItem.Checked = true;
                            MessageBox.Show("Auto-start enabled! MouseWheelGuard will silently protect your wheel on system boot.", "MouseWheelGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to toggle auto-start: " + ex.Message, "MouseWheelGuard Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region Win32 P/Invoke Definitions

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        #endregion
    }
}
