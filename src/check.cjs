const { spawnSync } = require("node:child_process")
const { randomUUID } = require("node:crypto")
const fs = require("node:fs")
const os = require("node:os")
const path = require("node:path")
const vm = require("node:vm")
const { getLauncherBuildHash } = require("./launcher/build-hash.cjs")

const projectRoot = __dirname
const repositoryRoot = path.join(projectRoot, "..")

function read(relativePath) {
  return fs.readFileSync(path.join(projectRoot, relativePath), "utf8")
}

function checkJavaScript(relativePath) {
  new vm.Script(read(relativePath), { filename: relativePath })
}

checkJavaScript("main.cjs")
checkJavaScript("widget-preload.cjs")

const widget = read("widget.html")
const inlineScripts = [...widget.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)]
if (!inlineScripts.length) throw new Error("widget.html does not contain its renderer script.")
for (const [index, match] of inlineScripts.entries()) {
  new vm.Script(match[1], { filename: `widget.html script ${index + 1}` })
}

const packageJson = JSON.parse(read("package.json"))
const packageLock = JSON.parse(read("package-lock.json"))
if (typeof packageJson?.scripts?.check !== "string") {
  throw new Error("package.json does not define its verification command.")
}
if (JSON.stringify(packageJson.devDependencies ?? {}) !==
    JSON.stringify(packageLock?.packages?.[""]?.devDependencies ?? {})) {
  throw new Error("package.json and package-lock.json dependencies do not match.")
}

const launcher = fs.readFileSync(path.join(repositoryRoot, "start-widget.exe"))
if (launcher.length < 2 || launcher[0] !== 0x4d || launcher[1] !== 0x5a) {
  throw new Error("start-widget.exe is missing or is not a Windows executable.")
}

if (process.platform === "win32") {
  const token = randomUUID().replaceAll("-", "")
  const readyPath = path.join(os.tmpdir(), `codex-launcher-self-test-${token}.ready`)
  try {
    const result = spawnSync(
      path.join(repositoryRoot, "start-widget.exe"),
      ["--self-test", "2", readyPath, token],
      { cwd: repositoryRoot, timeout: 10_000, windowsHide: true },
    )
    const response = fs.existsSync(readyPath) ? fs.readFileSync(readyPath, "utf8").trim() : ""
    const buildHash = getLauncherBuildHash(path.join(projectRoot, "launcher"))
    if (result.error || result.status !== 0 || response !== `${token}|${buildHash}`) {
      throw new Error("start-widget.exe failed its updater protocol self-test.")
    }
  } finally {
    try { fs.unlinkSync(readyPath) } catch { }
  }
}
