const { app, BrowserWindow, dialog, ipcMain, Menu, nativeImage, powerMonitor, screen, shell, systemPreferences, Tray } = require("electron")
const { spawn } = require("node:child_process")
const { createHash, randomUUID } = require("node:crypto")
const fs = require("node:fs")
const path = require("node:path")
const vm = require("node:vm")

const APP_ROOT = path.join(__dirname, "..")
const REFRESH_INTERVAL_MS = 30_000
const USAGE_URL = "https://chatgpt.com/codex/settings/usage"
const STARTUP_REGISTRY_KEY = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"
const STARTUP_VALUE_NAME = "CodexUsageTray"
const STARTUP_SHORTCUT_NAME = "Codex Usage Tray.lnk"
const UPDATE_BRANCH = "main"
const UPDATE_LOCK_FILE = "update.lock"
const UPDATE_PENDING_FILE = "update.pending"
const UPDATE_PREFERENCES_FILE = "update-preferences.json"
const UPDATE_REMOTE = "origin"
const UPDATE_RESULT_FILE = "update-result.json"
const UPDATE_STATE_DIRECTORY = "updates"
const UPDATE_CHECK_TIMEOUT_MS = 60_000
const UPDATE_HANDOFF_TIMEOUT_MS = 10_000
const MAX_UPDATE_CHANGES = 12
const WIDGET_EXPANDED_WIDTH = 380
const WIDGET_COLLAPSED_WIDTH = 190
const WIDGET_RESIZE_DURATION_MS = 180
const WIDGET_RESUME_POSITION_ATTEMPTS = 8
const WIDGET_RESUME_POSITION_INTERVAL_MS = 500

let tray
let client
let refreshTimer
let refreshInFlight = false
let widget
let loginProcess
let loginPoll
let latestLimits = []
let lastError = "Starting Codex..."
let widgetPosition
let snappingWidget = false
let widgetHiddenByUser = false
let widgetDragStart
let widgetResizeTimer
let widgetPositionTimer
let widgetResumePositionTimer
let zOrderKeeper
let zOrderRestartTimer
let quitting = false
let updateInFlight = false
let updateAtStartup = true
let updateRepairNeeded = false
let updateRestartPending = false
let updateReadySignaled = false
let startupUpdateScheduled = false
let startupUpdateTimer
let updateRecoveryTimer

const userDataOverride = process.env.CODEX_USAGE_TRAY_USER_DATA
if (userDataOverride) app.setPath("userData", path.resolve(userDataOverride))
const hasSingleInstanceLock = app.requestSingleInstanceLock()
if (!hasSingleInstanceLock) app.quit()
const launchedByUpdater = process.env.CODEX_UPDATE_LAUNCH === "1"

