const { app, BrowserWindow, ipcMain, Menu, nativeImage, screen, shell, Tray } = require("electron")
const { spawn } = require("node:child_process")
const fs = require("node:fs")
const path = require("node:path")

const REFRESH_INTERVAL_MS = 60_000
const USAGE_URL = "https://chatgpt.com/codex/settings/usage"

let tray
let client
let refreshTimer
let widget
let loginProcess
let loginPoll
let latestLimits = []
let lastError = "Starting Codex..."
let widgetPosition
let snappingWidget = false
let widgetHiddenByUser = false
let widgetDragStart

function createTrayIcon() {
  const customIcon = nativeImage.createFromPath(path.join(__dirname, "icon.png"))
  if (!customIcon.isEmpty()) {
    const { width, height } = customIcon.getSize()
    const side = Math.floor(Math.min(width * 0.84, height * 0.98))
    return customIcon
      .crop({
        x: Math.floor((width - side) / 2),
        y: Math.floor((height - side) / 2),
        width: side,
        height: side,
      })
      .resize({ width: 32, height: 32, quality: "best" })
  }

  const size = 16
  const pixels = Buffer.alloc(size * size * 4)

  for (let y = 0; y < size; y += 1) {
    for (let x = 0; x < size; x += 1) {
      const offset = (y * size + x) * 4
      const filled = x >= 2 && x <= 13 && y >= 2 && y <= 13
      pixels[offset] = filled ? 16 : 0
      pixels[offset + 1] = filled ? 185 : 0
      pixels[offset + 2] = filled ? 129 : 0
      pixels[offset + 3] = filled ? 255 : 0
    }
  }

  return nativeImage.createFromBitmap(pixels, { width: size, height: size })
}

function findCodexBinary() {
  if (process.env.CODEX_BINARY) return process.env.CODEX_BINARY

  const releases = path.join(app.getPath("home"), ".codex", "packages", "standalone", "releases")
  try {
    const versions = fs.readdirSync(releases).sort().reverse()
    for (const version of versions) {
      const binary = path.join(releases, version, "bin", "codex.exe")
      if (fs.existsSync(binary)) return binary
    }
  } catch {
    // Fall back to the executable on PATH.
  }

  if (process.platform === "win32" && process.env.APPDATA) {
    const npmBinary = path.join(
      process.env.APPDATA,
      "npm",
      "node_modules",
      "@openai",
      "codex",
      "node_modules",
      "@openai",
      "codex-win32-x64",
      "vendor",
      "x86_64-pc-windows-msvc",
      "bin",
      "codex.exe",
    )
    if (fs.existsSync(npmBinary)) return npmBinary

    return path.join(process.env.APPDATA, "npm", "codex.cmd")
  }

  return "codex"
}

function spawnCodex(args, options) {
  const binary = findCodexBinary()
  if (process.platform === "win32" && binary.toLowerCase().endsWith(".cmd")) {
    return spawn(process.env.ComSpec ?? "cmd.exe", ["/d", "/s", "/c", `"${binary}" ${args.join(" ")}`], options)
  }
  return spawn(binary, args, options)
}

function isCodexCliAvailable() {
  return new Promise((resolve) => {
    const probe = spawnCodex(["--version"], {
      windowsHide: true,
      stdio: "ignore",
    })

    probe.on("error", () => resolve(false))
    probe.on("exit", (code) => resolve(code === 0))
  })
}

function installCodexCli() {
  return new Promise((resolve, reject) => {
    const command = process.platform === "win32" ? process.env.ComSpec ?? "cmd.exe" : "npm"
    const args = process.platform === "win32"
      ? ["/d", "/s", "/c", "npm install -g @openai/codex"]
      : ["install", "-g", "@openai/codex"]
    const installer = spawn(command, args, {
      windowsHide: true,
      stdio: "ignore",
    })

    installer.on("error", reject)
    installer.on("exit", (code) => {
      if (code === 0) {
        // npm's default global bin directory is not always inherited by hidden launchers.
        if (process.platform === "win32" && process.env.APPDATA) {
          process.env.PATH = `${path.join(process.env.APPDATA, "npm")};${process.env.PATH}`
        }
        resolve()
      } else {
        reject(new Error(`Codex installation failed (${code ?? "unknown"}).`))
      }
    })
  })
}

