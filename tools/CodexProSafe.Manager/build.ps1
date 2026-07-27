[CmdletBinding()]
param(
    [switch]$SkipSelfTest
)

$ErrorActionPreference = 'Stop'
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

$sources = Get-ChildItem -LiteralPath $project -Filter '*.cs' |
    Sort-Object Name |
    ForEach-Object { $_.FullName }
$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Management.dll',
    'System.Security.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll'
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
    SelfTest = if ($SkipSelfTest) { 'skipped' } else { 'passed' }
}