function createTrayIcon() {
  const customIcon = nativeImage.createFromPath(path.join(__dirname, "icon.png"))
  if (!customIcon.isEmpty()) {
    return customIcon.resize({ width: 32, height: 32, quality: "best" })
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

function getLegacyStartupShortcutPath() {
  return path.join(
    app.getPath("appData"),
    "Microsoft",
    "Windows",
    "Start Menu",
    "Programs",
    "Startup",
    STARTUP_SHORTCUT_NAME,
  )
}

function getRegistryExecutable() {
  return path.join(process.env.SystemRoot ?? "C:\\Windows", "System32", "reg.exe")
}

function isWindowsStartupEnabled() {
  return new Promise((resolve) => {
    const registry = spawn(getRegistryExecutable(), [
      "query",
      STARTUP_REGISTRY_KEY,
      "/v",
      STARTUP_VALUE_NAME,
    ], {
      windowsHide: true,
      stdio: "ignore",
    })

    registry.on("error", () => resolve(false))
    registry.on("exit", (code) => resolve(code === 0))
  })
}

function removeLegacyStartupShortcut() {
  try {
    fs.unlinkSync(getLegacyStartupShortcutPath())
  } catch (error) {
    if (error.code !== "ENOENT") throw error
  }
}

function setWindowsStartupEnabled(enabled) {
  if (!enabled) removeLegacyStartupShortcut()

  return new Promise((resolve, reject) => {
    const launcher = path.join(__dirname, "..", "start-widget.exe")
    const args = enabled
      ? [
          "add",
          STARTUP_REGISTRY_KEY,
          "/v",
          STARTUP_VALUE_NAME,
          "/t",
          "REG_SZ",
          "/d",
          `"${launcher}"`,
          "/f",
        ]
      : ["delete", STARTUP_REGISTRY_KEY, "/v", STARTUP_VALUE_NAME, "/f"]
    const registry = spawn(getRegistryExecutable(), args, {
      windowsHide: true,
      stdio: "ignore",
    })

    registry.on("error", reject)
    registry.on("exit", (code) => {
      if (code !== 0) {
        if (!enabled) {
          resolve()
          return
        }
        reject(new Error(`Registry update failed (${code ?? "unknown"}).`))
        return
      }
      if (enabled) removeLegacyStartupShortcut()
      resolve()
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

function runCommand(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: APP_ROOT,
      env: options.env ?? process.env,
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    })
    let stdout = ""
    let stderr = ""
    let settled = false
    let timeoutTimer

    const finish = (error, result) => {
      if (settled) return
      settled = true
      clearTimeout(timeoutTimer)
      if (error) reject(error)
      else resolve(result)
    }
    child.stdout.setEncoding("utf8")
    child.stderr.setEncoding("utf8")
    child.stdout.on("data", (chunk) => {
      stdout += chunk
    })
    child.stderr.on("data", (chunk) => {
      stderr += chunk
    })
    child.once("error", (error) => finish(error))
    child.once("close", (code) => finish(null, { code, stdout, stderr }))
    timeoutTimer = setTimeout(() => {
      child.kill()
      finish(new Error(`${command} did not finish within ${UPDATE_CHECK_TIMEOUT_MS / 1000} seconds.`))
    }, options.timeoutMs ?? UPDATE_CHECK_TIMEOUT_MS)
  })
}

async function runGitCommand(args) {
  try {
    return await runCommand("git", ["-C", APP_ROOT, ...args], {
      env: {
        ...process.env,
        GCM_INTERACTIVE: "Never",
        GIT_TERMINAL_PROMPT: "0",
      },
    })
  } catch (error) {
    if (error.code === "ENOENT") throw new Error("Git is not installed or is not available on PATH.")
    throw error
  }
}

async function runGit(args) {
  const result = await runGitCommand(args)
  if (result.code !== 0) {
    throw new Error((result.stderr || result.stdout).trim() || `Git exited with code ${result.code ?? "unknown"}.`)
  }
  return result.stdout.trim()
}

function getUpdateLockPath() {
  return path.join(getUpdateStateDirectory(), UPDATE_LOCK_FILE)
}

function getUpdatePendingPath() {
  return path.join(getUpdateStateDirectory(), UPDATE_PENDING_FILE)
}

function getUpdateResultPath() {
  return path.join(getUpdateStateDirectory(), UPDATE_RESULT_FILE)
}

function getLegacyUpdatePath(fileName) {
  return path.join(app.getPath("userData"), fileName)
}

function getRepositoryId() {
  const resolved = path.resolve(APP_ROOT)
  const root = path.parse(resolved).root
  const normalized = resolved === root ? resolved : resolved.replace(/[\\/]+$/, "")
  const identity = normalized.replace(/[A-Z]/g, (character) => character.toLowerCase())
  return createHash("sha256").update(identity, "utf8").digest("hex").slice(0, 16)
}

function getUpdateStateDirectory() {
  const baseDirectory = userDataOverride
    ? path.resolve(userDataOverride)
    : path.join(process.env.LOCALAPPDATA ?? app.getPath("userData"), "CodexUsageTray")
  return path.join(baseDirectory, UPDATE_STATE_DIRECTORY, getRepositoryId())
}

function ensureUpdateStateDirectory() {
  const directory = getUpdateStateDirectory()
  fs.mkdirSync(directory, { recursive: true })
  return directory
}

function getUpdatePreferencesPath() {
  return path.join(app.getPath("userData"), UPDATE_PREFERENCES_FILE)
}

function readUpdatePreferences() {
  try {
    const preferences = JSON.parse(fs.readFileSync(getUpdatePreferencesPath(), "utf8"))
    return preferences?.updateAtStartup === true
  } catch {
    return true
  }
}

function writeUpdatePreferences(enabled) {
  try {
    fs.writeFileSync(getUpdatePreferencesPath(), JSON.stringify({ updateAtStartup: enabled }))
    return true
  } catch (error) {
    dialog.showMessageBox({
      type: "error",
      title: "Codex Usage Tray",
      message: "The update preference could not be saved.",
      detail: error.message,
      buttons: ["OK"],
      noLink: true,
    }).catch(() => {})
    return false
  }
}

function isProcessRunning(pid) {
  try {
    process.kill(pid, 0)
    return true
  } catch {
    return false
  }
}

function hasActiveUpdateLockAtPath(lockPath) {
  let lockContent

  try {
    lockContent = fs.readFileSync(lockPath, "utf8")
    const [pidText, startedAtText] = lockContent.trim().split("|")
    const pid = Number(pidText)
    const age = Date.now() - Number(startedAtText)
    if (Number.isInteger(pid) && age >= 0 && age < 6 * 60 * 60 * 1000 && isProcessRunning(pid)) {
      return true
    }
  } catch {
    return false
  }

  try {
    if (fs.readFileSync(lockPath, "utf8") === lockContent) fs.unlinkSync(lockPath)
  } catch {
    // The lock changed or disappeared while it was being inspected.
  }
  return false
}

function hasActiveUpdateLock() {
  return hasActiveUpdateLockAtPath(getUpdateLockPath()) ||
    hasActiveUpdateLockAtPath(getLegacyUpdatePath(UPDATE_LOCK_FILE))
}

function waitForUpdaterReady(updater, readyPath, token) {
  return new Promise((resolve, reject) => {
    let settled = false
    const finish = (error) => {
      if (settled) return
      settled = true
      clearInterval(pollTimer)
      clearTimeout(timeoutTimer)
      if (error) reject(error)
      else resolve()
    }
    const poll = () => {
      try {
        if (fs.readFileSync(readyPath, "utf8").trim() === token) finish()
      } catch {
        // The helper has not finished writing its ready file yet.
      }
    }
    const pollTimer = setInterval(poll, 50)
    const timeoutTimer = setTimeout(
      () => finish(new Error("The update helper did not become ready in time.")),
      UPDATE_HANDOFF_TIMEOUT_MS,
    )
    updater.once("error", finish)
    updater.once("exit", (code) => {
      finish(new Error(`The update helper exited before handoff (${code ?? "unknown"}).`))
    })
    poll()
  })
}

function removeFailedUpdaterMarkers(updaterPid, expectedCommit, targetCommit, token) {
  try {
    const lockPath = getUpdateLockPath()
    const pendingPath = getUpdatePendingPath()
    const lockContent = fs.readFileSync(lockPath, "utf8")
    const pendingContent = fs.readFileSync(pendingPath, "utf8")
    const [pid] = lockContent.trim().split("|")
    if (Number(pid) !== updaterPid) return
    if (!pendingContent.includes(`|${expectedCommit}|${targetCommit}|${token}|`)) return
    if (fs.readFileSync(lockPath, "utf8") !== lockContent) return
    if (fs.readFileSync(pendingPath, "utf8") !== pendingContent) return
    fs.unlinkSync(pendingPath)
    if (fs.readFileSync(lockPath, "utf8") === lockContent) fs.unlinkSync(lockPath)
  } catch {
    // The helper either cleaned its own markers or never created them.
  }
}

async function startDetachedUpdater(expectedCommit, targetCommit) {
  const keeperPid = zOrderKeeper?.pid ?? 0
  const launcher = path.join(APP_ROOT, "start-widget.exe")
  if (!fs.existsSync(launcher)) throw new Error("The update launcher is missing.")

  const token = randomUUID().replaceAll("-", "")
  const updateDirectory = path.join(app.getPath("temp"), "CodexUsageTray")
  const stateDirectory = ensureUpdateStateDirectory()
  const updaterPath = path.join(updateDirectory, `updater-${token}.exe`)
  const handoffReadyPath = path.join(stateDirectory, `update-handoff-${token}.ready`)
  const appReadyPath = path.join(stateDirectory, `update-app-${token}.ready`)
  const logPath = path.join(stateDirectory, "update.log")
  const resultPath = path.join(stateDirectory, UPDATE_RESULT_FILE)

  fs.mkdirSync(updateDirectory, { recursive: true })
  for (const readyPath of [handoffReadyPath, appReadyPath]) {
    try {
      fs.unlinkSync(readyPath)
    } catch {
      // Each update uses unique paths, so missing files are expected.
    }
  }
  fs.copyFileSync(launcher, updaterPath)
  updateRestartPending = true
  stopWidgetPinning()
  const updater = spawn(updaterPath, [
    "--update",
    "--repo", APP_ROOT,
    "--state-dir", stateDirectory,
    "--parent-pid", String(process.pid),
    "--keeper-pid", String(keeperPid),
    "--expected", expectedCommit,
    "--target", targetCommit,
    "--handoff-ready", handoffReadyPath,
    "--app-ready", appReadyPath,
    "--log", logPath,
    "--result", resultPath,
    "--token", token,
  ], {
    cwd: APP_ROOT,
    detached: true,
    windowsHide: false,
    stdio: "ignore",
  })

  try {
    await waitForUpdaterReady(updater, handoffReadyPath, token)
    try {
      fs.unlinkSync(handoffReadyPath)
    } catch {
      // The helper also removes stale handoff files after the parent exits.
    }
    updater.unref()
  } catch (error) {
    updater.kill()
    removeFailedUpdaterMarkers(updater.pid, expectedCommit, targetCommit, token)
    updateRestartPending = false
    restartWidgetPinning()
    throw error
  }
}

function readUpdateResult() {
  const resultPaths = [getUpdateResultPath(), getLegacyUpdatePath(UPDATE_RESULT_FILE)]
  const resultPath = resultPaths.find((candidate) => fs.existsSync(candidate)) ?? resultPaths[0]
  let result = null
  let malformed = false

  try {
    const content = fs.readFileSync(resultPath, "utf8").replace(/^\uFEFF/, "")
    const parsed = JSON.parse(content)
    if (typeof parsed?.success === "boolean" && typeof parsed.message === "string") {
      result = { ...parsed, resultPath }
    }
    else malformed = true
  } catch (error) {
    malformed = error.code !== "ENOENT"
    // A missing or malformed result should not affect normal startup.
  }

  if (malformed || result?.success) {
    try {
      fs.unlinkSync(resultPath)
    } catch {
      // A handled result can be discarded on a later launch.
    }
  }

  return result
}

function showPendingUpdateResult() {
  const result = readUpdateResult()
  const repairPending = hasPendingUpdateState()
  updateRepairNeeded = repairPending
  if (!result) {
    if (repairPending) updateTray()
    return
  }

  if (!result.success && !repairPending) {
    try {
      fs.unlinkSync(result.resultPath)
    } catch {
      // A recovered result can be discarded on a later launch.
    }
  }
  updateTray()
  if (!result.success && result.notified) return

  dialog.showMessageBox({
    type: result.success ? "info" : repairPending ? "error" : "warning",
    title: "Codex Usage Tray",
    message: result.success
      ? "Codex Usage Tray was updated."
      : repairPending
        ? "Codex Usage Tray could not be updated."
        : "Codex Usage Tray recovered from an incomplete update.",
    detail: result.message,
    buttons: ["OK"],
    noLink: true,
  }).then(() => {
    if (result.success || !repairPending) return
    try {
      const { resultPath, ...persistedResult } = result
      fs.writeFileSync(
        resultPath,
        JSON.stringify({ ...persistedResult, notified: true }),
      )
    } catch {
      // The same diagnostic may be shown again if its acknowledgement cannot be persisted.
    }
  }).catch(() => {})
}

function hasPendingUpdateState() {
  return fs.existsSync(getUpdatePendingPath()) ||
    fs.existsSync(path.join(APP_ROOT, ".update.pending")) ||
    fs.existsSync(getLegacyUpdatePath(UPDATE_PENDING_FILE))
}

function watchRecoveryCompletion() {
  if (process.env.CODEX_UPDATE_RECOVERY !== "1") return
  let attempts = 0
  clearInterval(updateRecoveryTimer)
  updateRecoveryTimer = setInterval(() => {
    attempts += 1
    if (hasPendingUpdateState() && attempts < 30) return
    clearInterval(updateRecoveryTimer)
    updateRecoveryTimer = null
    if (!hasPendingUpdateState() && updateRepairNeeded) {
      updateRepairNeeded = false
      updateTray()
    }
  }, 500)
}

function signalUpdateReady() {
  if (updateReadySignaled) return
  const readyPath = process.env.CODEX_UPDATE_READY_FILE
  const token = process.env.CODEX_UPDATE_TOKEN
  if (!launchedByUpdater || !readyPath || !token) return

  try {
    fs.writeFileSync(readyPath, token, "utf8")
    updateReadySignaled = true
  } catch (error) {
    lastError = `Could not confirm update startup: ${error.message}`
    updateWidget()
  }
}

async function getAvailableUpdateDetails(localCommit, remoteCommit) {
  const log = await runGit([
    "log",
    "--reverse",
    "--format=%s",
    `${localCommit}..${remoteCommit}`,
  ])
  const changes = log.split(/\r?\n/).filter(Boolean).map((line) => {
    const subject = line.replace(/[\x00-\x1f\x7f]/gi, " ").replace(/\s+/g, " ").trim()
    const label = subject.length > 160 ? `${subject.slice(0, 157)}...` : subject || "Untitled change"
    return `- ${label}`
  })
  const visibleChanges = changes.slice(-MAX_UPDATE_CHANGES)
  if (changes.length > visibleChanges.length) {
    visibleChanges.unshift(`- ... ${changes.length - visibleChanges.length} earlier commits not shown`)
  }
  if (!visibleChanges.length) visibleChanges.push("- No commit descriptions were provided.")

  return [
    "What's new:",
    ...visibleChanges,
    "",
    "The application will close, install dependencies, run its syntax check, and restart.",
  ].join("\n")
}

function isTrustedUpdateRemote(remoteUrl) {
  const normalized = remoteUrl.trim().replace(/\/+$/, "").toLowerCase()
  return normalized === "https://github.com/naksipayila/codex-tracker.git" ||
    normalized === "https://github.com/naksipayila/codex-tracker" ||
    normalized === "git@github.com:naksipayila/codex-tracker.git" ||
    normalized === "ssh://git@github.com/naksipayila/codex-tracker.git"
}

function validateJavaScript(source, filename) {
  try {
    new vm.Script(source, { filename })
  } catch (error) {
    throw new Error(`${filename} failed its syntax check: ${error.message}`)
  }
}

async function validateTargetUpdate(targetCommit) {
  const targetPath = (filePath) => `${targetCommit}:${filePath}`
  const [mainSource, preloadSource, widgetSource, checkSource, packageSource, lockSource] = await Promise.all([
    runGit(["show", targetPath("src/main.cjs")]),
    runGit(["show", targetPath("src/widget-preload.cjs")]),
    runGit(["show", targetPath("src/widget.html")]),
    runGit(["show", targetPath("src/check.cjs")]),
    runGit(["show", targetPath("src/package.json")]),
    runGit(["show", targetPath("src/package-lock.json")]),
    runGit(["cat-file", "-e", targetPath("start-widget.exe")]),
    runGit(["cat-file", "-e", targetPath("src/launcher/Program.cs")]),
    runGit(["cat-file", "-e", targetPath("src/launcher/build.ps1")]),
    runGit(["cat-file", "-e", targetPath("src/launcher/build-hash.cjs")]),
    runGit(["cat-file", "-e", targetPath("src/launcher/icon.ico")]),
  ])

  validateJavaScript(mainSource, "src/main.cjs")
  validateJavaScript(preloadSource, "src/widget-preload.cjs")
  validateJavaScript(checkSource, "src/check.cjs")
  const inlineScripts = [...widgetSource.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)]
  if (!inlineScripts.length) throw new Error("src/widget.html does not contain its renderer script.")
  for (const [index, match] of inlineScripts.entries()) {
    validateJavaScript(match[1], `src/widget.html script ${index + 1}`)
  }

  let packageJson
  let packageLock
  try {
    packageJson = JSON.parse(packageSource)
    packageLock = JSON.parse(lockSource)
  } catch (error) {
    throw new Error(`The target package metadata is invalid: ${error.message}`)
  }
  if (typeof packageJson?.scripts?.check !== "string") {
    throw new Error("The target package does not define its verification command.")
  }
  const declaredDependencies = JSON.stringify(packageJson.devDependencies ?? {})
  const lockedDependencies = JSON.stringify(packageLock?.packages?.[""]?.devDependencies ?? {})
  if (declaredDependencies !== lockedDependencies) {
    throw new Error("The target package.json and package-lock.json dependencies do not match.")
  }
}

async function checkForUpdates(options = {}) {
  if (updateInFlight) return

  const startup = options.startup === true
  updateInFlight = true
  updateTray()

  try {
    const gitDirectory = path.join(APP_ROOT, ".git")
    if (!fs.existsSync(gitDirectory) || !fs.statSync(gitDirectory).isDirectory()) {
      throw new Error("Automatic updates require a Git clone of the repository.")
    }

    const branch = await runGit(["branch", "--show-current"])
    if (branch !== UPDATE_BRANCH) {
      throw new Error(`Automatic updates require the ${UPDATE_BRANCH} branch. The current branch is ${branch || "detached"}.`)
    }

    const status = await runGit(["status", "--porcelain", "--untracked-files=all"])
    if (status) {
      throw new Error("The working tree has local changes. Commit or discard them before updating.")
    }

    const remoteUrl = await runGit(["remote", "get-url", UPDATE_REMOTE])
    if (!isTrustedUpdateRemote(remoteUrl)) {
      throw new Error("Automatic updates require the official Codex Usage Tray GitHub remote.")
    }
    await runGit([
      "fetch",
      "--quiet",
      "--no-tags",
      UPDATE_REMOTE,
      `refs/heads/${UPDATE_BRANCH}:refs/remotes/${UPDATE_REMOTE}/${UPDATE_BRANCH}`,
    ])
    const [localCommit, remoteCommit] = await Promise.all([
      runGit(["rev-parse", "HEAD"]),
      runGit(["rev-parse", `refs/remotes/${UPDATE_REMOTE}/${UPDATE_BRANCH}`]),
    ])

    if (localCommit === remoteCommit && !updateRepairNeeded) {
      if (!startup) {
        await dialog.showMessageBox({
          type: "info",
          title: "Codex Usage Tray",
          message: "Codex Usage Tray is up to date.",
          buttons: ["OK"],
          noLink: true,
        })
      }
      return
    }

    let updateDetails
    if (localCommit !== remoteCommit) {
      const ancestry = await runGitCommand(["merge-base", "--is-ancestor", localCommit, remoteCommit])
      if (ancestry.code === 1) {
        throw new Error("The local main branch contains commits that are not on origin/main. Sync it manually before updating.")
      }
      if (ancestry.code !== 0) {
        throw new Error((ancestry.stderr || ancestry.stdout).trim() || "Could not compare the local and remote branches.")
      }

      await validateTargetUpdate(remoteCommit)
      updateDetails = await getAvailableUpdateDetails(localCommit, remoteCommit)
    }

    const repairing = localCommit === remoteCommit
    const confirmation = await dialog.showMessageBox({
      type: "question",
      title: "Codex Usage Tray",
      message: repairing ? "The previous update needs repair." : "An update is available.",
      detail: repairing
        ? "The application will close, reinstall dependencies, run its syntax check, and restart."
        : updateDetails,
      buttons: [repairing ? "Repair and restart" : "Update and restart", "Not now"],
      defaultId: 1,
      cancelId: 1,
      noLink: true,
    })
    if (confirmation.response !== 0) return

    await startDetachedUpdater(localCommit, remoteCommit)
    app.quit()
  } catch (error) {
    if (!startup) {
      await dialog.showMessageBox({
        type: "error",
        title: "Codex Usage Tray",
        message: "Automatic update is unavailable.",
        detail: error.message,
        buttons: ["OK"],
        noLink: true,
      })
    }
  } finally {
    updateInFlight = false
    if (!quitting && tray) updateTray()
  }
}

function scheduleStartupUpdate() {
  if (startupUpdateScheduled || !updateAtStartup || launchedByUpdater || quitting) return
  startupUpdateScheduled = true
  startupUpdateTimer = setTimeout(() => {
    startupUpdateTimer = null
    if (!quitting && updateAtStartup) checkForUpdates({ startup: true })
  }, 500)
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
        restartWidgetPinning()
      },
    },
    ...(updateRepairNeeded
      ? [{
          label: updateInFlight ? "Repairing update..." : "Repair update",
          enabled: !updateInFlight,
          click: () => checkForUpdates(),
        }]
      : []),
    {
      label: updateInFlight ? "Checking for updates..." : "Check update at startup",
      type: "checkbox",
      checked: updateAtStartup,
      enabled: !updateInFlight,
      click: (item) => {
        if (!writeUpdatePreferences(item.checked)) {
          item.checked = updateAtStartup
          return
        }
        updateAtStartup = item.checked
        updateTray()
      },
    },
    { type: "separator" },
    { label: "Quit", click: () => app.quit() },
  ]

  tray.setContextMenu(Menu.buildFromTemplate(menuItems))
  updateWidget()
}

