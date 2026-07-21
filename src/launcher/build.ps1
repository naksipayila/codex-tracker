param(
    [string] $OutputPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "CodexTracker.exe"),
    [switch] $SelfContained,
    [string] $Version = "1.0.0"
)

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolProject = Join-Path $repositoryRoot "tools\CodexTracker.Tooling\CodexTracker.Tooling.csproj"
$arguments = @(
    "run", "--project", $toolProject, "--configuration", "Release", "--",
    "build", "--output", ([IO.Path]::GetFullPath($OutputPath)), "--version", $Version
)
if ($SelfContained) { $arguments += "--self-contained" }
& dotnet @arguments
exit $LASTEXITCODE
