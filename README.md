# Codex Usage Tray

Windows system tray monitor for the Codex usage limits associated with the current Codex CLI login.

It starts the official local `codex app-server` and calls its documented `account/rateLimits/read` API. It does not read `%USERPROFILE%\.codex\auth.json`, browser cookies, or ChatGPT web endpoints. If Codex CLI is absent, it installs the official npm package with `npm install -g @openai/codex`.

## Run

```powershell
npm install
npm start
```

`start-widget.vbs` launches the widget without showing a terminal. A user-level Windows startup registry entry runs this launcher automatically when you sign in.

The compact widget stays flush above the Windows taskbar and shows the remaining 5-hour and weekly Codex limits with their reset times. Drag it horizontally to place it where you prefer; its position is remembered. It refreshes every minute. The system tray menu provides **Refresh now**, the official usage dashboard, and **Quit**.

If Codex is installed outside the standard standalone location, set `CODEX_BINARY` to the full path of `codex.exe` before launching the app.

Codex still requires a one-time ChatGPT sign-in before usage limits are available.