class CodexAppServer {
  constructor() {
    this.nextId = 1
    this.pending = new Map()
    this.buffer = ""
    this.process = null
  }

  async start() {
    this.process = spawnCodex(["app-server"], {
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    })

    this.process.stdout.setEncoding("utf8")
    this.process.stdout.on("data", (chunk) => this.onData(chunk))
    this.process.on("error", (error) => this.failAll(error))
    this.process.on("exit", (code) => this.failAll(new Error(`Codex app-server stopped (${code ?? "unknown"}).`)))

    await this.request("initialize", {
      clientInfo: {
        name: "codex_usage_tray",
        title: "Codex Usage Tray",
        version: app.getVersion(),
      },
      capabilities: null,
    })
    this.notify("initialized", {})
  }

  onData(chunk) {
    this.buffer += chunk
    let newline
    while ((newline = this.buffer.indexOf("\n")) !== -1) {
      const line = this.buffer.slice(0, newline)
      this.buffer = this.buffer.slice(newline + 1)
      if (!line.trim()) continue

      try {
        const message = JSON.parse(line)
        if (message.id !== undefined && this.pending.has(message.id)) {
          const { resolve, reject } = this.pending.get(message.id)
          this.pending.delete(message.id)
          if (message.error) reject(new Error(message.error.message))
          else resolve(message.result)
        } else if (message.method === "account/rateLimits/updated") {
          refreshLimits()
        }
      } catch {
        // App-server diagnostics may be written alongside protocol messages.
      }
    }
  }

  request(method, params) {
    return new Promise((resolve, reject) => {
      const id = this.nextId++
      this.pending.set(id, { resolve, reject })
      this.write({ method, id, params })
    })
  }

  notify(method, params) {
    this.write({ method, params })
  }

  write(message) {
    // Codex app-server's Windows stdio transport expects CRLF-delimited JSON.
    this.process.stdin.write(`${JSON.stringify(message)}\r\n`)
  }

  failAll(error) {
    for (const { reject } of this.pending.values()) reject(error)
    this.pending.clear()
  }

  stop() {
    this.process?.kill()
  }
}

function normalizeLimits(result) {
  const snapshots = result?.rateLimitsByLimitId
    ? Object.values(result.rateLimitsByLimitId)
    : [result?.rateLimits]

  return snapshots.flatMap((snapshot) => {
    if (!snapshot) return []
    const name = snapshot.limitName ?? snapshot.limitId ?? "Codex"
    const windowLabel = (window) => {
      if (!window?.windowDurationMins) return "usage"
      if (window.windowDurationMins % 1440 === 0) return `${window.windowDurationMins / 1440}d window`
      return `${window.windowDurationMins / 60}h window`
    }
    return [
      snapshot.primary && { ...snapshot.primary, limitId: snapshot.limitId, label: `${name} ${windowLabel(snapshot.primary)}` },
      snapshot.secondary && { ...snapshot.secondary, limitId: snapshot.limitId, label: `${name} ${windowLabel(snapshot.secondary)}` },
    ].filter(Boolean)
  })
}

function updateTray() {
  const menuItems = [
    {
      label: "Show widget",
      click: () => {
        widgetHiddenByUser = false
        positionWidget()
        widget?.show()
        raiseWidget()
      },
    },
    {
      label: "Reset widget position",
      click: () => {
        widgetPosition = null
        try {
          fs.unlinkSync(path.join(app.getPath("userData"), "widget-position.json"))
        } catch {
          // No saved position is also a valid reset state.
        }
        positionWidget()
      },
    },
    { label: "Refresh now", click: refreshLimits },
    { label: "Open Codex usage dashboard", click: () => shell.openExternal(USAGE_URL) },
    { type: "separator" },
    { label: "Quit", click: () => app.quit() },
  ]

  tray.setContextMenu(Menu.buildFromTemplate(menuItems))
  tray.setToolTip("")
  updateWidget()
}

function readWidgetPosition() {
  try {
    return JSON.parse(fs.readFileSync(path.join(app.getPath("userData"), "widget-position.json"), "utf8"))
  } catch {
    return null
  }
}

function saveWidgetPosition(x, display, width) {
  const usableWidth = Math.max(1, display.bounds.width - width)
  widgetPosition = {
    xRatio: Math.max(0, Math.min(1, (x - display.bounds.x) / usableWidth)),
  }
  try {
    fs.writeFileSync(path.join(app.getPath("userData"), "widget-position.json"), JSON.stringify(widgetPosition))
  } catch {
    // The widget still works if its position cannot be persisted.
  }
}

