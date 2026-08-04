[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$repository = Split-Path -Parent $PSScriptRoot
$managerProject = Join-Path $repository 'tools\CodexProSafe.Manager'
$installer = Join-Path $managerProject 'install.ps1'
$program = Join-Path $managerProject 'Program.cs'
$mainForm = Join-Path $managerProject 'MainForm.cs'
$managerExecutable = Join-Path $managerProject 'bin\CodexProSafe.Manager.exe'

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Manager installer did not parse.' }
$parameterNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
$expectedParameters = @('NoBuild', 'NoLaunch', 'CodexDiagnostics')
if (@($parameterNames | Where-Object { $_ -notin $expectedParameters }).Count -ne 0 -or
    @($expectedParameters | Where-Object { $_ -notin $parameterNames }).Count -ne 0) {
    throw 'Manager installer exposed an unexpected parameter surface.'
}
$installerText = Get-Content -LiteralPath $installer -Raw
if ($installerText -notmatch "\[ValidateSet\('off',\s*'read'\)\]" -or
    $installerText -notmatch "--set-codex-diagnostics") {
    throw 'Manager installer did not retain the fixed diagnostic-mode contract.'
}

$programText = Get-Content -LiteralPath $program -Raw
$mainFormText = Get-Content -LiteralPath $mainForm -Raw
if ($programText -notmatch '--set-codex-diagnostics' -or $programText -notmatch '--safe-status') {
    throw 'Manager operational commands were not registered before GUI startup.'
}
if ($mainFormText -match '\bRichTextBox\b') {
    throw 'Manager still uses a native text log control.'
}

& (Join-Path $managerProject 'build.ps1') | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $managerExecutable -PathType Leaf)) {
    throw 'Manager privacy build failed.'
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('CodexProSafe.Manager.cli-test.' + [guid]::NewGuid().ToString('N'))
$resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$resolvedRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
if (-not $resolvedRoot.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'CLI test directory escaped the system temporary directory.'
}
New-Item -ItemType Directory -Path $resolvedRoot | Out-Null
try {
    $stdout = Join-Path $resolvedRoot 'stdout.txt'
    $stderr = Join-Path $resolvedRoot 'stderr.txt'
    $process = Start-Process -FilePath $managerExecutable `
        -ArgumentList @('--set-codex-diagnostics', 'invalid') `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 2) { throw 'Invalid diagnostic mode did not return the fixed usage exit code.' }
    $expected = '{"schema":"codexpro-manager-command-v1","command":"set-codex-diagnostics","status":"invalid_request","mode":"unavailable","restartRequired":false}'
    if ((Get-Content -LiteralPath $stdout -Raw).Trim() -ne $expected) {
        throw 'Invalid diagnostic mode did not emit the fixed sanitized envelope.'
    }
    if ((Get-Item -LiteralPath $stderr).Length -ne 0) {
        throw 'Invalid diagnostic mode wrote to stderr.'
    }

    $statusStdout = Join-Path $resolvedRoot 'status-stdout.txt'
    $statusStderr = Join-Path $resolvedRoot 'status-stderr.txt'
    $statusProcess = Start-Process -FilePath $managerExecutable `
        -ArgumentList @('--safe-status', 'unexpected') `
        -RedirectStandardOutput $statusStdout `
        -RedirectStandardError $statusStderr `
        -Wait -PassThru -WindowStyle Hidden
    if ($statusProcess.ExitCode -ne 2) { throw 'Invalid safe-status usage did not return the fixed usage exit code.' }
    $status = Get-Content -LiteralPath $statusStdout -Raw | ConvertFrom-Json
    $statusFields = @($status.PSObject.Properties.Name)
    $expectedStatusFields = @(
        'schema', 'savedDiagnosticMode', 'installedHelperTrust', 'connectorLocalHealthy',
        'tunnelLocalProcessHealthy', 'tunnelAuthenticatedReady', 'restartRequired', 'overall', 'limitation'
    )
    if (@($statusFields | Where-Object { $_ -notin $expectedStatusFields }).Count -ne 0 -or
        @($expectedStatusFields | Where-Object { $_ -notin $statusFields }).Count -ne 0) {
        throw 'Invalid safe-status usage emitted fields outside the fixed schema.'
    }
    if ((Get-Item -LiteralPath $statusStderr).Length -ne 0) {
        throw 'Invalid safe-status usage wrote to stderr.'
    }
}
finally {
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}

Write-Output 'manager operational privacy tests passed'
