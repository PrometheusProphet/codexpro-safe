$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $repository 'tools\CodexProSafe.Manager\install.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) {
    throw "Windows Manager installer has PowerShell parse errors."
}

$parameters = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
foreach ($required in @('NoBuild', 'NoLaunch', 'Rollback', 'CodexDiagnostics')) {
    if ($parameters -notcontains $required) { throw "Windows Manager installer is missing parameter $required." }
}

$source = Get-Content -Raw -LiteralPath $installer
foreach ($required in @(
    'Save-RollbackPackage',
    'Restore-RollbackPackage',
    'Copy-VerifiedFile',
    'codexpro-manager-rollback-v1',
    'managerSha256',
    'helperSha256',
    'manifestSha256',
    'settingsSha256',
    'settingsSddl',
    '[System.IO.File]::Copy',
    "Reason 'pre-update'",
    "Reason 'pre-rollback'"
)) {
    if ($source.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Windows Manager installer rollback contract is missing $required."
    }
}

if ($source -notmatch "Get-Process\s+-Name\s+'CodexProSafe\.Manager'") {
    throw 'Windows Manager installer no longer refuses replacement while the Manager is running.'
}
if ($source -notmatch 'if\s*\(\$Rollback\)') {
    throw 'Windows Manager installer rollback branch is unavailable.'
}

$functionNames = @('Get-Sha256Hex', 'Copy-VerifiedFile', 'Save-RollbackPackage', 'Restore-RollbackPackage')
$definitions = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $functionNames -contains $node.Name
}, $true))
if ($definitions.Count -ne $functionNames.Count) {
    throw 'Windows Manager installer rollback helpers could not be isolated for testing.'
}
foreach ($definition in $definitions) {
    . ([scriptblock]::Create($definition.Extent.Text))
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('CodexProSafe.Manager.rollback-test.' + [Guid]::NewGuid().ToString('N'))
$resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$resolvedRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
if (-not $resolvedRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Rollback test directory escaped the system temporary directory.'
}
New-Item -ItemType Directory -Path $resolvedRoot | Out-Null
try {
    $install = Join-Path $resolvedRoot 'install'
    $settingsDirectory = Join-Path $resolvedRoot 'settings'
    New-Item -ItemType Directory -Path $install, $settingsDirectory | Out-Null
    $manager = Join-Path $install 'CodexProSafe.Manager.exe'
    $helper = Join-Path $install 'CodexProSafe.DiagnosticHelper.exe'
    $manifest = Join-Path $install 'CodexProSafe.DiagnosticHelper.json'
    $settings = Join-Path $settingsDirectory 'settings.dat'
    [System.IO.File]::WriteAllText($manager, 'manager-original')
    [System.IO.File]::WriteAllText($helper, 'helper-original')
    [System.IO.File]::WriteAllText($manifest, 'manifest-original')
    [System.IO.File]::WriteAllText($settings, 'opaque-dpapi-bytes')
    $expected = @{
        manager = Get-Sha256Hex -LiteralPath $manager
        helper = Get-Sha256Hex -LiteralPath $helper
        manifest = Get-Sha256Hex -LiteralPath $manifest
        settings = Get-Sha256Hex -LiteralPath $settings
    }

    $backup = Save-RollbackPackage -InstallDirectory $install -SettingsPath $settings -Reason 'synthetic'
    [System.IO.File]::WriteAllText($manager, 'manager-updated')
    [System.IO.File]::WriteAllText($helper, 'helper-updated')
    [System.IO.File]::WriteAllText($manifest, 'manifest-updated')
    [System.IO.File]::WriteAllText($settings, 'settings-updated')
    Restore-RollbackPackage -Backup $backup -InstallDirectory $install -SettingsPath $settings
    foreach ($entry in @(
        @($manager, $expected.manager),
        @($helper, $expected.helper),
        @($manifest, $expected.manifest),
        @($settings, $expected.settings)
    )) {
        if ((Get-Sha256Hex -LiteralPath $entry[0]) -ne $entry[1]) {
            throw 'Windows Manager synthetic rollback was not byte-identical.'
        }
    }

    [System.IO.File]::AppendAllText((Join-Path $backup 'CodexProSafe.Manager.exe'), 'tamper')
    $tamperRejected = $false
    try { Restore-RollbackPackage -Backup $backup -InstallDirectory $install -SettingsPath $settings }
    catch { $tamperRejected = $true }
    if (-not $tamperRejected) { throw 'Windows Manager rollback accepted a tampered package.' }
}
finally {
    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}

Write-Output 'windows manager install/rollback contract passed'