function readWidgetPosition() {
  try {
    return JSON.parse(fs.readFileSync(path.join(app.getPath("userData"), "widget-position.json"), "utf8"))
  } catch {
    return null
  }
}

function writeWidgetPosition() {
  try {
    fs.writeFileSync(path.join(app.getPath("userData"), "widget-position.json"), JSON.stringify(widgetPosition))
  } catch {
    // The widget still works if its position cannot be persisted.
  }
}

function saveWidgetPosition(x, display, width) {
  const usableWidth = Math.max(1, display.bounds.width - width)
  widgetPosition = {
    ...widgetPosition,
    xRatio: Math.max(0, Math.min(1, (x - display.bounds.x) / usableWidth)),
  }
  writeWidgetPosition()
}

function saveWidgetCollapsed(collapsed) {
  widgetPosition = { ...widgetPosition, collapsed }
  writeWidgetPosition()
}

function hidesInFullscreenApps() {
  return widgetPosition?.hideInFullscreen !== false
}

function saveWidgetHideInFullscreen(hideInFullscreen) {
  widgetPosition = { ...widgetPosition, hideInFullscreen }
  writeWidgetPosition()
}

function getTaskbarLayout(display) {
  const { bounds, workArea } = display
  if (workArea.width === bounds.width && workArea.height === bounds.height) return null
  if (workArea.height < bounds.height) {
    return workArea.y === bounds.y
      ? { side: "bottom", size: bounds.height - workArea.height }
      : { side: "top", size: bounds.height - workArea.height }
  }
  return workArea.x === bounds.x
    ? { side: "right", size: bounds.width - workArea.width }
    : { side: "left", size: bounds.width - workArea.width }
}

