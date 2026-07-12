<p align="center">
  <img src="src/icon.png" width="96" alt="Codex Usage Tray icon">
</p>

<h1 align="center">Codex Usage Tray</h1>

<p align="center">
  A compact Windows taskbar widget for your Codex usage limits.
</p>

<p align="center">
  <strong>5-hour and weekly limits</strong> &middot; <strong>Background launcher</strong> &middot; <strong>Windows startup support</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows-0078D4?style=flat-square" alt="Windows">
  <img src="https://img.shields.io/badge/runtime-Electron-47848F?style=flat-square" alt="Electron">
  <img src="https://img.shields.io/badge/data-Codex%20app--server-2DD4A2?style=flat-square" alt="Codex app-server">
</p>

## Quick start

1. Install [Node.js](https://nodejs.org/).
2. Double-click `start-widget.exe` in the repository root.
3. On first launch, the launcher installs local dependencies silently, then opens the widget without a terminal window.
4. If limits are not available, right-click the widget and choose **Auth login** to complete the official Codex CLI sign-in.

The widget stays flush with the Windows taskbar. Drag anywhere on it to change its horizontal position; the location is remembered for the current Windows user.

## Right-click menu

| Action | What it does |
| --- | --- |
| **Auth login** | Opens the official `codex login` flow when the CLI is not signed in. |
| **Add to Windows startup** | Starts the hidden launcher when you sign in to Windows. |
| **Refresh now** | Reads the latest Codex limits immediately. |
| **Hide widget** | Keeps the tray app running while hiding the taskbar widget. |
| **Open Codex usage dashboard** | Opens the Codex usage page in your browser. |

## How it works

Limits are read locally through the official `codex app-server` interface. The app does not read browser cookies, ChatGPT web endpoints, or Codex authentication files directly.

If the Codex CLI is missing, the app installs `@openai/codex` globally and then uses its local app-server connection. The tray context menu also provides **Quit** when you want to stop the app completely.

## Development

```powershell
npm --prefix src install
npm --prefix src start
```

Run the focused syntax check with:

```powershell
npm --prefix src run check
```

The Electron main process is `src/main.cjs`; the renderer is `src/widget.html` and receives capabilities only through `src/widget-preload.cjs`.
