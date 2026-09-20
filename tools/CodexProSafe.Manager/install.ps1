[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoLaunch,
    [switch]$Rollback,
    [ValidateSet('off', 'read')]
    [string]$CodexDiagnostics
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    $stream = [System.IO.File]::OpenRead($LiteralPath)
    try {
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        }
        finally { $hasher.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Copy-VerifiedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Rollback package is incomplete."
    }
    if ((Get-Sha256Hex -LiteralPath $Source) -ne $ExpectedHash) {
        throw "Rollback package fingerprint verification failed."
    }
    $temporary = $Destination + '.new-' + [Guid]::NewGuid().ToString('N')
    Copy-Item -LiteralPath $Source -Destination $temporary
    if ((Get-Sha256Hex -LiteralPath $temporary) -ne $ExpectedHash) {
        Remove-Item -LiteralPath $temporary -Force
        throw "Staged package fingerprint verification failed."
    }
    [System.IO.File]::Copy($temporary, $Destination, $true)
    Remove-Item -LiteralPath $temporary -Force
}

function Save-RollbackPackage {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$SettingsPath,
        [Parameter(Mandatory = $true)][string]$Reason
    )
    $manager = Join-Path $InstallDirectory 'CodexProSafe.Manager.exe'
    if (-not (Test-Path -LiteralPath $manager -PathType Leaf)) { return $null }
    $helper = Join-Path $InstallDirectory 'CodexProSafe.DiagnosticHelper.exe'
    $manifest = Join-Path $InstallDirectory 'CodexProSafe.DiagnosticHelper.json'
    if (-not (Test-Path -LiteralPath $helper -PathType Leaf) -or
        -not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw 'The installed Manager package is incomplete; refusing to create an ambiguous rollback.'
    }

    $rollbackRoot = Join-Path $InstallDirectory 'Rollback'
    New-Item -ItemType Directory -Force -Path $rollbackRoot | Out-Null
    $backupName = (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' + $Reason + '-' + (Get-Sha256Hex -LiteralPath $manager).Substring(0, 12)
    $backup = Join-Path $rollbackRoot $backupName
    New-Item -ItemType Directory -Path $backup | Out-Null
    Copy-Item -LiteralPath $manager -Destination (Join-Path $backup 'CodexProSafe.Manager.exe')
    Copy-Item -LiteralPath $helper -Destination (Join-Path $backup 'CodexProSafe.DiagnosticHelper.exe')
    Copy-Item -LiteralPath $manifest -Destination (Join-Path $backup 'CodexProSafe.DiagnosticHelper.json')

    $settingsPresent = Test-Path -LiteralPath $SettingsPath -PathType Leaf
    $settingsHash = $null
    $settingsSddl = $null
    if ($settingsPresent) {
        $settingsBackup = Join-Path $backup 'settings.dat'
        Copy-Item -LiteralPath $SettingsPath -Destination $settingsBackup
        $settingsHash = Get-Sha256Hex -LiteralPath $settingsBackup
        $settingsAcl = [System.IO.File]::GetAccessControl($SettingsPath)
        $settingsSddl = $settingsAcl.GetSecurityDescriptorSddlForm(
            [System.Security.AccessControl.AccessControlSections]::Access)
        [System.IO.File]::SetAccessControl($settingsBackup, $settingsAcl)
    }

    [pscustomobject]@{
        schema = 'codexpro-manager-rollback-v1'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        reason = $Reason
        managerSha256 = Get-Sha256Hex -LiteralPath (Join-Path $backup 'CodexProSafe.Manager.exe')
        helperSha256 = Get-Sha256Hex -LiteralPath (Join-Path $backup 'CodexProSafe.DiagnosticHelper.exe')
        manifestSha256 = Get-Sha256Hex -LiteralPath (Join-Path $backup 'CodexProSafe.DiagnosticHelper.json')
        settingsPresent = $settingsPresent
        settingsSha256 = $settingsHash
        settingsSddl = $settingsSddl
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $backup 'rollback.json') -Encoding UTF8
    return $backup
}

