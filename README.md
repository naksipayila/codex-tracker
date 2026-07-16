<h1 align="center">Codex Tracker</h1>

Windows taskbar widget for viewing your Latrix 6-hour and weekly usage limits.

## Usage

1. Download and run `CodexTracker.exe` on Windows x64.
2. Add the Latrix API key to the OpenCode global config at `~/.config/opencode/opencode.json`, under `provider.latrix.options.apiKey`.
3. Your Latrix limits appear in the widget automatically.

## Updates

Release packages publish a self-contained `CodexTracker.exe` from version tags. Git-clone installations also use the repository updater and require the `main` branch with no local changes.

Latrix usage reads the API key from the OpenCode global config at `~/.config/opencode/opencode.json`, under `provider.latrix.options.apiKey`. Codex Tracker does not ask for, copy, or store this key.

## Development

Build and verification commands are implemented in the C# tooling project:

```text
dotnet run --project tools\CodexTracker.Tooling\CodexTracker.Tooling.csproj --configuration Release -- build --output .\CodexTracker.exe
dotnet run --project tools\CodexTracker.Tooling\CodexTracker.Tooling.csproj --configuration Release -- check
dotnet run --project tools\CodexTracker.Tooling\CodexTracker.Tooling.csproj --configuration Release -- release
```

The `src\check.ps1` and `src\launcher\build.ps1` files remain as compatibility wrappers for older repository installations. The fault-injection updater test remains PowerShell because it creates and mutates temporary Git repositories and Windows process fixtures.

## Controls

- Drag the widget to reposition it on the taskbar.
- Use **Launch at Windows startup** to start it automatically after Windows sign-in.
- Use **Hide widget** or **Quit** from the widget or tray menu when needed.