function getTaskbarLayout(display) {
  const { bounds, workArea } = display
  if (workArea.height < bounds.height) {
    return workArea.y === bounds.y
      ? { side: "bottom", size: bounds.height - workArea.height }
      : { side: "top", size: bounds.height - workArea.height }
  }
  return workArea.x === bounds.x
    ? { side: "right", size: bounds.width - workArea.width }
    : { side: "left", size: bounds.width - workArea.width }
}

function positionWidget(preferredX) {
  if (!widget) return

  const display = screen.getPrimaryDisplay()
  const { bounds, workArea } = display
  const [width, height] = widget.getSize()
  const taskbar = getTaskbarLayout(display)
  const maxX = bounds.x + bounds.width - width
  const defaultX = bounds.x + Math.round((bounds.width - width) * 0.3)
  const savedX = widgetPosition
    ? bounds.x + Math.round((bounds.width - width) * widgetPosition.xRatio)
    : defaultX
  let x = Math.max(bounds.x, Math.min(maxX, preferredX ?? savedX))
  let y = workArea.y + workArea.height - height

  if (taskbar.side === "top") y = bounds.y + taskbar.size
  if (taskbar.side === "left") {
    x = workArea.x
    y = bounds.y + Math.round((bounds.height - height) * 0.3)
  }
  if (taskbar.side === "right") {
    x = workArea.x + workArea.width - width
    y = bounds.y + Math.round((bounds.height - height) * 0.3)
  }

  snappingWidget = true
  widget.setPosition(x, y)
  setTimeout(() => {
    snappingWidget = false
  }, 0)
}

function updateWidget() {
  if (!widget || widget.isDestroyed()) return

  const codexLimits = latestLimits.filter((limit) => limit.limitId === "codex")
  const fiveHour = codexLimits.find((limit) => limit.windowDurationMins === 300)
  const weekly = codexLimits.find((limit) => limit.windowDurationMins === 10_080)
  const percentRemaining = (limit) => Math.max(0, Math.min(100, 100 - Number(limit?.usedPercent ?? 0))).toFixed(0)
  const resetTime = (limit) => {
    if (!limit?.resetsAt) return ""
    const timestamp = limit.resetsAt < 10_000_000_000 ? limit.resetsAt * 1000 : limit.resetsAt
    return new Date(timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
  }

  widget.webContents.send("usage:update", {
    fiveHour: fiveHour ? `${percentRemaining(fiveHour)}%` : "--",
    fiveHourReset: resetTime(fiveHour),
    weekly: weekly ? `${percentRemaining(weekly)}%` : "--",
    weeklyReset: resetTime(weekly),
    status: lastError,
  })
}

function raiseWidget() {
  if (!widget || widget.isDestroyed() || widgetHiddenByUser) return

  if (!widget.isVisible()) widget.showInactive()
  widget.setAlwaysOnTop(true, "floating")
  widget.moveTop()
}

function createWidget() {
  widget = new BrowserWindow({
    width: 320,
    height: 42,
    frame: false,
    resizable: false,
    movable: true,
    minimizable: false,
    maximizable: false,
    alwaysOnTop: true,
    skipTaskbar: true,
    hasShadow: false,
    thickFrame: false,
    show: false,
    backgroundColor: "#1c242e",
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      preload: path.join(__dirname, "widget-preload.cjs"),
    },
  })

  widgetPosition = readWidgetPosition()
  // The Windows taskbar is itself topmost, so the normal floating level can be hidden behind it.
  widget.setAlwaysOnTop(true, "floating")
  widget.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true })
  widget.loadFile(path.join(__dirname, "widget.html"))
  widget.once("ready-to-show", () => {
    widget.setHasShadow(false)
    positionWidget()
    updateWidget()
    widget.show()
    raiseWidget()
  })
  widget.on("closed", () => {
    widget = null
  })
  widget.on("moved", () => {
    if (snappingWidget) return
    const [x] = widget.getPosition()
    const display = screen.getDisplayMatching(widget.getBounds())
    saveWidgetPosition(x, display, widget.getSize()[0])
    positionWidget(x)
  })
}