function shouldAnimateWidget() {
  try {
    const settings = systemPreferences.getAnimationSettings()
    return settings.shouldRenderRichAnimation && !settings.prefersReducedMotion
  } catch {
    return true
  }
}

function resizeWidgetWidth(targetWidth, delay = 0) {
  if (!widget || widget.isDestroyed()) return

  clearTimeout(widgetResizeTimer)
  const resize = () => {
    if (!widget || widget.isDestroyed()) return
    const startBounds = widget.getBounds()
    const display = screen.getDisplayMatching(startBounds)
    const right = Math.max(
      display.bounds.x + targetWidth,
      Math.min(display.bounds.x + display.bounds.width, startBounds.x + startBounds.width),
    )
    const targetBounds = { ...startBounds, x: right - targetWidth, width: targetWidth }
    snappingWidget = true
    widget.setResizable(true)
    widget.setBounds(targetBounds)
    widget.setResizable(false)
    const updatedBounds = widget.getBounds()
    saveWidgetPosition(updatedBounds.x, display, updatedBounds.width)
    setTimeout(() => {
      snappingWidget = false
    }, 100)
  }

  if (!delay) {
    resize()
    return
  }
  widgetResizeTimer = setTimeout(resize, delay)
}

function positionWidget(preferredX) {
  if (!widget || widget.isDestroyed()) return false

  const display = screen.getPrimaryDisplay()
  const { bounds, workArea } = display
  const taskbar = getTaskbarLayout(display)
  if (!taskbar) return false
  const [width, currentHeight] = widget.getSize()
  const height = taskbar.side === "top" || taskbar.side === "bottom"
    ? Math.max(28, Math.min(34, Math.round(taskbar.size * 0.7)))
    : currentHeight
  if (height !== currentHeight) widget.setSize(width, height)
  const maxX = bounds.x + bounds.width - width
  const defaultX = bounds.x + Math.round((bounds.width - width) * 0.3)
  const savedX = widgetPosition
    ? bounds.x + Math.round((bounds.width - width) * widgetPosition.xRatio)
    : defaultX
  const requestedX = Number.isFinite(preferredX) ? preferredX : savedX
  let x = Math.max(bounds.x, Math.min(maxX, requestedX))
  let y = bounds.y + bounds.height - taskbar.size + Math.round((taskbar.size - height) / 2)

  if (taskbar.side === "top") y = bounds.y + Math.round((taskbar.size - height) / 2)
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
  return true
}

