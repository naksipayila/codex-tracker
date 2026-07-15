const { createHash } = require("node:crypto")
const fs = require("node:fs")
const path = require("node:path")

function normalizeText(value) {
  return value.replace(/\r\n?/g, "\n")
}

function getLauncherBuildHash(launcherDirectory = __dirname) {
  const program = normalizeText(fs.readFileSync(path.join(launcherDirectory, "Program.cs"), "utf8"))
  const buildScript = normalizeText(fs.readFileSync(path.join(launcherDirectory, "build.ps1"), "utf8"))
  const icon = fs.readFileSync(path.join(launcherDirectory, "icon.ico")).toString("base64")
  return createHash("sha256").update(`${program}\0${buildScript}\0${icon}`, "utf8").digest("hex")
}

if (require.main === module) process.stdout.write(getLauncherBuildHash())

module.exports = { getLauncherBuildHash }
