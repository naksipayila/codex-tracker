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

$script:BuildInputs = @(
    "global.json",
    "src/CodexUsageTray.csproj",
    "src/app.manifest",
    "src/BuildManifest.cs",
    "src/NativeApplication.cs",
    "src/StartupRegistration.cs",
    "src/NativeSettings.cs",
    "src/NativeMethods.cs",
    "src/WidgetWindow.cs",
    "src/TelemetryPanel.cs",
    "src/TelemetryWindow.cs",
    "src/Theme.cs",
    "src/NativeTypes.cs",
    "src/LatrixIntegration.cs",
    "src/UpdateService.cs",
    "src/launcher/Program.cs",
    "src/launcher/build.ps1",
    "src/launcher/icon.ico"
)
$script:RequiredFiles = @($script:BuildInputs)
$script:Csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

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
            [void] $builder.Append([char]([int] $character + ([int] [char] 'a' - [int] [char] 'A')))
        } else {
            [void] $builder.Append($character)
        }
    }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($builder.ToString()))
        return ([BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant()).Substring(0, 16)
    } finally {
        $sha256.Dispose()
    }
}

function Get-NativeBuildHash([string] $Repository) {
    $manifest = New-Object Text.StringBuilder
    foreach ($relativePath in $script:BuildInputs) {
        $path = Join-Path $Repository ($relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))
        [void] $manifest.Append($relativePath).Append("`0")
        if ($relativePath.EndsWith(".ico", [StringComparison]::OrdinalIgnoreCase)) {
            [void] $manifest.Append([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)))
        } else {
            [void] $manifest.Append([IO.File]::ReadAllText($path).Replace("`r`n", "`n").Replace("`r", "`n"))
        }
        [void] $manifest.Append("`0")
    }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest.ToString()))
        ).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
}

function Build-FixtureLauncher([string] $Repository, [string] $OutputPath) {
    $metadataPath = Join-Path ([IO.Path]::GetDirectoryName($OutputPath)) ("metadata-" + [guid]::NewGuid().ToString("N") + ".cs")
    $buildHash = Get-NativeBuildHash $Repository
    Write-Text $metadataPath ("using System.Reflection;`r`n[assembly: AssemblyInformationalVersion(`"build-$buildHash`")]`r`n")
    try {
        & $script:Csc /nologo /target:winexe "/out:$OutputPath" /reference:System.Windows.Forms.dll `
            (Join-Path $Repository "src\launcher\Program.cs") `
            (Join-Path $Repository "src\BuildManifest.cs") `
            (Join-Path $Repository "src\StartupRegistration.cs") `
            (Join-Path $SourceRoot "tests\updater\NativeApplicationStub.cs") `
            $metadataPath
        if ($LASTEXITCODE -ne 0) { throw "Could not compile the native application fixture." }
    } finally {
        [IO.File]::Delete($metadataPath)
    }
}