async function refreshLimits() {
  if (!client) return

  try {
    const result = await client.request("account/rateLimits/read", {})
    latestLimits = normalizeLimits(result)
    lastError = latestLimits.length ? "" : "No Codex usage limits were returned."
  } catch (error) {
    latestLimits = []
    lastError = `Could not read Codex usage: ${error.message}`
  }
  updateTray()
}

async function startCodexClient() {
  if (!(await isCodexCliAvailable())) {
    lastError = "Installing Codex CLI..."
    updateTray()
    await installCodexCli()
  }

  client = new CodexAppServer()
  await client.start()
  await refreshLimits()
}

function isCodexLoggedIn() {
  return new Promise((resolve) => {
    const status = spawnCodex(["login", "status"], {
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    })
    let output = ""
    status.stdout.setEncoding("utf8")
    status.stderr.setEncoding("utf8")
    status.stdout.on("data", (chunk) => {
      output += chunk
    })
    status.stderr.on("data", (chunk) => {
      output += chunk
    })
    status.on("error", () => resolve(false))
    status.on("close", () => resolve(output.includes("Logged in")))
  })
}

function startCliLogin() {
  if (loginProcess) return

  clearInterval(loginPoll)
  loginPoll = null
  lastError = "Complete the Codex CLI sign-in."
  updateTray()
  loginProcess = spawnCodex(["login"], {
    windowsHide: false,
    stdio: "ignore",
    detached: true,
  })
  loginProcess.unref()
  loginProcess.on("error", (error) => {
    loginProcess = null
    lastError = `Could not start Codex login: ${error.message}`
    updateTray()
  })
  loginProcess.on("exit", () => {
    // Let the user retry if they close the Codex login window before completing it.
    loginProcess = null
  })
  loginPoll = setInterval(async () => {
    if (!(await isCodexLoggedIn())) return

    clearInterval(loginPoll)
    loginPoll = null
    loginProcess = null
    try {
      await startCodexClient()
    } catch (error) {
      lastError = `Could not start Codex: ${error.message}`
      updateTray()
    }
  }, 2000)
}

app.whenReady().then(async () => {
  ipcMain.on("widget:drag-start", (_, screenX) => {
    if (!widget || widget.isDestroyed()) return
    const [x] = widget.getPosition()
    widgetDragStart = { screenX, x }
  })
  ipcMain.on("widget:drag-move", (_, screenX) => {
    if (!widget || widget.isDestroyed() || !widgetDragStart) return
    const x = widgetDragStart.x + screenX - widgetDragStart.screenX
    const display = screen.getPrimaryDisplay()
    saveWidgetPosition(x, display, widget.getSize()[0])
    positionWidget(x)
  })
  ipcMain.on("widget:drag-end", () => {
    widgetDragStart = null
  })
  ipcMain.on("widget:context-menu", async () => {
    if (!widget || widget.isDestroyed()) return

    const loggedIn = await isCodexLoggedIn()
    const menuItems = [
      ...(!loggedIn
        ? [{ label: "Auth login", click: startCliLogin }, { type: "separator" }]
        : []),
      {
        label: "Hide widget",
        click: () => {
          widgetHiddenByUser = true
          widget.hide()
        },
      },
      { type: "separator" },
      { label: "Refresh now", click: refreshLimits },
      { label: "Open Codex usage dashboard", click: () => shell.openExternal(USAGE_URL) },
      { type: "separator" },
      { label: "Quit", click: () => app.quit() },
    ]
    Menu.buildFromTemplate(menuItems).popup({ window: widget })
  })

  tray = new Tray(createTrayIcon())
  createWidget()
  updateTray()

  screen.on("display-added", positionWidget)
  screen.on("display-removed", positionWidget)
  screen.on("display-metrics-changed", positionWidget)

  try {
    if (!(await isCodexCliAvailable())) {
      lastError = "Installing Codex CLI..."
      updateTray()
      await installCodexCli()
    }

    if (await isCodexLoggedIn()) await startCodexClient()
    else {
      lastError = "Sign in from the widget menu."
      updateTray()
    }
  } catch (error) {
    lastError = `Could not start Codex: ${error.message}`
    updateTray()
  }

  refreshTimer = setInterval(refreshLimits, REFRESH_INTERVAL_MS)
})

app.on("window-all-closed", (event) => event.preventDefault())
app.on("before-quit", () => {
  clearInterval(refreshTimer)
  clearInterval(loginPoll)
  client?.stop()
  loginProcess?.kill()
})
