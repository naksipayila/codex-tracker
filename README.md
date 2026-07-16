<h1 align="center">Codex Tracker</h1>

Windows taskbar widget for viewing your Latrix 6-hour and weekly usage limits.

## Usage

1. Install the [.NET 8 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/8.0) if it is not already installed.
2. Add the Latrix API key to the OpenCode global config at `~/.config/opencode/opencode.json`, under `provider.latrix.options.apiKey`.
3. Download and run `Codex Tracker.exe`.
4. Your Latrix limits appear in the widget automatically.

## Updates

Automatic updates require a Git clone of this repository on the `main` branch with no local changes. A direct download of `Codex Tracker.exe` runs normally, but must be updated by replacing the EXE manually.

Latrix usage reads the API key from the OpenCode global config at `~/.config/opencode/opencode.json`, under `provider.latrix.options.apiKey`. Codex Tracker does not ask for, copy, or store this key.

## Controls

- Drag the widget to reposition it on the taskbar.
- Use **Launch at Windows startup** to start it automatically after Windows sign-in.
- Use **Hide widget** or **Quit** from the widget or tray menu when needed.