function scheduleWidgetPosition(delay = 150, restartPinning = false, retries = 3) {
  clearTimeout(widgetPositionTimer)
  widgetPositionTimer = setTimeout(() => {
    if (!positionWidget() && retries > 0) {
      scheduleWidgetPosition(250, restartPinning, retries - 1)
      return
    }
    if (widgetHiddenByUser) return
    raiseWidget()
    if (restartPinning) restartWidgetPinning()
  }, delay)
}

function restoreWidgetPositionAfterResume() {
  clearTimeout(widgetResumePositionTimer)
  let attemptsLeft = WIDGET_RESUME_POSITION_ATTEMPTS

  const restore = () => {
    if (quitting || !widget || widget.isDestroyed()) {
      widgetResumePositionTimer = null
      return
    }

    // Windows can adjust the work area and clamp topmost windows several times while the shell wakes up.
    positionWidget()
    attemptsLeft -= 1
    if (attemptsLeft > 0) {
      widgetResumePositionTimer = setTimeout(restore, WIDGET_RESUME_POSITION_INTERVAL_MS)
      return
    }

    widgetResumePositionTimer = null
    if (widgetHiddenByUser) return
    raiseWidget()
    restartWidgetPinning()
  }

  widgetResumePositionTimer = setTimeout(restore, WIDGET_RESUME_POSITION_INTERVAL_MS)
}

