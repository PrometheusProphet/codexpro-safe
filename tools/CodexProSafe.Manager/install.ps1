[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $project 'bin\CodexProSafe.Manager.exe'

if (-not $NoBuild) {
    & (Join-Path $project 'build.ps1')
}
if (-not (Test-Path -LiteralPath $source)) {
    throw "Built manager executable was not found at $source."
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\CodexProSafe Manager'
$installedExecutable = Join-Path $installDirectory 'CodexProSafe.Manager.exe'
New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
Copy-Item -LiteralPath $source -Destination $installedExecutable -Force

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
    Shortcut = $shortcutPath
    Sha256 = (Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
}
$result

if (-not $NoLaunch) {
    Start-Process -FilePath $installedExecutable
}
