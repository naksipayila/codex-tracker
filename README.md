# Codex Tracker

Windows taskbar widget for Latrix 6-hour and weekly usage limits.

## Install

Download and run `CodexTracker.exe` on Windows x64. Releases install for the current user under `%LOCALAPPDATA%\Programs\Codex Tracker`.
Release installs update automatically; Git clones update in place on a clean `main` checkout.

## Controls

- Drag the widget to reposition it.
- Use **Launch at Windows startup** for automatic startup.
- Use **Hide widget** or **Quit** from the widget or tray menu.

## Development

```text
dotnet run --project tools\CodexTracker.Tooling\CodexTracker.Tooling.csproj --configuration Release -- build --output .\CodexTracker.exe
dotnet run --project tools\CodexTracker.Tooling\CodexTracker.Tooling.csproj --configuration Release -- check
dotnet run --project tools\CodexTracker.Tooling\CodexTracker.Tooling.csproj --configuration Release -- release
```