function updateWidget() {
  if (!widget || widget.isDestroyed()) return

  const codexLimits = latestLimits.filter((limit) => limit.limitId === "codex")
  const fiveHour = codexLimits.find((limit) => limit.windowDurationMins === 300)
  const weekly = codexLimits.find((limit) => limit.windowDurationMins === 10_080)
  const percentRemaining = (limit) => Math.max(0, Math.min(100, 100 - Number(limit?.usedPercent ?? 0))).toFixed(0)
  const resetTime = (limit, includeDate = false) => {
    if (!limit?.resetsAt) return ""
    const timestamp = limit.resetsAt < 10_000_000_000 ? limit.resetsAt * 1000 : limit.resetsAt
    const date = new Date(timestamp)
    const time = `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`
    if (!includeDate) return time
    const dayMonth = date.toLocaleDateString("tr-TR", { day: "2-digit", month: "short" })
    return `${dayMonth} ${time}`
  }

  widget.webContents.send("usage:update", {
    fiveHour: fiveHour ? `${percentRemaining(fiveHour)}%` : "--",
    fiveHourReset: resetTime(fiveHour),
    weekly: weekly ? `${percentRemaining(weekly)}%` : "--",
    weeklyReset: resetTime(weekly, true),
    status: lastError,
  })
}

