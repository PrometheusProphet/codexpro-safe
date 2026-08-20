[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$SkipToolHiveProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
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

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot ".ai-bridge\mcp-runtime-status.md"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repoRoot $OutputPath
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

$manualBegin = "<!-- BEGIN MANUAL VERIFICATION -->"
$manualEnd = "<!-- END MANUAL VERIFICATION -->"

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$Arguments = @()
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $text
    }
}

function Get-ProcessObservation {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    $matches = @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $Names -contains $_.ProcessName } |
            Select-Object -ExpandProperty ProcessName -Unique |
            Sort-Object
    )

    if ($matches.Count -eq 0) {
        return "not observed"
    }

    return "observed running (" + ($matches -join ", ") + ")"
}

function Get-FileVersionText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return "not installed at the expected path"
    }

    $version = (Get-Item -LiteralPath $Path).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        return "version metadata unavailable"
    }

    return $version.Trim()
}

function Get-PreservedManualNotes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $existing = [System.IO.File]::ReadAllText($Path)
        $pattern = "(?s)" +
            [regex]::Escape($manualBegin) +
            "\s*(.*?)\s*" +
            [regex]::Escape($manualEnd)
        $match = [regex]::Match($existing, $pattern)
        if ($match.Success) {
            return $match.Groups[1].Value.Trim()
        }
    }

    return @"
- No manual verification notes have been recorded since automatic snapshot
  refresh was enabled.
- Add only sanitized evidence here. Never record credentials, organization IDs,
  real tunnel IDs, private URLs, or secret-bearing commands.
"@.Trim()
}

$gitBranchResult = Invoke-ExternalCommand -FilePath "git" -Arguments @(
    "-C", $repoRoot, "branch", "--show-current"
)
if ($gitBranchResult.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($gitBranchResult.Output)) {
    throw "Unable to determine the current Git branch."
}
$gitBranch = $gitBranchResult.Output

$gitHeadResult = Invoke-ExternalCommand -FilePath "git" -Arguments @(
    "-C", $repoRoot, "rev-parse", "--short", "HEAD"
)
if ($gitHeadResult.ExitCode -ne 0) {
    throw "Unable to determine the current Git HEAD."
}
$gitHead = $gitHeadResult.Output

$gitStatusResult = Invoke-ExternalCommand -FilePath "git" -Arguments @(
    "-C", $repoRoot, "status", "--porcelain=v1"
)
if ($gitStatusResult.ExitCode -ne 0) {
    throw "Unable to inspect the Git worktree."
}
$worktreeChangeCount = 0
if (-not [string]::IsNullOrWhiteSpace($gitStatusResult.Output)) {
    $worktreeChangeCount = @($gitStatusResult.Output -split "\r?\n").Count
}

$remoteRef = "refs/remotes/origin/$gitBranch"
$remoteResult = Invoke-ExternalCommand -FilePath "git" -Arguments @(
    "-C", $repoRoot, "rev-parse", "--verify", $remoteRef
)
$alignment = "origin/$gitBranch is not available locally"
if ($remoteResult.ExitCode -eq 0) {
    $aheadBehindResult = Invoke-ExternalCommand -FilePath "git" -Arguments @(
        "-C", $repoRoot, "rev-list", "--left-right", "--count",
        "$remoteRef...HEAD"
    )
    if ($aheadBehindResult.ExitCode -eq 0) {
        $counts = @($aheadBehindResult.Output -split "\s+")
        if ($counts.Count -ge 2) {
            $behind = [int]$counts[0]
            $ahead = [int]$counts[1]
            if ($ahead -eq 0 -and $behind -eq 0) {
                $alignment = "matched origin/$gitBranch"
            }
            else {
                $alignment = "ahead $ahead, behind $behind versus origin/$gitBranch"
            }
        }
    }
}

