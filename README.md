<div align="center">

<img src="https://github.com/naksipayila/codex-tracker/raw/refs/heads/main/src/limit.png" width="72" alt="Codex Usage Tray icon">

# Codex Usage Tray

**A compact Windows taskbar widget for Codex usage limits**

`Windows` `Electron` `Codex CLI`

See your remaining allowance without opening a browser.

</div>

---

Codex Usage Tray displays your remaining **5-hour** and **weekly** Codex allowance immediately adjacent to the Windows taskbar. It refreshes every minute, updates when Codex reports a rate-limit change, and remembers its horizontal placement.

| The widget shows | The app does |
| --- | --- |
| Remaining 5-hour allowance and reset time | Refreshes every 60 seconds |
| Remaining weekly allowance and reset time | Listens for Codex limit updates |
| `--` until Codex is connected | Uses the official local Codex integration |

## Features

| | |
| --- | --- |
| **Taskbar-aware** | A frameless, always-on-top widget is placed flush with the primary display's Windows taskbar edge. |
| **Draggable** | Drag anywhere on the widget to choose its horizontal position. The position is restored next time. |
| **Tray controls** | Show the widget, reset its position, refresh limits, open the official dashboard, or quit. |
| **Codex-owned auth** | When signed out, the widget menu exposes `Auth login`, which starts the official `codex login` flow. |
| **Local integration** | Limits come from the local Codex app-server, not from browser sessions or web scraping. |

## Requirements

- Windows
- [Node.js](https://nodejs.org/) with npm available on `PATH`
- Internet access on the first run, to install project dependencies and, if necessary, the Codex CLI

This is a source project, not a packaged `.exe`.

## Start the Widget

### From a terminal

```powershell
npm install
npm start
```

### By double-clicking

Share the complete project folder, including `src`, `package.json`, `package-lock.json`, and `start-widget.vbs`. Then double-click `start-widget.vbs`.

| First run | Later runs |
| --- | --- |
| Opens a command window and runs `npm install` | Starts the widget silently |

If dependency installation fails, the launcher shows an error message instead of starting Electron.

## Connect Codex

On startup, the app checks whether the Codex CLI is available and signed in.

1. If Codex is missing, the app installs the official package automatically.
2. If you are signed out, the widget keeps showing `--`; it does not open a sign-in prompt on its own.
3. Right-click the widget and choose **Auth login**.
4. Complete the official Codex CLI login flow. The widget polls for the completed login and then loads the limits.

The automatic installation command is:

```powershell
npm install -g @openai/codex
```

`Auth login` is shown only while Codex is signed out. The app does not collect credentials.

## Controls

| Where | Available actions |
| --- | --- |
| Widget right-click | `Auth login` when needed, hide widget, refresh, open usage dashboard, quit |
| Tray icon right-click | Show widget, reset widget position, refresh, open usage dashboard, quit |
| Widget drag | Move the widget horizontally |

## Start With Windows

After the initial dependency installation, `start-widget.vbs` launches without leaving a terminal window open.

To run it when you sign in, add a Windows Run entry pointing to the full path of `start-widget.vbs`, or place a shortcut in the Startup folder:

```text
shell:startup
```

## Data and Privacy

Usage data is read from the official local Codex app-server method:

```text
account/rateLimits/read
```

The widget does **not** read:

- `%USERPROFILE%\.codex\auth.json`
- Browser cookies
- ChatGPT web endpoints

The usage-dashboard menu action explicitly opens the official Codex usage page in your browser. Codex owns authentication; the widget never collects credentials.

The app persists only the widget's horizontal position in Electron user data as `widget-position.json`.

## Configuration

Most installations need no configuration. Codex discovery supports the standalone install, the Windows npm global install, and `codex` on `PATH`. To use another executable, set `CODEX_BINARY` before launching the widget:

```powershell
$env:CODEX_BINARY = "C:\path\to\codex.exe"
npm start
```

## Development

```powershell
npm run check
```

| Path | Responsibility |
| --- | --- |
| `src/main.cjs` | Electron lifecycle, taskbar placement, tray and menus, Codex client, CLI discovery, installation, and login |
| `src/widget.html` | 320 x 42 taskbar widget interface and drag interaction |
| `src/widget-preload.cjs` | Restricted renderer IPC bridge |
| `src/limit.png` | Tray icon source |
| `start-widget.vbs` | Windows launcher with visible first-run dependency setup |
