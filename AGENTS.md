# Codex Usage Tray

## Commands

- Install dependencies: `npm install`
- Run the Electron widget: `npm start`
- Focused verification: `npm run check` (syntax-checks `src/main.cjs`)
- `start-widget.vbs` runs `npm start` hidden; the user-level Windows Run entry invokes this file at sign-in.

## Structure

- `src/main.cjs` owns Electron lifecycle, tray/menu behavior, widget placement, Codex CLI discovery/install/login, and the `codex app-server` JSON-RPC client.
- `src/widget.html` is the compact taskbar-adjacent UI. Keep Node integration disabled; expose renderer actions only through `src/widget-preload.cjs`.
- `limit.png` is the tray-icon source. `createTrayIcon()` crops and resizes it for the Windows tray.

## Codex Integration

- Fetch limits only through the local official `codex app-server` method `account/rateLimits/read`; do not read `~/.codex/auth.json`, browser cookies, or ChatGPT web endpoints.
- On Windows, app-server stdin messages must be CRLF-delimited JSONL. `CodexAppServer.write()` intentionally uses `\r\n`.
- `codex login status` writes its status to stderr on Windows; collect both stdout and stderr and resolve only after the child `close` event.
- If Codex is missing, `installCodexCli()` runs `npm install -g @openai/codex`. `findCodexBinary()` must continue to support `CODEX_BINARY`, standalone installs, and npm's Windows x64 `codex.exe` path.
- Let Codex own login: `startCliLogin()` launches `codex login`; do not add custom credential collection or browser-auth flows.

## Widget Behavior

- Keep the widget flush above the taskbar and persist its horizontal position under Electron `userData` (`widget-position.json`).
- Preserve right-click menu actions and whole-widget horizontal drag support.
