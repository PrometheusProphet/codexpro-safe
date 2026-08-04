[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $project 'bin\CodexProSafe.Manager.exe'
$helperSource = Join-Path $project 'bin\CodexProSafe.DiagnosticHelper.exe'
$helperManifestSource = Join-Path $project 'bin\CodexProSafe.DiagnosticHelper.json'

if (Get-Process -Name 'CodexProSafe.Manager' -ErrorAction SilentlyContinue) {
    throw 'Exit the running CodexPro-Safe Manager before installing or updating its sealed helper package.'
}

if (-not $NoBuild) {
    & (Join-Path $project 'build.ps1')
}
if (-not (Test-Path -LiteralPath $source)) {
    throw "Built manager executable was not found at $source."
}
if (-not (Test-Path -LiteralPath $helperSource) -or -not (Test-Path -LiteralPath $helperManifestSource)) {
    throw 'Built diagnostic helper package was not found. Run build.ps1 first.'
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\CodexProSafe Manager'
$installedExecutable = Join-Path $installDirectory 'CodexProSafe.Manager.exe'
$installedHelper = Join-Path $installDirectory 'CodexProSafe.DiagnosticHelper.exe'
$installedHelperManifest = Join-Path $installDirectory 'CodexProSafe.DiagnosticHelper.json'
New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
Copy-Item -LiteralPath $source -Destination $installedExecutable -Force
Copy-Item -LiteralPath $helperSource -Destination $installedHelper -Force
Copy-Item -LiteralPath $helperManifestSource -Destination $installedHelperManifest -Force

$seal = Start-Process -FilePath $installedExecutable -ArgumentList '--seal-helper-trust' -Wait -PassThru -WindowStyle Hidden
if ($seal.ExitCode -ne 0) {
    throw "Manager could not seal the diagnostic helper fingerprint into DPAPI settings (exit $($seal.ExitCode))."
}

$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'CodexPro-Safe Manager.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = $installedExecutable + ',0'
$shortcut.Description = 'Start, restart, stop, and monitor CodexPro-Safe'
$shortcut.Save()

$result = [pscustomobject]@{
    Executable = $installedExecutable
    DiagnosticHelper = $installedHelper
    Shortcut = $shortcutPath
    Sha256 = (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    DiagnosticHelperSha256 = (Get-FileHash -LiteralPath $installedHelper -Algorithm SHA256).Hash.ToLowerInvariant()
}
$result

if (-not $NoLaunch) {
    Start-Process -FilePath $installedExecutable
}
