using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// -------------------------------------------------------------------------
// Automatically closes the "Access Control Not Enabled" dialog from
// Q-Sys Designer without polling, using SetWinEventHook.
// Runs in the system tray; right-click the icon and choose "Exit" to quit.
//
// Build: double-click build.bat (uses the csc.exe bundled with Windows).
// Keep AccessControlDialogAutoClose.ico in the same folder; it is embedded into
// the exe both as the file icon and as the tray icon resource ("app.ico").
//   csc.exe /target:winexe /reference:System.Windows.Forms.dll
//           /reference:System.Drawing.dll
//           /win32icon:AccessControlDialogAutoClose.ico
//           /resource:AccessControlDialogAutoClose.ico,app.ico
//           /out:AccessControlDialogAutoClose.exe AccessControlDialogAutoClose.cs
//
// FOR DEVELOPMENT USE ONLY - do not run this on production systems.
// -------------------------------------------------------------------------
internal static class Program
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const uint WM_CLOSE = 0x0010;
    private const string TargetTitlePart = "Access Control Not Enabled";
    private const string TargetProcessName = "Q-Sys Designer";

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // Keep a static reference so the delegate is never garbage-collected
    // while the unmanaged hook still holds a pointer to it.
    private static readonly WinEventDelegate ProcDelegate = WinEventProc;

    private static IntPtr _hook = IntPtr.Zero;

    // Process id the hook is currently scoped to (0 = not currently hooked to
    // anything, because Designer was not running last time we checked).
    private static uint _hookedPid = 0;

    // Runs for the lifetime of this tool, at a very low frequency (nothing
    // like the original 500ms polling). It does not watch for the dialog
    // itself - it only notices when Q-Sys Designer starts, exits, or is
    // restarted with a new process id, and re-points the hook accordingly.
    private static System.Windows.Forms.Timer _watchTimer;

    // Diagnostic log used while tracking down the Cybereason/elevation issue.
    // Disabled by default now that the tool works - flip this to true (and
    // rebuild) to turn logging back on if something needs debugging again.
    private const bool LoggingEnabled = false;

    // Overwritten fresh on every run, only when LoggingEnabled is true.
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "AccessControlDialogAutoClose.log");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    private static void Main()
    {
        // Opt in to DPI awareness so the tray picks a sharp icon size
        // (24px/32px) on scaled displays instead of stretching 16px.
        SetProcessDPIAware();

        // Allow only one instance (e.g. autostart + manual launch).
        bool createdNew;
        using (var mutex = new Mutex(true, "CloseAccessControlDialog_SingleInstance", out createdNew))
        {
            if (!createdNew)
                return;

            if (LoggingEnabled)
            {
                try { File.Delete(LogPath); } catch { }
                Log("Started. Process integrity: " + (IsElevated() ? "elevated (admin)" : "standard user"));
            }

            // Watch only Q-Sys Designer's own windows rather than every window on
            // the system - narrower in scope, and re-checked periodically so a
            // Designer restart (new process id) is picked up automatically.
            RefreshHookTarget();
            StartWatchTimer();

            // Tray icon with a right-click menu containing only "Exit".
            var menu = new ContextMenuStrip();
            if (LoggingEnabled)
            {
                menu.Items.Add("Open Log", null, delegate(object sender, EventArgs e)
                {
                    try { Process.Start(LogPath); } catch { }
                });
            }
            menu.Items.Add("Exit", null, delegate(object sender, EventArgs e)
            {
                Application.Exit();
            });

            var tray = new NotifyIcon();
            tray.Icon = LoadTrayIcon();
            tray.Text = "Access Control Dialog Auto-Closer";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            // The message loop also drives the WinEvent hook callback.
            // It blocks until Windows has something to dispatch, so CPU use
            // is essentially zero while idle.
            Application.Run();

            // Cleanup: remove the hook, timer and tray icon (avoids a ghost icon).
            if (_watchTimer != null)
            {
                _watchTimer.Stop();
                _watchTimer.Dispose();
            }

            if (_hook != IntPtr.Zero)
                UnhookWinEvent(_hook);

            tray.Visible = false;
            tray.Dispose();
        }
    }

    // Looks up Q-Sys Designer's process id if it is currently running.
    // Returns 0 if it is not found (SetWinEventHook treats 0 as "all processes").
    private static uint FindDesignerProcessId()
    {
        Process[] procs = Process.GetProcessesByName(TargetProcessName);
        try
        {
            return procs.Length > 0 ? (uint)procs[0].Id : 0;
        }
        finally
        {
            foreach (Process p in procs)
                p.Dispose();
        }
    }

    // (Re)installs the WinEvent hook scoped to a single process id. Passing 0
    // clears the hook entirely (used when Designer is not currently running) -
    // this tool never falls back to watching every window on the system.
    private static void HookProcess(uint pid)
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        if (pid == 0)
        {
            Log("Q-Sys Designer is not running - hook cleared, watching for it to (re)start.");
            return;
        }

        _hook = SetWinEventHook(
            EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero,
            ProcDelegate, pid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
            Log("SetWinEventHook FAILED for pid=" + pid + " (Win32 error " + Marshal.GetLastWin32Error() + ")");
        else
            Log("Hook installed, scope = pid " + pid);
    }

    // Compares the currently running Designer process (if any) against what
    // the hook is scoped to, and re-points the hook whenever that changed -
    // Designer just started, just exited, or restarted with a new process id.
    private static void RefreshHookTarget()
    {
        uint pid = FindDesignerProcessId();
        if (pid == _hookedPid && _hook != IntPtr.Zero)
            return;

        HookProcess(pid);
        _hookedPid = pid;
    }

    private static bool IsElevated()
    {
        try
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string message)
    {
        if (!LoggingEnabled)
            return;

        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }
        catch
        {
            // ignore logging failures - diagnostics should never crash the app
        }
    }

    // Re-checks every 15 seconds for the rest of this tool's life - a cheap
    // process-name lookup, nothing like the original 500ms dialog-polling
    // loop - so a Designer restart is always picked up within a few seconds.
    private static void StartWatchTimer()
    {
        _watchTimer = new System.Windows.Forms.Timer();
        _watchTimer.Interval = 15000;
        _watchTimer.Tick += delegate(object sender, EventArgs e)
        {
            RefreshHookTarget();
        };
        _watchTimer.Start();
    }

    // Loads the multi-size .ico embedded in this exe and lets Windows pick the
    // image that best matches the current tray icon size.
    // Falls back to a generic system icon if the resource is missing.
    private static Icon LoadTrayIcon()
    {
        try
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            {
                if (stream != null)
                    return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }
        catch
        {
            // ignore and use the fallback below
        }
        return SystemIcons.Application;
    }

    private static void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Ignore events for child controls (toolbar buttons, list items, etc.)
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero)
            return;

        var titleBuilder = new StringBuilder(256);
        GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
        string title = titleBuilder.ToString();

        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        string procName = "(unknown)";
        try
        {
            Process proc = Process.GetProcessById((int)pid);
            procName = proc.ProcessName;
            proc.Dispose();
        }
        catch
        {
            // process may have already exited; keep procName as "(unknown)"
        }

        // Logged for every top-level window so we can see whether the hook is
        // receiving anything at all, even when nothing below ends up matching.
        Log("Window shown: title=\"" + title + "\" process=" + procName);

        if (title.IndexOf(TargetTitlePart, StringComparison.Ordinal) < 0)
            return;

        if (!procName.Equals(TargetProcessName, StringComparison.OrdinalIgnoreCase))
        {
            Log("  -> title matched but process name differs ('" + procName + "' != '" + TargetProcessName + "'), skipped.");
            return;
        }

        Log("  -> MATCH. Sending WM_CLOSE...");

        // WM_CLOSE is what the title bar "X" button ends up sending.
        bool sent = PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (!sent)
            Log("  -> PostMessage FAILED (Win32 error " + Marshal.GetLastWin32Error() + ")");
        else
            Log("  -> PostMessage sent OK.");
    }
}
