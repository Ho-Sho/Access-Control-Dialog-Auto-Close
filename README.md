# Access Control Dialog Auto-Closer

Automatically closes the recurring **"Access Control Not Enabled"** dialog in Q-SYS Designer, so it stops interrupting development work.

## Why

When a Q-SYS Core runs in unprotected mode, Designer repeatedly pops up an "Access Control Not Enabled" dialog. This tool watches for that specific window and closes it for you.

## How it works

- Uses `SetWinEventHook` to react the instant the dialog appears — **no polling loop**, near-zero CPU usage while idle.
- Watches only Q-SYS Designer's own windows (scoped by process id), not the whole system.
- Re-checks every 15 seconds whether Designer is running, so it keeps working across Designer restarts.
- Lives in the system tray (hover text: "Access Control Dialog Auto-Closer"). Right-click → **Exit** to quit.
- Runs as Administrator (`app.manifest`) because Designer itself typically runs elevated — Windows blocks cross-privilege window-close messages (UIPI) otherwise.
- Built-in diagnostic logging exists but is **disabled by default** (`LoggingEnabled = false` in the source) — flip it to `true` and rebuild to debug.

## Requirements

- Windows 10/11
- .NET Framework 4.x (already included in Windows — no extra install needed)

## Build

No IDE or SDK required. Double-click `build.bat`, or run manually from a Command Prompt:

```bat
csc.exe /target:winexe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  /win32icon:AccessControlDialogAutoClose.ico /win32manifest:app.manifest ^
  /resource:AccessControlDialogAutoClose.ico,app.ico ^
  /out:AccessControlDialogAutoClose.exe AccessControlDialogAutoClose.cs
```

## Usage

1. Run `AccessControlDialogAutoClose.exe` and approve the UAC prompt (admin rights are required — see above).
2. It sits quietly in the tray until the dialog appears, then closes it automatically.
3. To run it at every login, put a shortcut to the exe in `shell:startup`.

## ⚠️ Disclaimer

For **development use only** — do not run this on production systems. It force-closes a specific dialog window; it does not fix the underlying Access Control setting, which should be properly configured on any production Core.