function raiseWidget() {
  if (!widget || widget.isDestroyed() || widgetHiddenByUser) return

  if (!widget.isVisible()) widget.showInactive()
  widget.setAlwaysOnTop(true, "screen-saver")
  widget.moveTop()
}

function pinWidgetAboveTaskbar() {
  const launcher = path.join(__dirname, "..", "start-widget.exe")
  if (quitting || updateRestartPending || zOrderKeeper || !widget || widget.isDestroyed() || !fs.existsSync(launcher)) return

  const handle = widget.getNativeWindowHandle()
  const windowHandle = handle.length === 8 ? handle.readBigUInt64LE().toString() : handle.readUInt32LE().toString()
  const args = ["--pin-hwnd", windowHandle, "--parent-pid", String(process.pid)]
  if (hidesInFullscreenApps()) args.push("--hide-in-fullscreen")
  const keeper = spawn(launcher, args, {
    windowsHide: true,
    stdio: "ignore",
  })
  zOrderKeeper = keeper
  const restartKeeper = () => {
    if (zOrderKeeper !== keeper) return
    zOrderKeeper = null
    if (quitting || !widget || widget.isDestroyed()) return
    clearTimeout(zOrderRestartTimer)
    zOrderRestartTimer = setTimeout(pinWidgetAboveTaskbar, 1000)
  }
  keeper.once("error", restartKeeper)
  keeper.once("exit", restartKeeper)
  keeper.unref()
}

function restartWidgetPinning() {
  stopWidgetPinning()
  pinWidgetAboveTaskbar()
}

function stopWidgetPinning() {
  clearTimeout(zOrderRestartTimer)
  const keeper = zOrderKeeper
  zOrderKeeper = null
  keeper?.kill()
}

