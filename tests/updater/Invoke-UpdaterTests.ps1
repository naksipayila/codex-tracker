param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,

    [Parameter(Mandatory = $true)]
    [string] $ScratchRoot
)

$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$ScratchRoot = [IO.Path]::GetFullPath($ScratchRoot)
$scratchDriveRoot = [IO.Path]::GetPathRoot($ScratchRoot)
$scratchPrefix = $ScratchRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$sourcePrefix = $SourceRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ([string]::Equals($ScratchRoot, $scratchDriveRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($ScratchRoot, $SourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $sourcePrefix.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $scratchPrefix.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ScratchRoot must be a dedicated directory outside SourceRoot and may not contain it."
}

function Write-Text([string] $Path, [string] $Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Invoke-Git([string] $Repository, [string[]] $Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & git -C $Repository @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw "git $($Arguments -join ' ') failed:`n$($output -join [Environment]::NewLine)"
    }
    return ($output -join "`n").Trim()
}

function Quote-Argument([string] $Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Get-RepositoryId([string] $Repository) {
    $identity = [IO.Path]::GetFullPath($Repository)
    $root = [IO.Path]::GetPathRoot($identity)
    if (![string]::Equals($identity, $root, [StringComparison]::OrdinalIgnoreCase)) {
        $identity = $identity.TrimEnd('\', '/')
    }
    $builder = New-Object Text.StringBuilder
    foreach ($character in $identity.ToCharArray()) {
        if ($character -ge 'A' -and $character -le 'Z') {
            [void]$builder.Append([char]([int]$character + ([int][char]'a' - [int][char]'A')))
        } else {
            [void]$builder.Append($character)
        }
    }
    $identity = $builder.ToString()
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity))
        return ([BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant()).Substring(0, 16)
    } finally {
        $sha256.Dispose()
    }
}

function Stop-FakeElectron([string] $LogPath) {
    if (!(Test-Path -LiteralPath $LogPath)) { return }
    foreach ($line in [IO.File]::ReadAllLines($LogPath)) {
        $parts = $line.Split('|')
        if ($parts.Length -ne 3) { continue }
        $processId = 0
        if (![int]::TryParse($parts[2], [ref]$processId)) { continue }
        try {
            $process = [Diagnostics.Process]::GetProcessById($processId)
            $executable = $process.MainModule.FileName
            if ($executable.StartsWith($ScratchRoot, [StringComparison]::OrdinalIgnoreCase)) {
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }
            $process.Dispose()
        } catch {
        }
    }
}

function Invoke-UpdateCase([string] $Name, [string] $Fault) {
    $caseRoot = Join-Path $ScratchRoot $Name
    $repository = Join-Path $caseRoot "repo"
    $tools = Join-Path $caseRoot "tools"
    $stateBase = Join-Path $caseRoot "state"
    $events = Join-Path $caseRoot "electron-events.log"
    $npmLog = Join-Path $caseRoot "npm.log"
    $containedChildPid = Join-Path $caseRoot "contained-child.pid"
    [IO.Directory]::CreateDirectory($repository) | Out-Null
    [IO.Directory]::CreateDirectory($tools) | Out-Null

    & git init --quiet $repository
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize fixture $Name." }
    Invoke-Git $repository @("symbolic-ref", "HEAD", "refs/heads/main") | Out-Null
    Invoke-Git $repository @("config", "user.name", "Updater Test") | Out-Null
    Invoke-Git $repository @("config", "user.email", "updater-test@example.invalid") | Out-Null
    Invoke-Git $repository @("config", "core.autocrlf", "false") | Out-Null

    Copy-Item -LiteralPath $script:BuiltLauncher -Destination (Join-Path $repository "start-widget.exe")
    Copy-Item -LiteralPath (Join-Path $SourceRoot "src\launcher\icon.ico") -Destination (Join-Path $repository "icon.ico")
    $src = Join-Path $repository "src"
    $fakeElectronDirectory = Join-Path $src "node_modules\electron\dist"
    [IO.Directory]::CreateDirectory($fakeElectronDirectory) | Out-Null
    Copy-Item -LiteralPath $script:FakeElectron -Destination (Join-Path $fakeElectronDirectory "electron.exe")

    Write-Text (Join-Path $repository ".gitignore") "node_modules/`n"
    Write-Text (Join-Path $src "main.cjs") "console.log('fixture')`n"
    Write-Text (Join-Path $src "widget-preload.cjs") "void 0`n"
    Write-Text (Join-Path $src "widget.html") "<!doctype html><script>void 0</script>`n"
    Write-Text (Join-Path $src "check.cjs") "void 0`n"
    [IO.Directory]::CreateDirectory((Join-Path $src "launcher")) | Out-Null
    Copy-Item -LiteralPath (Join-Path $SourceRoot "src\launcher\Program.cs") -Destination (Join-Path $src "launcher\Program.cs")
    Copy-Item -LiteralPath (Join-Path $SourceRoot "src\launcher\build.ps1") -Destination (Join-Path $src "launcher\build.ps1")
    Copy-Item -LiteralPath (Join-Path $SourceRoot "src\launcher\build-hash.cjs") -Destination (Join-Path $src "launcher\build-hash.cjs")
    Copy-Item -LiteralPath (Join-Path $SourceRoot "src\launcher\icon.ico") -Destination (Join-Path $src "launcher\icon.ico")
    Write-Text (Join-Path $src "package.json") '{"name":"updater-fixture","version":"1.0.0","scripts":{"check":"exit 0"}}'
    Write-Text (Join-Path $src "package-lock.json") '{"name":"updater-fixture","version":"1.0.0","lockfileVersion":3,"requires":true,"packages":{"":{"name":"updater-fixture","version":"1.0.0"}}}'
    Write-Text (Join-Path $src "version.txt") "A`n"
    Write-Text (Join-Path $tools "npm.cmd") (
        "@echo off`r`n" +
        'if "%FAKE_NPM_SPAWN_CHILD%"=="1" start "" /b "%FAKE_CHILD_EXE%" "%FAKE_CHILD_PID%"' + "`r`n" +
        'echo %*>> "%FAKE_NPM_LOG%"' + "`r`nexit /b 0`r`n"
    )

    Invoke-Git $repository @("add", ".") | Out-Null
    Invoke-Git $repository @("commit", "--quiet", "-m", "fixture A") | Out-Null
    $expected = Invoke-Git $repository @("rev-parse", "HEAD")

    Write-Text (Join-Path $src "version.txt") "B`n"
    if ($Fault -eq "crlf-source") {
        $programPath = Join-Path $src "launcher\Program.cs"
        $programContent = [IO.File]::ReadAllText($programPath).Replace("`r`n", "`n").Replace("`r", "`n")
        [IO.File]::WriteAllText($programPath, $programContent.Replace("`n", "`r`n"), $utf8)
    }
    if ($Fault -eq "bad-launcher") {
        Copy-Item -LiteralPath $script:LegacyLauncher -Destination (Join-Path $repository "start-widget.exe") -Force
    }
    Invoke-Git $repository @("add", ".") | Out-Null
    Invoke-Git $repository @("commit", "--quiet", "-m", "fixture B") | Out-Null
    $target = Invoke-Git $repository @("rev-parse", "HEAD")
    Invoke-Git $repository @("reset", "--hard", $expected) | Out-Null

    # node_modules is intentionally ignored and survives fixture resets.
    [IO.Directory]::CreateDirectory($fakeElectronDirectory) | Out-Null
    Copy-Item -LiteralPath $script:FakeElectron -Destination (Join-Path $fakeElectronDirectory "electron.exe") -Force

    $repositoryId = Get-RepositoryId $repository
    $stateDirectory = Join-Path $stateBase "updates\$repositoryId"
    [IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
    $token = [Guid]::NewGuid().ToString("N")
    $handoff = Join-Path $stateDirectory "handoff.ready"
    $appReady = Join-Path $stateDirectory "app.ready"
    $log = Join-Path $stateDirectory "update.log"
    $result = Join-Path $stateDirectory "update-result.json"

    $previousPath = $env:PATH
    $previousUserData = $env:CODEX_USAGE_TRAY_USER_DATA
    $previousLog = $env:FAKE_ELECTRON_LOG
    $previousFailure = $env:FAKE_ELECTRON_FAIL_VERSION
    $previousUnstable = $env:FAKE_ELECTRON_UNSTABLE_VERSION
    $previousNpmLog = $env:FAKE_NPM_LOG
    $previousSpawnChild = $env:FAKE_NPM_SPAWN_CHILD
    $previousChildExe = $env:FAKE_CHILD_EXE
    $previousChildPid = $env:FAKE_CHILD_PID
    try {
        $env:PATH = "$tools;$previousPath"
        $env:CODEX_USAGE_TRAY_USER_DATA = $stateBase
        $env:FAKE_ELECTRON_LOG = $events
        $env:FAKE_ELECTRON_FAIL_VERSION = if ($Fault -eq "runtime") { "B" } else { "" }
        $env:FAKE_ELECTRON_UNSTABLE_VERSION = if ($Fault -eq "unstable") { "B" } else { "" }
        $env:FAKE_NPM_LOG = $npmLog
        $env:FAKE_NPM_SPAWN_CHILD = if ($Fault -eq "job-containment") { "1" } else { "" }
        $env:FAKE_CHILD_EXE = $script:ContainedChild
        $env:FAKE_CHILD_PID = $containedChildPid

        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.WorkingDirectory = $repository
        $startInfo.UseShellExecute = $false
        if ($Fault -eq "legacy-resume" -or $Fault -eq "rollback-resume") {
            if ($Fault -eq "legacy-resume") {
                Write-Text (Join-Path $stateBase "update.pending") "$expected|$target"
            } else {
                $repositoryIdentity = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($repository).TrimEnd('\', '/')))
                Write-Text (Join-Path $stateDirectory "update.pending") "v3|$repositoryIdentity|$expected|$target|$token|rollback"
            }
            $startInfo.FileName = Join-Path $repository "start-widget.exe"
            $startInfo.Arguments = ""
        } else {
            $arguments = @(
                "--update",
                "--repo", $repository,
                "--state-dir", $stateDirectory,
                "--parent-pid", "0",
                "--keeper-pid", "0",
                "--expected", $expected,
                "--target", $target,
                "--handoff-ready", $handoff,
                "--app-ready", $appReady,
                "--log", $log,
                "--result", $result,
                "--token", $token
            )
            $startInfo.FileName = $script:BuiltLauncher
            $startInfo.Arguments = (($arguments | ForEach-Object { Quote-Argument $_ }) -join " ")
        }
        $process = [Diagnostics.Process]::Start($startInfo)
        if (!$process.WaitForExit(90000)) {
            $process.Kill()
            throw "Updater case $Name timed out."
        }
        $process.Dispose()

        if ($Fault -eq "legacy-resume" -or $Fault -eq "rollback-resume") {
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            do {
                $actualHead = Invoke-Git $repository @("rev-parse", "HEAD")
                $pendingFiles = @(Get-ChildItem -LiteralPath $stateDirectory -Filter "update.pending" -ErrorAction SilentlyContinue)
                $resumedHead = if ($Fault -eq "rollback-resume") { $expected } else { $target }
                if ($actualHead -eq $resumedHead -and $pendingFiles.Count -eq 0) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
        }

        $actualHead = Invoke-Git $repository @("rev-parse", "HEAD")
        $rollbackFault = $Fault -eq "bad-launcher" -or $Fault -eq "runtime" -or
            $Fault -eq "unstable" -or $Fault -eq "rollback-resume"
        $expectedHead = if ($rollbackFault) { $expected } else { $target }
        if ($actualHead -ne $expectedHead) {
            throw "$Name ended at $actualHead instead of $expectedHead."
        }
        $status = Invoke-Git $repository @("status", "--porcelain", "--untracked-files=all")
        if ($status) { throw "$Name left a dirty repository:`n$status" }
        foreach ($marker in @("update.lock", "update.pending", "handoff.ready", "app.ready")) {
            if (Test-Path -LiteralPath (Join-Path $stateDirectory $marker)) {
                throw "$Name left $marker behind."
            }
        }
        if (Test-Path -LiteralPath (Join-Path $stateBase "update.pending")) {
            throw "$Name left its legacy pending marker behind."
        }
        $readyFiles = @(Get-ChildItem -LiteralPath $stateDirectory -Filter "update-*.ready" -ErrorAction SilentlyContinue)
        if ($readyFiles.Count -ne 0) { throw "$Name left ready markers behind." }
        $logContent = [IO.File]::ReadAllText($log)
        if (!$rollbackFault -and !$logContent.Contains("Update completed successfully.")) {
            throw "$Name did not report update success."
        }
        if ($rollbackFault -and !$logContent.Contains("previous version was restored")) {
            throw "$Name did not report a successful rollback."
        }
        if ($rollbackFault -and $Fault -ne "rollback-resume" -and !(Test-Path -LiteralPath $result)) {
            throw "$Name did not preserve its update failure result."
        }
        $npmCommands = [IO.File]::ReadAllText($npmLog)
        if (!$npmCommands.Contains("ci") -or !$npmCommands.Contains("run check") -or
            $npmCommands.Contains("install")) {
            throw "$Name did not run the deterministic dependency and verification commands."
        }
        if ($Fault -eq "job-containment") {
            if (!(Test-Path -LiteralPath $containedChildPid)) {
                throw "$Name did not start its containment probe child."
            }
            $childProcessId = [int][IO.File]::ReadAllText($containedChildPid)
            Start-Sleep -Milliseconds 300
            try {
                $childProcess = [Diagnostics.Process]::GetProcessById($childProcessId)
                $childProcess.Kill()
                $childProcess.Dispose()
                throw "$Name allowed a command descendant to escape its Job Object."
            } catch [ArgumentException] {
            }
        }
    } finally {
        Stop-FakeElectron $events
        $env:PATH = $previousPath
        $env:CODEX_USAGE_TRAY_USER_DATA = $previousUserData
        $env:FAKE_ELECTRON_LOG = $previousLog
        $env:FAKE_ELECTRON_FAIL_VERSION = $previousFailure
        $env:FAKE_ELECTRON_UNSTABLE_VERSION = $previousUnstable
        $env:FAKE_NPM_LOG = $previousNpmLog
        $env:FAKE_NPM_SPAWN_CHILD = $previousSpawnChild
        $env:FAKE_CHILD_EXE = $previousChildExe
        $env:FAKE_CHILD_PID = $previousChildPid
    }
}

if (Test-Path -LiteralPath $ScratchRoot) {
    Remove-Item -LiteralPath $ScratchRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($ScratchRoot) | Out-Null
$script:BuiltLauncher = Join-Path $SourceRoot "start-widget.exe"
$sourceCheckLauncher = Join-Path $ScratchRoot "source-check-launcher.exe"
$script:FakeElectron = Join-Path $ScratchRoot "fake-electron.exe"
$script:LegacyLauncher = Join-Path $ScratchRoot "legacy-launcher.exe"
$script:ContainedChild = Join-Path $ScratchRoot "contained-child.exe"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

& "$SourceRoot\src\launcher\build.ps1" -OutputPath $sourceCheckLauncher
& $csc /nologo /target:winexe "/out:$script:FakeElectron" "$SourceRoot\tests\updater\FakeElectron.cs"
if ($LASTEXITCODE -ne 0) { throw "Could not compile the fake Electron process." }
& $csc /nologo /target:winexe "/out:$script:LegacyLauncher" "$SourceRoot\tests\updater\LegacyLauncher.cs"
if ($LASTEXITCODE -ne 0) { throw "Could not compile the legacy launcher fixture." }
& $csc /nologo /target:winexe "/out:$script:ContainedChild" "$SourceRoot\tests\updater\ContainedChild.cs"
if ($LASTEXITCODE -ne 0) { throw "Could not compile the containment probe child." }

$protocolToken = [Guid]::NewGuid().ToString("N")
$protocolReady = Join-Path $ScratchRoot "tracked-launcher-self-test.ready"
$protocolProcess = Start-Process -FilePath $script:BuiltLauncher -ArgumentList "--self-test", "2", $protocolReady, $protocolToken -Wait -PassThru
$protocolBuildHash = (& node "$SourceRoot\src\launcher\build-hash.cjs").Trim()
if ($protocolProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $protocolReady) -or
    [IO.File]::ReadAllText($protocolReady).Trim() -ne ($protocolToken + "|" + $protocolBuildHash)) {
    throw "The tracked start-widget.exe does not implement the current updater protocol."
}
Remove-Item -LiteralPath $protocolReady -Force

Invoke-UpdateCase "success" ""
Invoke-UpdateCase "bad-launcher" "bad-launcher"
Invoke-UpdateCase "runtime-rollback" "runtime"
Invoke-UpdateCase "unstable-runtime-rollback" "unstable"
Invoke-UpdateCase "job-containment" "job-containment"
Invoke-UpdateCase "crlf-source-hash" "crlf-source"
Invoke-UpdateCase "legacy-resume" "legacy-resume"
Invoke-UpdateCase "rollback-resume" "rollback-resume"
$unicodeCaseName = "unicode-stra" + [char]0x00df + "e"
Invoke-UpdateCase $unicodeCaseName ""

"Updater fault-injection tests passed."
