<h1 align="center">Codex Tracker</h1>

Windows taskbar widget for viewing your Latrix 6-hour and weekly usage limits.

## Usage

1. Download and run `Codex Tracker Setup.exe` on Windows x64.
2. Follow the per-user installation prompts. The installer includes the .NET runtime.
3. Add the Latrix API key to the OpenCode global config at `~/.config/opencode/opencode.json`, under `provider.latrix.options.apiKey`.
4. Your Latrix limits appear in the widget automatically.

## Updates

The installer is designed for per-user installation under `%LOCALAPPDATA%`. Release packages are published from version tags. Git-clone installations also use the repository updater and require the `main` branch with no local changes.

Latrix usage reads the API key from the OpenCode global config at `~/.config/opencode/opencode.json`, under `provider.latrix.options.apiKey`. Codex Tracker does not ask for, copy, or store this key.

## Controls

- Drag the widget to reposition it on the taskbar.
- Use **Launch at Windows startup** to start it automatically after Windows sign-in.
- Use **Hide widget** or **Quit** from the widget or tray menu when needed.