function Stop-FakeApplication([string] $LogPath) {
    if (!(Test-Path -LiteralPath $LogPath)) { return }
    foreach ($line in [IO.File]::ReadAllLines($LogPath)) {
        $parts = $line.Split('|')
        if ($parts.Length -ne 3) { continue }
        $processId = 0
        if (![int]::TryParse($parts[2], [ref] $processId)) { continue }
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

function Invoke-InstallCase {
    $caseRoot = Join-Path $ScratchRoot "portable-install"
    $portable = Join-Path $caseRoot "portable"
    $installed = Join-Path $caseRoot "installed"
    $probe = Join-Path $caseRoot "installed-directory.txt"
    [IO.Directory]::CreateDirectory($portable) | Out-Null

    Build-FixtureLauncher $SourceRoot (Join-Path $portable "CodexTracker.exe")
    $previousInstallDirectory = $env:CODEX_USAGE_TRAY_INSTALL_DIRECTORY
    $previousSkipMigration = $env:CODEX_USAGE_TRAY_SKIP_STARTUP_MIGRATION
    $previousProbe = $env:FAKE_APPLICATION_INSTALL_PROBE
    try {
        $env:CODEX_USAGE_TRAY_INSTALL_DIRECTORY = $installed
        $env:CODEX_USAGE_TRAY_SKIP_STARTUP_MIGRATION = "1"
        $env:FAKE_APPLICATION_INSTALL_PROBE = $probe
        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = Join-Path $portable "CodexTracker.exe"
        $startInfo.WorkingDirectory = $portable
        $startInfo.UseShellExecute = $false
        $process = [Diagnostics.Process]::Start($startInfo)
        if (!$process.WaitForExit(90000)) {
            $process.Kill()
            throw "Portable install case timed out."
        }
        $process.Dispose()

        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (!(Test-Path -LiteralPath $probe) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
        }
        if (!(Test-Path -LiteralPath (Join-Path $installed "CodexTracker.exe"))) {
            throw "Portable install case did not create the installed executable."
        }
        $installedDirectory = [IO.File]::ReadAllText($probe).Trim()
        if (![string]::Equals(
            [IO.Path]::GetFullPath($installedDirectory).TrimEnd('\', '/'),
            [IO.Path]::GetFullPath($installed).TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Portable install case launched from $installedDirectory instead of $installed."
        }
    } finally {
        $env:CODEX_USAGE_TRAY_INSTALL_DIRECTORY = $previousInstallDirectory
        $env:CODEX_USAGE_TRAY_SKIP_STARTUP_MIGRATION = $previousSkipMigration
        $env:FAKE_APPLICATION_INSTALL_PROBE = $previousProbe
    }
}

function Copy-NativeSources([string] $Repository) {
    foreach ($relativePath in $script:RequiredFiles) {
        $source = Join-Path $SourceRoot ($relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))
        $destination = Join-Path $Repository ($relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($source, $destination, $true)
    }
}

function Enable-ContainmentProbe([string] $ProgramPath) {
    $source = [IO.File]::ReadAllText($ProgramPath)
    $needle = 'if (args.Length != 4 || args[1] != UpdaterProtocolVersion || args[3].Length < 16) return 2;'
    $injection = $needle + @'

            var probeExecutable = Environment.GetEnvironmentVariable("FAKE_SELF_TEST_CHILD");
            var probePidPath = Environment.GetEnvironmentVariable("FAKE_SELF_TEST_CHILD_PID");
            if (!string.IsNullOrEmpty(probeExecutable) && !string.IsNullOrEmpty(probePidPath))
            {
                Process.Start(probeExecutable, QuoteArgument(probePidPath));
                Thread.Sleep(300);
            }
'@
    if (!$source.Contains($needle)) { throw "Could not inject the containment probe." }
    Write-Text $ProgramPath ($source.Replace($needle, $injection))
}

function Invoke-UpdateCase([string] $Name, [string] $Fault) {
    $caseRoot = Join-Path $ScratchRoot $Name
    $repository = Join-Path $caseRoot "repo"
    $stateBase = Join-Path $caseRoot "state"
    $events = Join-Path $caseRoot "application-events.log"
    $containedChildPid = Join-Path $caseRoot "contained-child.pid"
    [IO.Directory]::CreateDirectory($repository) | Out-Null

    & git init --quiet $repository
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize fixture $Name." }
    Invoke-Git $repository @("symbolic-ref", "HEAD", "refs/heads/main") | Out-Null
    Invoke-Git $repository @("config", "user.name", "Updater Test") | Out-Null
    Invoke-Git $repository @("config", "user.email", "updater-test@example.invalid") | Out-Null
    Invoke-Git $repository @("config", "core.autocrlf", "false") | Out-Null

    Copy-NativeSources $repository
    Write-Text (Join-Path $repository ".gitignore") "bin/`nobj/`n"
    Write-Text (Join-Path $repository "src\version.txt") "A`n"
    Build-FixtureLauncher $repository (Join-Path $repository "CodexTracker.exe")
    if ($Fault -eq "source-migration") {
        foreach ($relativePath in $script:RequiredFiles) {
            Remove-Item -LiteralPath (Join-Path $repository ($relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))) -Force
        }
    }
    Invoke-Git $repository @("add", ".") | Out-Null
    Invoke-Git $repository @("commit", "--quiet", "-m", "fixture A") | Out-Null
    $expected = Invoke-Git $repository @("rev-parse", "HEAD")

    if ($Fault -eq "source-migration") { Copy-NativeSources $repository }
    Write-Text (Join-Path $repository "src\version.txt") "B`n"
    if ($Fault -eq "crlf-source") {
        $programPath = Join-Path $repository "src\launcher\Program.cs"
        $programContent = [IO.File]::ReadAllText($programPath).Replace("`r`n", "`n").Replace("`r", "`n")
        [IO.File]::WriteAllText($programPath, $programContent.Replace("`n", "`r`n"), $utf8)
    }
    if ($Fault -eq "job-containment") {
        Enable-ContainmentProbe (Join-Path $repository "src\launcher\Program.cs")
    }
    Build-FixtureLauncher $repository (Join-Path $repository "CodexTracker.exe")
    if ($Fault -eq "bad-launcher") {
        [IO.File]::Copy($script:InvalidLauncher, (Join-Path $repository "CodexTracker.exe"), $true)
    }
    Invoke-Git $repository @("add", ".") | Out-Null
    Invoke-Git $repository @("commit", "--quiet", "-m", "fixture B") | Out-Null
    $target = Invoke-Git $repository @("rev-parse", "HEAD")
    Invoke-Git $repository @("reset", "--hard", $expected) | Out-Null

    $repositoryId = Get-RepositoryId $repository
    $stateDirectory = Join-Path $stateBase "updates\$repositoryId"
    [IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
    $token = [Guid]::NewGuid().ToString("N")
    $handoff = Join-Path $stateDirectory "handoff.ready"
    $appReady = Join-Path $stateDirectory "app.ready"
    $log = Join-Path $stateDirectory "update.log"
    $result = Join-Path $stateDirectory "update-result.json"

    $previousUserData = $env:CODEX_USAGE_TRAY_USER_DATA
    $previousLog = $env:FAKE_APPLICATION_LOG
    $previousFailure = $env:FAKE_APPLICATION_FAIL_VERSION
    $previousUnstable = $env:FAKE_APPLICATION_UNSTABLE_VERSION
    $previousChild = $env:FAKE_SELF_TEST_CHILD
    $previousChildPid = $env:FAKE_SELF_TEST_CHILD_PID
    try {
        $env:CODEX_USAGE_TRAY_USER_DATA = $stateBase
        $env:FAKE_APPLICATION_LOG = $events
        $env:FAKE_APPLICATION_FAIL_VERSION = if ($Fault -eq "runtime") { "B" } else { "" }
        $env:FAKE_APPLICATION_UNSTABLE_VERSION = if ($Fault -eq "unstable") { "B" } else { "" }
        $env:FAKE_SELF_TEST_CHILD = if ($Fault -eq "job-containment") { $script:ContainedChild } else { "" }
        $env:FAKE_SELF_TEST_CHILD_PID = $containedChildPid

        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.WorkingDirectory = $repository
        $startInfo.UseShellExecute = $false
        if ($Fault -eq "legacy-resume" -or $Fault -eq "rollback-resume") {
            if ($Fault -eq "legacy-resume") {
                Write-Text (Join-Path $stateBase "update.pending") "$expected|$target"
            } else {
                $identity = [IO.Path]::GetFullPath($repository).TrimEnd('\', '/')
                $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($identity))
                Write-Text (Join-Path $stateDirectory "update.pending") "v3|$encoded|$expected|$target|$token|rollback"
            }
            $startInfo.FileName = Join-Path $repository "CodexTracker.exe"
        } else {
            $arguments = @(
                "--update", "--repo", $repository, "--state-dir", $stateDirectory,
                "--parent-pid", "0", "--keeper-pid", "0", "--expected", $expected,
                "--target", $target, "--handoff-ready", $handoff, "--app-ready", $appReady,
                "--log", $log, "--result", $result, "--token", $token
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
                $pending = Join-Path $stateDirectory "update.pending"
                $resumedHead = if ($Fault -eq "rollback-resume") { $expected } else { $target }
                if ($actualHead -eq $resumedHead -and !(Test-Path -LiteralPath $pending)) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
        }

        $actualHead = Invoke-Git $repository @("rev-parse", "HEAD")
        $rollbackFault = $Fault -in @("bad-launcher", "runtime", "unstable", "rollback-resume")
        $expectedHead = if ($rollbackFault) { $expected } else { $target }
        if ($actualHead -ne $expectedHead) { throw "$Name ended at $actualHead instead of $expectedHead." }
        $status = Invoke-Git $repository @("status", "--porcelain", "--untracked-files=all")
        if ($status) { throw "$Name left a dirty repository:`n$status" }
        foreach ($marker in @("update.lock", "update.pending", "handoff.ready", "app.ready")) {
            if (Test-Path -LiteralPath (Join-Path $stateDirectory $marker)) { throw "$Name left $marker behind." }
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
        if ($Fault -eq "job-containment") {
            if (!(Test-Path -LiteralPath $containedChildPid)) { throw "$Name did not start its containment probe child." }
            $childProcessId = [int] [IO.File]::ReadAllText($containedChildPid)
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
        Stop-FakeApplication $events
        $env:CODEX_USAGE_TRAY_USER_DATA = $previousUserData
        $env:FAKE_APPLICATION_LOG = $previousLog
        $env:FAKE_APPLICATION_FAIL_VERSION = $previousFailure
        $env:FAKE_APPLICATION_UNSTABLE_VERSION = $previousUnstable
        $env:FAKE_SELF_TEST_CHILD = $previousChild
        $env:FAKE_SELF_TEST_CHILD_PID = $previousChildPid
    }
}

if (Test-Path -LiteralPath $ScratchRoot) {
    Remove-Item -LiteralPath $ScratchRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($ScratchRoot) | Out-Null
$script:BuiltLauncher = Join-Path $SourceRoot "CodexTracker.exe"
$sourceCheckLauncher = Join-Path $ScratchRoot "source-check-launcher.exe"
$script:InvalidLauncher = Join-Path $ScratchRoot "invalid-launcher.exe"
$script:ContainedChild = Join-Path $ScratchRoot "contained-child.exe"

& "$SourceRoot\src\launcher\build.ps1" -OutputPath $sourceCheckLauncher
& $script:Csc /nologo /target:winexe "/out:$script:InvalidLauncher" "$SourceRoot\tests\updater\InvalidLauncher.cs"
if ($LASTEXITCODE -ne 0) { throw "Could not compile the invalid launcher fixture." }
& $script:Csc /nologo /target:winexe "/out:$script:ContainedChild" "$SourceRoot\tests\updater\ContainedChild.cs"
if ($LASTEXITCODE -ne 0) { throw "Could not compile the containment probe child." }

$protocolToken = [Guid]::NewGuid().ToString("N")
$protocolReady = Join-Path $ScratchRoot "tracked-launcher-self-test.ready"
$protocolProcess = Start-Process -FilePath $script:BuiltLauncher -ArgumentList "--self-test", "3", $protocolReady, $protocolToken -Wait -PassThru
$protocolBuildHash = Get-NativeBuildHash $SourceRoot
if ($protocolProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $protocolReady) -or
    [IO.File]::ReadAllText($protocolReady).Trim() -ne ($protocolToken + "|" + $protocolBuildHash)) {
    throw "The tracked CodexTracker.exe does not implement the current native updater protocol."
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
Invoke-UpdateCase "source-migration" "source-migration"
Invoke-InstallCase
$unicodeCaseName = "unicode-stra" + [char] 0x00df + "e"
Invoke-UpdateCase $unicodeCaseName ""

"Native updater fault-injection tests passed."
