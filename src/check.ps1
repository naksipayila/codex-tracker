param(
    [switch] $SkipBuild
)

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$toolProject = Join-Path $repositoryRoot "tools\CodexTracker.Tooling\CodexTracker.Tooling.csproj"
$arguments = @("run", "--project", $toolProject, "--configuration", "Release", "--", "check")
if ($SkipBuild) { $arguments += "--skip-build" }
& dotnet @arguments
exit $LASTEXITCODE
