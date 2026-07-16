<h1 align="center">Codex Tracker</h1>

<p align="center">
  A compact native Windows taskbar widget for your Codex usage limits.
</p>

<p align="center">
  <strong>5-hour and weekly limits</strong> &middot; <strong>Native WPF</strong> &middot; <strong>Windows startup support</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows-0078D4?style=flat-square" alt="Windows">
  <img src="https://img.shields.io/badge/runtime-.NET%208%20WPF-512BD4?style=flat-square" alt=".NET 8 WPF">
  <img src="https://img.shields.io/badge/data-Codex%20app--server-2DD4A2?style=flat-square" alt="Codex app-server">
</p>

## Quick start

1. Install the [.NET 8 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/8.0) if it is not already installed.
2. Double-click `Codex Tracker.exe` in the repository root.
3. If limits are not available, right-click the widget and choose **Sign in to Codex**.

The application is a framework-dependent, single-file native executable. Electron, browser processes, npm dependencies, and Node.js are not used. If Codex CLI is missing, the application runs OpenAI's official PowerShell standalone installer.

The widget stays flush with the primary Windows taskbar. Drag anywhere except the arrow control to change its horizontal position; the location is remembered for the current Windows user.

Automatic updates require a Git clone on a clean `main` branch whose `origin` points to the official `naksipayila/codex-tracker` GitHub repository.

## Step-by-step usage

1. Download `Codex Tracker.exe` from the repository file list and run it.
2. The widget appears against the Windows taskbar and the app adds an icon to the system tray.
3. Right-click the widget, choose **Sign in to Codex**, and complete the official `codex login` flow when prompted.
4. When sign-in finishes, the widget shows your current 5-hour and weekly usage limits. Right-click it and choose **Refresh** to request an immediate update.
5. Drag any part of the widget except its arrow control to move it horizontally along the taskbar. The chosen position is saved for your Windows user account.
6. Right-click the tray icon to show a hidden widget, toggle startup update checks, or quit the application.
7. Right-click the widget and select **Launch at Windows startup** if you want it to start automatically after you sign in to Windows.
8. Use **Hide in fullscreen apps** to keep the widget out of the way while another application covers its monitor. Choose **Hide widget** to remove it temporarily while leaving the tray icon running.

## System tray menu

| Action | What it does |
| --- | --- |
| **Show widget** | Shows the taskbar widget if it was hidden. |
| **Check update at startup** | Silently checks `origin/main` when the application starts. |
| **Repair update** | Appears when an interrupted update needs recovery. |
| **Quit** | Stops the widget and tray application. |

Updates accept only a fast-forward and never discard external repository changes. A temporary copy of the same C# executable performs the transaction, validates the updated native binary, requires the restarted application to report ready and remain stable, and rolls back clean failed transactions with `git reset --keep`.

Repository-scoped update journals and diagnostics are stored under `%LOCALAPPDATA%\CodexUsageTray\updates`. Interrupted transactions resume on the next launch.

## Widget menu

| Action | What it does |
| --- | --- |
| **Open Codex usage dashboard** | Opens the Codex usage page in the default browser. |
| **Sign in to Codex** | Starts the official `codex login` flow. |
| **Launch at Windows startup** | Updates the current user's Windows Run registry value. |
| **Hide in fullscreen apps** | Hides the widget while another application covers its monitor. |
| **Hide widget** | Keeps the tray application running while hiding the widget. |
| **Quit** | Stops the application. |

## How it works

Limits are read only through the local official `codex app-server` method `account/rateLimits/read`. The application does not read browser cookies, ChatGPT web endpoints, or Codex authentication files.

The C# app-server client uses CRLF-delimited JSONL on Windows, drains both output streams, responds to rate-limit update notifications, and refreshes every 30 seconds. CLI discovery preserves `CODEX_BINARY`, official standalone Codex releases, and `codex.exe` on `PATH`. If the CLI is missing, the application runs `powershell -ExecutionPolicy Bypass -Command "irm https://chatgpt.com/codex/install.ps1 | iex"`; the official installer verifies release SHA-256 metadata before activation.

`Codex Tracker.exe` has four native modes:

| Mode | Purpose |
| --- | --- |
| Normal | Runs the WPF widget, tray, Codex client, settings, and update discovery. |
| `--pin-hwnd` | Keeps the widget above the Windows taskbar and handles fullscreen visibility. |
| `--update` | Runs the transactional update and rollback progress window from a temporary copy. |
| `--self-test` | Verifies the native updater protocol and canonical build-input hash. |

## Development

Restore and build the native application:

```powershell
dotnet restore .\src\CodexUsageTray.csproj
dotnet build .\src\CodexUsageTray.csproj --configuration Release
```

Publish the tracked root executable with its canonical input hash:

```powershell
.\src\launcher\build.ps1
```

Run source, unit-test, and tracked executable checks:

```powershell
.\src\check.ps1
```

Run updater success and rollback fault-injection tests:

```powershell
.\tests\updater\Invoke-UpdaterTests.ps1 `
  -SourceRoot (Resolve-Path .) `
  -ScratchRoot (Join-Path $env:TEMP "codex-native-updater-tests")
```

The main native application code is under `src/*.cs`. `src/launcher/Program.cs` owns process modes, transactional updates, Job Object containment, and the Win32 pinning helper. The release workflow builds with .NET 8 and does not install Electron or npm dependencies.