$managerPath = Join-Path $env:LOCALAPPDATA `
    "Programs\CodexProSafe Manager\CodexProSafe.Manager.exe"
$managerVersion = Get-FileVersionText -Path $managerPath
$managerHash = "unavailable"
if (Test-Path -LiteralPath $managerPath -PathType Leaf) {
    $managerHash = Get-Sha256Hex -LiteralPath $managerPath
}
$managerProcess = Get-ProcessObservation -Names @("CodexProSafe.Manager")
$tunnelProcess = Get-ProcessObservation -Names @("tunnel-client")

$toolHivePath = Join-Path $env:LOCALAPPDATA "ToolHive\bin\thv.exe"
$toolHiveVersion = "not installed at the expected path"
$toolHiveProbe = "not run because the ToolHive CLI is unavailable"
if (Test-Path -LiteralPath $toolHivePath -PathType Leaf) {
    $versionResult = Invoke-ExternalCommand -FilePath $toolHivePath -Arguments @("version")
    $versionLine = @(
        $versionResult.Output -split "\r?\n" |
            Where-Object { $_ -match "^ToolHive v" }
    )
    if ($versionResult.ExitCode -eq 0 -and $versionLine.Count -gt 0) {
        $toolHiveVersion = $versionLine[0].Trim()
    }
    else {
        $toolHiveVersion = "version query failed"
    }

    if ($SkipToolHiveProbe) {
        $toolHiveProbe = "skipped by request"
    }
    else {
        $probeResult = Invoke-ExternalCommand -FilePath $toolHivePath -Arguments @("list")
        if ($probeResult.ExitCode -eq 0) {
            $toolHiveProbe = "succeeded"
        }
        elseif ($probeResult.Output -match "no container runtime available") {
            $toolHiveProbe = "failed: no container runtime available"
        }
        else {
            $toolHiveProbe = "failed with exit code $($probeResult.ExitCode); inspect ToolHive directly"
        }
    }
}

$studioRoot = Join-Path $env:LOCALAPPDATA "ToolHive"
$studioDirectories = @()
if (Test-Path -LiteralPath $studioRoot -PathType Container) {
    $studioDirectories = @(
        Get-ChildItem -LiteralPath $studioRoot -Directory -Filter "app-*" -ErrorAction SilentlyContinue |
            Sort-Object -Property Name -Descending
    )
}
$studioVersion = "not installed at the expected path"
$studioPath = "not installed at the expected path"
if ($studioDirectories.Count -gt 0) {
    $studioPath = $studioDirectories[0].FullName
    $studioVersion = $studioDirectories[0].Name.Substring(4)
}
$toolHiveProcess = Get-ProcessObservation -Names @("ToolHive", "toolhive", "thv")

$manualNotes = Get-PreservedManualNotes -Path $OutputPath
$manualNotes = ($manualNotes -replace "\r?\n", [Environment]::NewLine)
$now = Get-Date
$snapshotTime = $now.ToString("yyyy-MM-dd HH:mm:ss zzz")
$timeZone = [System.TimeZoneInfo]::Local.Id

$lines = @(
    "# Local MCP Runtime Status",
    "",
    "Snapshot time: $snapshotTime ($timeZone)",
    "",
    "Generated by ``scripts/refresh-mcp-runtime-status.ps1``. This is a",
    "sanitized, machine-specific snapshot for task continuity. It is ignored by",
    "Git and is not source authority. Verify current state read-only before",
    "operational changes.",
    "",
    "## Repository baseline",
    "",
    "- Repository: ``$repoRoot``",
    "- Branch: ``$gitBranch``",
    "- HEAD: ``$gitHead``",
    "- Alignment at snapshot: $alignment",
    "- Worktree entries reported by Git: $worktreeChangeCount",
    "",
    "## CodexPro-Safe Manager",
    "",
    "- Expected executable: ``$managerPath``",
    "- Installed version: ``$managerVersion``",
    "- Installed executable SHA-256: ``$managerHash``",
    "- Manager process: $managerProcess",
    "- Tunnel-client process: $tunnelProcess",
    "",
    "Process presence and an installed executable do not prove authenticated",
    "tunnel readiness, profile ownership, connector health, or plugin callability.",
    "",
    "## ToolHive",
    "",
    "- CLI path: ``$toolHivePath``",
    "- CLI version query: ``$toolHiveVersion``",
    "- Studio path: ``$studioPath``",
    "- Studio version: ``$studioVersion``",
    "- ToolHive process: $toolHiveProcess",
    "- Read-only ``thv list`` probe: $toolHiveProbe",
    "",
    "A successful CLI probe does not prove a workload's Codex registration, real",
    "tool call, restart behavior, removal, or rollback. A failed probe does not",
    "invalidate a separately verified callable registration.",
    "",
    "## Automated probe limits",
    "",
    "- This script does not read encrypted Manager settings, environment values,",
    "  credentials, organization IDs, real tunnel IDs, private URLs, or provider",
    "  configuration.",
    "- It does not claim authenticated tunnel readiness or run a plugin tool call.",
    "- It does not start, stop, restart, install, register, remove, or migrate any",
    "  service or MCP.",
    "",
    "## Manual verification ledger",
    "",
    $manualBegin,
    $manualNotes,
    $manualEnd,
    ""
)

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$temporaryPath = Join-Path $outputDirectory `
    ([System.IO.Path]::GetRandomFileName() + ".tmp")
try {
    $content = ($lines -join [Environment]::NewLine)
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($temporaryPath, $content, $utf8WithoutBom)
    Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Output "Refreshed sanitized MCP runtime snapshot:"
Write-Output $OutputPath
