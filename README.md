<div align="center">

# Codex Usage Tray

**Windows taskbar widget for your Codex usage limits**

Compact. Local. Always within reach.

</div>

---

Codex Usage Tray places your remaining **5-hour** and **weekly** Codex allowance immediately above the Windows taskbar. It stays out of the way, remembers its position, and refreshes automatically every minute.

| What it shows | How it works |
| --- | --- |
| Remaining 5-hour allowance | Refreshes every 60 seconds |
| Remaining weekly allowance | Shows the reset time for each limit |
| Current Codex CLI account | Uses the official local Codex integration |

## Highlights

- Taskbar-adjacent, frameless widget that can be dragged horizontally
- Windows tray menu for refresh, visibility, usage dashboard, and quit actions
- Built-in `Auth login` action when Codex is not signed in
- Automatically installs the official Codex CLI if it is missing
- Starts silently through `start-widget.vbs`
- Stores only the widget position on the local machine

## Quick Start

### 1. Install prerequisites

- Windows
- [Node.js](https://nodejs.org/)

### 2. Install and run

```powershell
npm install
npm start
```

The widget appears just above the taskbar. Right-click it to access its menu.

## Sign In

If the Codex CLI is not authenticated, right-click the widget and select **Auth login**. This opens Codex's own `codex login` flow. Complete sign-in there, then restart the widget if the limits do not appear immediately.

The application installs the official CLI with the following command when necessary:

```powershell
npm install -g @openai/codex
```

## Run at Windows Sign-In

`start-widget.vbs` starts the widget without leaving a terminal window open.

To launch it when you sign in, create a Windows Run entry that points to the full path of `start-widget.vbs`, or place a shortcut to this file in the Startup folder:

```text
shell:startup
```

## Privacy and Data

Usage data is read only from the official local `codex app-server` method:

```text
account/rateLimits/read
```

The widget does **not** read:

- `%USERPROFILE%\.codex\auth.json`
- Browser cookies
- ChatGPT web endpoints

Codex owns the authentication process. The widget never collects credentials.

## Configuration

Most installations need no configuration. If Codex is installed in a non-standard location, point `CODEX_BINARY` to `codex.exe` before starting the widget:

```powershell
$env:CODEX_BINARY = "C:\path\to\codex.exe"
npm start
```

## Development

```powershell
# Syntax-check the Electron main process
npm run check
```

| Path | Purpose |
| --- | --- |
| `src/main.cjs` | Electron lifecycle, tray, widget placement, and Codex client |
| `src/widget.html` | Compact widget interface |
| `src/widget-preload.cjs` | Safe IPC bridge for the renderer |
| `start-widget.vbs` | Hidden Windows launcher |
