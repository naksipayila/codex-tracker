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

1. Install [Node.js 22 or newer](https://nodejs.org/).
2. Double-click `start-widget.exe` in the repository root.
3. On first launch, the launcher installs local dependencies silently, then opens the widget without a terminal window.
4. If limits are not available, right-click the widget and choose **Auth login** to complete the official Codex CLI sign-in.

The widget stays flush with the Windows taskbar. Drag anywhere on it to change its horizontal position; the location is remembered for the current Windows user.

Automatic updates require a Git clone on a clean `main` branch whose `origin` points to the official `naksipayila/codex-tracker` GitHub repository.

## System tray menu

| Action | What it does |
| --- | --- |
| **Show widget** | Shows the taskbar widget if it was hidden. |
| **Check update at startup** | Enabled by default. Silently checks `origin/main` when the widget starts, then shows the available changes and asks before updating. |
| **Repair update** | Appears when a running app detects that dependency installation or verification still needs to finish. |
| **Quit** | Stops the widget and tray app. |

The Git update only accepts a fast-forward and never discards user changes. Dependencies are installed deterministically with `npm ci`. If a clean update transaction fails after the fast-forward, the updater restores its own changes to the previous commit; if any external repository change is detected, rollback stops without modifying it.

Updates run through a temporary copy of the C# launcher. After confirmation, a compact progress window shows each update stage while the widget is closed. The target launcher must pass its compatibility self-test, and the restarted widget must report ready and remain alive before the transaction completes. Clone-specific coordination state and `update.log` diagnostics are stored under the current user's local application data, outside the repository.

If an update is interrupted, `start-widget.exe` reads the atomic phase journal and resumes the pending transaction on the next launch. Legacy pending markers from earlier versions are also supported.

## Widget right-click menu

| Action | What it does |
| --- | --- |
| **Auth login** | Opens the official `codex login` flow when the CLI is not signed in. |
| **Add to Windows startup** | Starts the hidden launcher when you sign in to Windows. |
| **Refresh now** | Reads the latest Codex limits immediately. |
| **Hide widget** | Keeps the tray app running while hiding the taskbar widget. |
| **Open Codex usage dashboard** | Opens the Codex usage page in your browser. |

## How it works

Limits are read locally through the official `codex app-server` interface. The app does not read browser cookies, ChatGPT web endpoints, or Codex authentication files directly.

If the Codex CLI is missing, the app installs `@openai/codex` globally and then uses its local app-server connection.

## Development

```powershell
npm --prefix src ci
npm --prefix src start
```

Run the source, package metadata, and launcher format checks with:

```powershell
npm --prefix src run check
```

Rebuild the launcher after changing its C# source, build script, or icon. The build embeds a canonical hash of all launcher build inputs for the updater compatibility check:

```powershell
.\src\launcher\build.ps1
```

Run the Windows updater success and rollback fault-injection tests with:

```powershell
.\tests\updater\Invoke-UpdaterTests.ps1 -SourceRoot (Resolve-Path .) -ScratchRoot (Join-Path $env:TEMP "codex-updater-tests")
```

The Electron main process is `src/main.cjs`; the renderer is `src/widget.html` and receives capabilities only through `src/widget-preload.cjs`.
