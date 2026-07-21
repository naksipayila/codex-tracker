# Codex Tracker

Codex Tracker is a Windows desktop widget for viewing Codex usage limits and Latrix telemetry from the taskbar.

## Features

- Shows 6-hour and weekly usage limits in the taskbar.
- Displays team telemetry, active users, models, tokens, and effort levels.
- Runs from the system tray and supports Windows startup settings.
- Supports verified updates through GitHub Releases.

## Install

Download `CodexTracker.exe` from the [latest release](https://github.com/naksipayila/codex-tracker/releases/latest) and run it on Windows.

The app reads the Latrix API key from one of these OpenCode configuration files:

```text
%USERPROFILE%\.config\opencode\opencode.json
%APPDATA%\opencode\opencode.json
```

The `.jsonc` equivalent is also supported.

The key is expected at `provider.latrix.options.apiKey`.

## Development

Requirements: Windows and .NET SDK `8.0.423`.

```powershell
dotnet restore src/CodexUsageTray.csproj
dotnet run --project src/CodexUsageTray.csproj
dotnet build src/CodexUsageTray.csproj --configuration Release
dotnet run --project tests/native/CodexUsageTray.NativeTests.csproj --configuration Release
```

To create a self-contained executable:

```powershell
powershell -ExecutionPolicy Bypass -File src/launcher/build.ps1 -OutputPath CodexTracker.exe -SelfContained -Version 1.0.27
```