function Restore-RollbackPackage {
    param(
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$SettingsPath
    )
    $metadataPath = Join-Path $Backup 'rollback.json'
    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    if ($metadata.schema -ne 'codexpro-manager-rollback-v1') {
        throw 'Rollback metadata is invalid.'
    }
    Copy-VerifiedFile -Source (Join-Path $Backup 'CodexProSafe.Manager.exe') `
        -Destination (Join-Path $InstallDirectory 'CodexProSafe.Manager.exe') -ExpectedHash $metadata.managerSha256
    Copy-VerifiedFile -Source (Join-Path $Backup 'CodexProSafe.DiagnosticHelper.exe') `
        -Destination (Join-Path $InstallDirectory 'CodexProSafe.DiagnosticHelper.exe') -ExpectedHash $metadata.helperSha256
    Copy-VerifiedFile -Source (Join-Path $Backup 'CodexProSafe.DiagnosticHelper.json') `
        -Destination (Join-Path $InstallDirectory 'CodexProSafe.DiagnosticHelper.json') -ExpectedHash $metadata.manifestSha256

    if ($metadata.settingsPresent) {
        Copy-VerifiedFile -Source (Join-Path $Backup 'settings.dat') -Destination $SettingsPath -ExpectedHash $metadata.settingsSha256
        $settingsAcl = New-Object System.Security.AccessControl.FileSecurity
        $settingsAcl.SetSecurityDescriptorSddlForm(
            [string]$metadata.settingsSddl,
            [System.Security.AccessControl.AccessControlSections]::Access)
        [System.IO.File]::SetAccessControl($SettingsPath, $settingsAcl)
    }
    elseif (Test-Path -LiteralPath $SettingsPath -PathType Leaf) {
        Remove-Item -LiteralPath $SettingsPath -Force
    }
}

$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $project 'bin\CodexProSafe.Manager.exe'
$helperSource = Join-Path $project 'bin\CodexProSafe.DiagnosticHelper.exe'
$helperManifestSource = Join-Path $project 'bin\CodexProSafe.DiagnosticHelper.json'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\CodexProSafe Manager'
$settingsPath = Join-Path $env:LOCALAPPDATA 'CodexProSafe Manager\settings.dat'
$installedExecutable = Join-Path $installDirectory 'CodexProSafe.Manager.exe'
$installedHelper = Join-Path $installDirectory 'CodexProSafe.DiagnosticHelper.exe'
$installedHelperManifest = Join-Path $installDirectory 'CodexProSafe.DiagnosticHelper.json'

if (Get-Process -Name 'CodexProSafe.Manager' -ErrorAction SilentlyContinue) {
    throw 'Exit the running CodexPro-Safe Manager before installing or updating its sealed helper package.'
}

if ($Rollback) {
    if ($PSBoundParameters.ContainsKey('CodexDiagnostics')) {
        throw 'Rollback restores the encrypted settings snapshot; do not combine it with -CodexDiagnostics.'
    }
    $rollbackRoot = Join-Path $installDirectory 'Rollback'
    $candidate = Get-ChildItem -LiteralPath $rollbackRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'rollback.json') -PathType Leaf } |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) { throw 'No verified Manager rollback package is available.' }
    $recovery = Save-RollbackPackage -InstallDirectory $installDirectory -SettingsPath $settingsPath -Reason 'pre-rollback'
    try {
        Restore-RollbackPackage -Backup $candidate.FullName -InstallDirectory $installDirectory -SettingsPath $settingsPath
    }
    catch {
        if ($null -ne $recovery) {
            Restore-RollbackPackage -Backup $recovery -InstallDirectory $installDirectory -SettingsPath $settingsPath
        }
        throw
    }
    [pscustomobject]@{
        RolledBackFrom = $candidate.FullName
        RecoveryBackup = $recovery
        Executable = $installedExecutable
        Sha256 = Get-Sha256Hex -LiteralPath $installedExecutable
    }
    if (-not $NoLaunch) { Start-Process -FilePath $installedExecutable }
    return
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

New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
$rollbackBackup = Save-RollbackPackage -InstallDirectory $installDirectory -SettingsPath $settingsPath -Reason 'pre-update'
try {
    Copy-VerifiedFile -Source $source -Destination $installedExecutable -ExpectedHash (Get-Sha256Hex -LiteralPath $source)
    Copy-VerifiedFile -Source $helperSource -Destination $installedHelper -ExpectedHash (Get-Sha256Hex -LiteralPath $helperSource)
    Copy-VerifiedFile -Source $helperManifestSource -Destination $installedHelperManifest -ExpectedHash (Get-Sha256Hex -LiteralPath $helperManifestSource)

    $seal = Start-Process -FilePath $installedExecutable -ArgumentList '--seal-helper-trust' -Wait -PassThru -WindowStyle Hidden
    if ($seal.ExitCode -ne 0) {
        throw "Manager could not seal the diagnostic helper fingerprint into DPAPI settings (exit $($seal.ExitCode))."
    }

    if ($PSBoundParameters.ContainsKey('CodexDiagnostics')) {
        $modeUpdate = Start-Process -FilePath $installedExecutable `
            -ArgumentList @('--set-codex-diagnostics', $CodexDiagnostics) `
            -Wait -PassThru -WindowStyle Hidden
        if ($modeUpdate.ExitCode -ne 0) {
            throw "Manager could not update the fixed diagnostic mode (exit $($modeUpdate.ExitCode))."
        }
    }
}
catch {
    if ($null -ne $rollbackBackup) {
        Restore-RollbackPackage -Backup $rollbackBackup -InstallDirectory $installDirectory -SettingsPath $settingsPath
    }
    throw
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
    Sha256 = Get-Sha256Hex -LiteralPath $installedExecutable
    DiagnosticHelperSha256 = Get-Sha256Hex -LiteralPath $installedHelper
    CodexDiagnostics = if ($PSBoundParameters.ContainsKey('CodexDiagnostics')) { $CodexDiagnostics } else { 'unchanged' }
    RollbackBackup = $rollbackBackup
}
$result

if (-not $NoLaunch) {
    Start-Process -FilePath $installedExecutable
}
