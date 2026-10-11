# Script file encoding checker.
#
# BACKGROUND -- this is the third time the same class of problem has bitten:
#
#   cmd.exe                     reads .cmd / .bat using the ANSI code page
#   Windows PowerShell 5.1      reads .ps1 the same way when there is no BOM
#
# Consequences:
#   - non-ASCII inside .cmd breaks the commands themselves; you get a screenful
#     of "not recognized as an internal or external command" and no clue why
#   - non-ASCII inside a BOM-less .ps1 produces string-terminator parser errors
#
# RULES
#   .cmd / .bat  ->  must be pure ASCII
#   .ps1         ->  may contain non-ASCII, but then MUST have a UTF-8 BOM
#
# This checker is deliberately ASCII-ONLY itself. A checker that can be broken
# by the very problem it checks for is not a checker.
#
# It runs automatically at build time (see the CheckScriptEncodings target in
# Twice1200.App.csproj) and can also be run by hand.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\scripts-check.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\scripts-check.ps1 -Root <dir> -Quiet
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\scripts-check.ps1 -Fix
#
# Exit codes: 0 = all good, 1 = problems found

param(
    [string]$Root = '',
    [switch]$Quiet,
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
}

if (-not (Test-Path -LiteralPath $Root)) {
    Write-Host "[x] Root directory not found: $Root"
    exit 1
}

# Canonicalise Root.
#
# This matters more than it looks: MSBuild passes a path containing ".."
# (e.g. <App>\..\..). Its string length is longer than the real paths, so a
# naive $file.FullName.Substring($Root.Length) throws ArgumentOutOfRangeException
# -- which makes the guard fail even when every file is fine.
$Root = (Resolve-Path -LiteralPath $Root).Path
if ($Root.EndsWith('\')) {
    $Root = $Root.TrimEnd('\')
}

function Say { param([string]$Message) if (-not $Quiet) { Write-Host $Message } }

# Directories whose scripts are not ours to police.
# .probe-edge holds throwaway investigation scripts.
$skipPattern = '\\(bin|obj|\.idea|\.git|\.probe-edge|node_modules)\\'

function Get-ScriptFiles {
    param([string]$Directory, [string]$Filter)

    # @() matters: with exactly one match, Get-ChildItem returns a single object
    # rather than an array, and the later '+' concatenation then fails with
    # "does not contain a method named 'op_Addition'".
    return @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter $Filter -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -notmatch $skipPattern })
}

function Test-HasUtf8Bom {
    param([byte[]]$Bytes)
    return ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF)
}

# Render a path relative to $Root for readable output.
#
# Returns the full path unchanged when it cannot be relativised, instead of
# throwing. A diagnostics tool must never fail because of its own formatting.
function Get-RelativePath {
    param([string]$FullPath)

    if ($FullPath.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullPath.Substring($Root.Length).TrimStart('\', '/')
    }

    return $FullPath
}

function Add-Utf8Bom {
    param([string]$Path)

    $text = [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($true))
}

$problems = @()
$fixed = @()
$checked = 0

# ---------------------------------------------------------------- .cmd / .bat
Say '--- .cmd / .bat  (cmd.exe uses the ANSI code page; must be pure ASCII) ---'

$cmdFiles = @(Get-ScriptFiles -Directory $Root -Filter '*.cmd') +
            @(Get-ScriptFiles -Directory $Root -Filter '*.bat')

foreach ($file in $cmdFiles) {
    $checked++
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $nonAscii = @($bytes | Where-Object { $_ -gt 127 }).Count

    if ($nonAscii -gt 0) {
        $relative = Get-RelativePath -FullPath $file.FullName
        $problems += "$relative : contains $nonAscii non-ASCII byte(s); .cmd/.bat must be pure ASCII"
        Say "  [FAIL] $relative  ($nonAscii non-ASCII bytes)"
    } else {
        Say "  [ ok ] $($file.Name)"
    }
}

# ---------------------------------------------------------------- .ps1
Say ''
Say '--- .ps1  (ANSI when there is no BOM; non-ASCII needs a BOM) ---'

$ps1Files = Get-ScriptFiles -Directory $Root -Filter '*.ps1'

foreach ($file in $ps1Files) {
    $checked++
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $hasBom = Test-HasUtf8Bom -Bytes $bytes
    $nonAscii = @($bytes | Where-Object { $_ -gt 127 }).Count
    $relative = Get-RelativePath -FullPath $file.FullName

    if ($nonAscii -eq 0) {
        Say "  [ ok ] $relative  (pure ASCII)"
    } elseif ($hasBom) {
        Say "  [ ok ] $relative  (non-ASCII, BOM present)"
    } else {
        if ($Fix) {
            Add-Utf8Bom -Path $file.FullName
            $verified = Test-HasUtf8Bom -Bytes ([System.IO.File]::ReadAllBytes($file.FullName))
            if ($verified) {
                $fixed += $relative
                Say "  [FIXED] $relative  (added UTF-8 BOM)"
            } else {
                $problems += "$relative : tried to add a BOM but it did not stick"
                Say "  [FAIL] $relative  (BOM still missing after fix)"
            }
        } else {
            $problems += "$relative : contains $nonAscii non-ASCII byte(s) but has no UTF-8 BOM"
            Say "  [FAIL] $relative  ($nonAscii non-ASCII bytes, no BOM)"
        }
    }
}

# ---------------------------------------------------------------- summary
Say ''

if ($fixed.Count -gt 0) {
    Write-Host "[i] Added a UTF-8 BOM to $($fixed.Count) file(s)." -ForegroundColor Yellow
    Write-Host '    Re-run the build; the build stops after a fix so that the newly' -ForegroundColor Yellow
    Write-Host '    written files are re-read from a clean state.' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}

if ($problems.Count -eq 0) {
    Say "[ ok ] Script encoding check passed ($checked file(s) checked)."
    exit 0
}

Write-Host ''
Write-Host "[x] Script encoding check failed:" -ForegroundColor Red
foreach ($problem in $problems) {
    Write-Host "      $problem" -ForegroundColor Red
}

Write-Host ''
Write-Host 'How to fix:' -ForegroundColor Yellow
Write-Host '  .cmd / .bat : rewrite as pure ASCII (use English comments)' -ForegroundColor Yellow
Write-Host '  .ps1        : add a UTF-8 BOM, or run this script with -Fix' -ForegroundColor Yellow

exit 1
