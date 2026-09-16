<#
.SYNOPSIS
    Add or remove the firstoption-revit MCP server in ~\.codex\config.toml.

.DESCRIPTION
    The setup cannot run codex.exe: the Codex app installs it as a Windows app link, and setup programs may not
    follow that link ("untrusted mount point"). This script writes the same section that "codex mcp add" writes,
    plus the timeouts that Revit work needs. It keeps every other line of the file.
#>
param(
    [Parameter(Mandatory)][ValidateSet('add', 'remove')][string]$Action,
    [string]$Server
)

$ErrorActionPreference = 'Stop'
$name = 'firstoption-revit'
$file = Join-Path $env:USERPROFILE '.codex\config.toml'
$lines = @()
if (Test-Path $file) { $lines = [IO.File]::ReadAllLines($file) }

# Remove the old section: from its header to the next [header].
$kept = New-Object System.Collections.Generic.List[string]
$inSection = $false
foreach ($line in $lines) {
    $t = $line.Trim()
    if ($t.StartsWith('[')) { $inSection = $t -eq "[mcp_servers.$name]" -or $t.StartsWith("[mcp_servers.$name.") }
    if (-not $inSection) { $kept.Add($line) }
}
while ($kept.Count -gt 0 -and $kept[$kept.Count - 1].Trim() -eq '') { $kept.RemoveAt($kept.Count - 1) }

if ($Action -eq 'add') {
    if ($kept.Count -gt 0) { $kept.Add('') }
    $kept.Add("[mcp_servers.$name]")
    # A TOML literal string ('...') keeps the backslashes of the Windows path.
    $kept.Add("command = '$Server'")
    $kept.Add('startup_timeout_sec = 20')
    $kept.Add('tool_timeout_sec = 300')
}
elseif (-not (Test-Path $file)) { exit 0 }

New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
[IO.File]::WriteAllText($file, (($kept -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "$Action $name in $file"
