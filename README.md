<div align="center">

<img src="limit.png" width="72" alt="Codex Usage Tray icon">

# Codex Usage Tray

**Windows taskbar widget for your Codex usage limits**

`Windows` `Electron` `Codex CLI`

Compact, local, and always within reach.

</div>

---

> Keep your Codex allowance in view without opening a browser or digging through settings.

Codex Usage Tray places your remaining **5-hour** and **weekly** allowance immediately above the Windows taskbar. It remembers where you place it and refreshes automatically every minute.

| In the widget | In the background |
| --- | --- |
| Remaining 5-hour allowance | Refreshes every 60 seconds |
| Remaining weekly allowance | Shows each reset time |
| Current Codex CLI account | Uses the official local Codex integration |

## Why It Fits

| | |
| --- | --- |
| **Taskbar-native** | A frameless widget stays flush with the Windows taskbar and can be dragged horizontally. |
| **Low friction** | The tray menu provides refresh, visibility, usage dashboard, and quit actions. |
| **Own your sign-in** | When needed, `Auth login` opens Codex's own authentication flow. |
| **Local by design** | The app uses the official local Codex app-server and stores only widget placement. |

## Get Started

### Option A: Install from a terminal

**Requirements:** Windows and [Node.js](https://nodejs.org/)

```powershell
npm install
npm start
```

### Option B: Start by double-clicking

Share the complete project folder with someone who has Node.js installed, then double-click `start-widget.vbs`.

| First run | Later runs |
| --- | --- |
| Opens a command window and runs `npm install` | Starts the widget silently |

The widget appears just above the taskbar. Right-click it to access its menu.

## Connect Codex

If the Codex CLI is not authenticated, right-click the widget and select **Auth login**. Codex opens its own `codex login` flow; this application never asks for credentials.

If the CLI is missing, the widget installs the official package automatically:

```powershell
npm install -g @openai/codex
```

## Start With Windows

After its initial dependency installation, `start-widget.vbs` launches without leaving a terminal window open.

To run it when you sign in, either add a Windows Run entry pointing to its full path or place a shortcut in the Startup folder:

```text
shell:startup
```

## Privacy

Usage data is read only through the official local Codex app-server:

```text
account/rateLimits/read
```

The widget does **not** read:

- `%USERPROFILE%\.codex\auth.json`
- Browser cookies
- ChatGPT web endpoints

Codex owns authentication. The widget never collects credentials.

## Configuration

Most installations need no configuration. If Codex lives outside the standard location, set `CODEX_BINARY` to its executable before launching the widget:

```powershell
$env:CODEX_BINARY = "C:\path\to\codex.exe"
npm start
```

## For Contributors

```powershell
# Syntax-check the Electron main process
npm run check
```

| Path | Responsibility |
| --- | --- |
| `src/main.cjs` | Electron lifecycle, tray, widget placement, and Codex client |
| `src/widget.html` | Compact widget interface |
| `src/widget-preload.cjs` | Safe IPC bridge for the renderer |
| `start-widget.vbs` | Windows launcher with first-run dependency setup |
