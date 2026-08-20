[CmdletBinding()]
param(
    [switch]$SkipSelfTest
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $project 'bin'
$obj = Join-Path $project 'obj'
$framework64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$compiler = if (Test-Path -LiteralPath $framework64) { $framework64 } else { $framework32 }

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

New-Item -ItemType Directory -Force -Path $bin, $obj | Out-Null

$helperSources = @(
    (Join-Path $project 'DiagnosticHelper\Program.cs'),
    (Join-Path $project 'DiagnosticHelper\MaintenanceNativeMethods.cs'),
    (Join-Path $project 'DiagnosticHelper\MaintenanceFilesystemProvider.cs'),
    (Join-Path $project 'DiagnosticHelper\MaintenanceProtocol.cs'),
    (Join-Path $project 'DiagnosticHelper\MaintenanceFilesystemSelfTest.cs')
)
$helperOutput = Join-Path $bin 'CodexProSafe.DiagnosticHelper.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warn:4 `
    ('/out:' + $helperOutput) '/reference:System.dll' '/reference:System.Core.dll' `
    '/reference:System.Web.Extensions.dll' $helperSources
if ($LASTEXITCODE -ne 0) {
    throw "Diagnostic helper compilation failed with exit code $LASTEXITCODE."
}

if (-not $SkipSelfTest) {
    $helperTest = Start-Process -FilePath $helperOutput -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
    if ($helperTest.ExitCode -ne 0) {
        throw "Diagnostic helper self-test failed with exit code $($helperTest.ExitCode)."
    }
    $maintenanceHelperTest = Start-Process -FilePath $helperOutput -ArgumentList '--self-test-maintenance-fs' -Wait -PassThru -WindowStyle Hidden
    if ($maintenanceHelperTest.ExitCode -ne 0) {
        throw "Maintenance filesystem helper self-test failed with exit code $($maintenanceHelperTest.ExitCode)."
    }
}

$launcherProject = Join-Path $project '..\CodexProSafe.MaintenanceFsLauncher'
$launcherSources = @(
    (Join-Path $launcherProject 'StrictJson.cs'),
    (Join-Path $launcherProject 'PackageTrust.cs'),
    (Join-Path $launcherProject 'NativeChild.cs'),
    (Join-Path $launcherProject 'Program.cs')
)
foreach ($launcherSource in $launcherSources) {
    if (-not (Test-Path -LiteralPath $launcherSource -PathType Leaf)) {
        throw "Missing maintenance filesystem launcher source: $launcherSource"
    }
}
$launcherOutput = Join-Path $bin 'CodexProSafe.MaintenanceFsLauncher.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warn:4 `
    ('/out:' + $launcherOutput) '/reference:System.dll' '/reference:System.Core.dll' `
    '/reference:System.Web.Extensions.dll' $launcherSources
if ($LASTEXITCODE -ne 0) {
    throw "Maintenance filesystem launcher compilation failed with exit code $LASTEXITCODE."
}
if (-not $SkipSelfTest) {
    $launcherTest = Start-Process -FilePath $launcherOutput -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
    if ($launcherTest.ExitCode -ne 0) {
        throw "Maintenance filesystem launcher self-test failed with exit code $($launcherTest.ExitCode)."
    }
}

$helperHash = (Get-FileHash -LiteralPath $helperOutput -Algorithm SHA256).Hash.ToLowerInvariant()
$helperManifest = Join-Path $bin 'CodexProSafe.DiagnosticHelper.json'
[pscustomobject]@{
    protocolVersion = 'codexpro-diagnostic-v1'
    maintenanceFsProtocolVersion = 'codexpro-maintenance-fs-v1'
    executable = 'CodexProSafe.DiagnosticHelper.exe'
    sha256 = $helperHash
} | ConvertTo-Json -Compress | Set-Content -LiteralPath $helperManifest -Encoding UTF8

$sources = Get-ChildItem -LiteralPath $project -Filter '*.cs' |
    Sort-Object Name |
    ForEach-Object { $_.FullName }
$gacRoot = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
$uiAutomationClient = Get-ChildItem -LiteralPath (Join-Path $gacRoot 'UIAutomationClient') -Recurse -Filter 'UIAutomationClient.dll' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
$uiAutomationTypes = Get-ChildItem -LiteralPath (Join-Path $gacRoot 'UIAutomationTypes') -Recurse -Filter 'UIAutomationTypes.dll' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $uiAutomationClient -or -not $uiAutomationTypes) {
    throw 'The Windows UI Automation assemblies were not found.'
}
$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Management.dll',
    'System.Security.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll',
    $uiAutomationClient,
    $uiAutomationTypes
)
$referenceArgs = $references | ForEach-Object { '/reference:' + $_ }
$output = Join-Path $bin 'CodexProSafe.Manager.exe'
$manifest = Join-Path $project 'app.manifest'

& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 `
    ('/out:' + $output) ('/win32manifest:' + $manifest) `
    $referenceArgs $sources
if ($LASTEXITCODE -ne 0) {
    throw "Manager compilation failed with exit code $LASTEXITCODE."
}

if (-not $SkipSelfTest) {
    $process = Start-Process -FilePath $output -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        $report = Join-Path ([System.IO.Path]::GetTempPath()) 'CodexProSafe.Manager.self-test.txt'
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
        throw "Manager self-test failed with exit code $($process.ExitCode)."
    }
}

$hash = Get-FileHash -LiteralPath $output -Algorithm SHA256
[pscustomobject]@{
    Executable = $output
    Sha256 = $hash.Hash.ToLowerInvariant()
    DiagnosticHelper = $helperOutput
    DiagnosticHelperSha256 = $helperHash
    SelfTest = if ($SkipSelfTest) { 'skipped' } else { 'passed' }
}