function createWidget() {
  widgetPosition = readWidgetPosition()
  widget = new BrowserWindow({
    width: widgetPosition?.collapsed ? WIDGET_COLLAPSED_WIDTH : WIDGET_EXPANDED_WIDTH,
    height: 34,
    frame: false,
    resizable: false,
    movable: true,
    minimizable: false,
    maximizable: false,
    alwaysOnTop: true,
    skipTaskbar: true,
    hasShadow: false,
    thickFrame: false,
    transparent: true,
    show: false,
    backgroundColor: "#00000000",
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      preload: path.join(__dirname, "widget-preload.cjs"),
    },
  })

  // Keep the text above the taskbar when the taskbar is activated.
  widget.setAlwaysOnTop(true, "screen-saver")
  widget.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true })
  widget.loadFile(path.join(__dirname, "widget.html"))
  widget.once("ready-to-show", () => {
    widget.setHasShadow(false)
    positionWidget()
    updateWidget()
    widget.show()
    raiseWidget()
    pinWidgetAboveTaskbar()
    widget.webContents.send("widget:animate-in", shouldAnimateWidget())
    signalUpdateReady()
    watchRecoveryCompletion()
    scheduleStartupUpdate()
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
  if (!client || refreshInFlight) return

  refreshInFlight = true

  try {
    const result = await client.request("account/rateLimits/read", {})
    latestLimits = normalizeLimits(result)
    lastError = latestLimits.length ? "" : "No Codex usage limits were returned."
  } catch (error) {
    latestLimits = []
    lastError = `Could not read Codex usage: ${error.message}`
  } finally {
    refreshInFlight = false
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

if (hasSingleInstanceLock) app.whenReady().then(async () => {
  if (!launchedByUpdater && hasActiveUpdateLock()) {
    app.quit()
    return
  }
  if (process.env.CODEX_UPDATE_RECOVERY === "1") {
    updateRepairNeeded = hasPendingUpdateState()
  }
  updateAtStartup = readUpdatePreferences()

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
  ipcMain.handle("widget:get-weekly-collapsed", () => Boolean(widgetPosition?.collapsed))
  ipcMain.handle("widget:toggle-weekly", (_, collapsed, animate) => {
    if (!widget || widget.isDestroyed()) return false
    if (typeof collapsed !== "boolean") collapsed = !widgetPosition?.collapsed
    const width = collapsed ? WIDGET_COLLAPSED_WIDTH : WIDGET_EXPANDED_WIDTH
    saveWidgetCollapsed(collapsed)
    resizeWidgetWidth(width, collapsed && animate ? WIDGET_RESIZE_DURATION_MS : 0)
    return collapsed
  })
  ipcMain.on("widget:context-menu", async () => {
    if (!widget || widget.isDestroyed()) return

    const [loggedIn, startupEnabled] = await Promise.all([
      isCodexLoggedIn(),
      isWindowsStartupEnabled(),
    ])
    const menuItems = [
      { label: "Open Codex usage dashboard", click: () => shell.openExternal(USAGE_URL) },
      { label: "Refresh usage", click: refreshLimits },
      { type: "separator" },
      ...(!loggedIn
        ? [{ label: "Sign in to Codex", click: startCliLogin }, { type: "separator" }]
        : []),
      {
        label: "Launch at Windows startup",
        type: "checkbox",
        checked: startupEnabled,
        click: async () => {
          try {
            await setWindowsStartupEnabled(!startupEnabled)
            lastError = startupEnabled
              ? "Removed from Windows startup."
              : "Added to Windows startup."
          } catch (error) {
            lastError = `Could not update Windows startup: ${error.message}`
          }
          updateTray()
        },
      },
      {
        label: "Hide in fullscreen apps",
        type: "checkbox",
        checked: hidesInFullscreenApps(),
        click: (item) => {
          saveWidgetHideInFullscreen(item.checked)
          restartWidgetPinning()
        },
      },
      { type: "separator" },
      {
        label: "Hide widget",
        click: () => {
          widgetHiddenByUser = true
          widget.hide()
          stopWidgetPinning()
        },
      },
      { type: "separator" },
      { label: "Quit", click: () => app.quit() },
    ]
    Menu.buildFromTemplate(menuItems).popup({ window: widget })
  })

  tray = new Tray(createTrayIcon())
  createWidget()
  updateTray()
  if (!launchedByUpdater || process.env.CODEX_UPDATE_RECOVERY === "1") showPendingUpdateResult()

  screen.on("display-added", () => scheduleWidgetPosition())
  screen.on("display-removed", () => scheduleWidgetPosition())
  screen.on("display-metrics-changed", () => scheduleWidgetPosition())
  powerMonitor.on("resume", restoreWidgetPositionAfterResume)
  powerMonitor.on("unlock-screen", restoreWidgetPositionAfterResume)

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

app.on("second-instance", () => {
  if (quitting || !widget || widget.isDestroyed()) return
  widgetHiddenByUser = false
  positionWidget()
  widget.show()
  raiseWidget()
  restartWidgetPinning()
})

app.on("window-all-closed", (event) => event.preventDefault())
app.on("before-quit", () => {
  quitting = true
  clearInterval(refreshTimer)
  clearInterval(loginPoll)
  clearTimeout(widgetPositionTimer)
  clearTimeout(widgetResumePositionTimer)
  clearTimeout(zOrderRestartTimer)
  clearTimeout(startupUpdateTimer)
  clearInterval(updateRecoveryTimer)
  client?.stop()
  loginProcess?.kill()
  zOrderKeeper?.kill()
})
