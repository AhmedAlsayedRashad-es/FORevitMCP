<#
.SYNOPSIS
    Build and install FirstOption Revit MCP on this computer.

.DESCRIPTION
    1. Publishes the MCP server to %LOCALAPPDATA%\FirstOption\RevitMCP\server
    2. Builds the Revit add-in for each Revit version and copies it to %APPDATA%\Autodesk\Revit\Addins\<version>
    3. Adds the bridge extension and the command library to the pyRevit extension paths, and turns on Routes
    4. Copies the skills to ~/.claude/skills and ~/.codex/skills
    5. Optional: registers the MCP server in Claude Code (-RegisterClaude) and Codex CLI (-RegisterCodex)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RegisterClaude -RegisterCodex

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RevitVersions 2025,2026 -DryRun
#>
[CmdletBinding()]
param(
    [string[]]$RevitVersions,
    [switch]$SkipServer,
    [switch]$SkipAddin,
    [switch]$SkipPyRevit,
    [switch]$SkipSkills,
    [switch]$RegisterClaude,
    [switch]$RegisterCodex,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$DataDir = Join-Path $env:LOCALAPPDATA 'FirstOption\RevitMCP'
$ServerDir = Join-Path $DataDir 'server'
$ServerExe = Join-Path $ServerDir 'FirstOption.RevitMcp.exe'
$McpName = 'firstoption-revit'

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    Write-Host "  $Exe $($Arguments -join ' ')" -ForegroundColor DarkGray
    if ($DryRun) { return }
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

function Get-LibraryPath {
    $settingsFile = Join-Path $DataDir 'settings.json'
    if (Test-Path $settingsFile) {
        $s = Get-Content $settingsFile -Raw | ConvertFrom-Json
        if ($s.libraryPath) { return [Environment]::ExpandEnvironmentVariables($s.libraryPath) }
    }
    return Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'FirstOption\RevitCommandLibrary'
}

if (-not $RevitVersions) {
    $RevitVersions = 2021..2026 | Where-Object { Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $_\Revit.exe") } | ForEach-Object { "$_" }
}
Write-Host "Revit versions: $($RevitVersions -join ', ')"

# 1. MCP server
if (-not $SkipServer) {
    Step "MCP server -> $ServerDir"
    if (Get-Process 'FirstOption.RevitMcp' -ErrorAction SilentlyContinue) {
        Write-Warning 'The MCP server is running (a Claude Code or Codex session uses it). Close those sessions if the copy fails.'
    }
    Invoke-Tool 'dotnet' @('publish', (Join-Path $Root 'src\Server\FirstOption.RevitMcp.Server.csproj'), '-c', 'Release', '-o', $ServerDir)
}

# 2. Revit add-in
if (-not $SkipAddin) {
    if (Get-Process 'Revit' -ErrorAction SilentlyContinue) {
        Write-Warning 'Revit is running. Close Revit, or the add-in files can be locked.'
    }
    foreach ($v in $RevitVersions) {
        Step "Revit $v add-in"
        Invoke-Tool 'dotnet' @('build', (Join-Path $Root 'src\Addin\FirstOption.RevitMcp.Addin.csproj'), '-c', 'Release', "-p:RevitVersion=$v")
        $out = Join-Path $Root "src\Addin\bin\Release\R$v"
        $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
        $dest = Join-Path $addinsDir 'FirstOption.RevitMcp'
        Write-Host "  copy $out -> $dest" -ForegroundColor DarkGray
        if (-not $DryRun) {
            New-Item -ItemType Directory -Force $dest | Out-Null
            Copy-Item (Join-Path $out '*') $dest -Recurse -Force -Exclude '*.addin', '*.pdb'
            Copy-Item (Join-Path $Root 'src\Addin\FirstOption.RevitMcp.addin') (Join-Path $addinsDir 'FirstOption.RevitMcp.addin') -Force
        }
    }
}

# 3. pyRevit
if (-not $SkipPyRevit) {
    Step 'pyRevit extensions and Routes'
    $library = Get-LibraryPath
    $panel = Join-Path $library 'FirstOptionLibrary.extension\FO Library.tab\Commands.panel'
    Write-Host "  library: $library" -ForegroundColor DarkGray
    if (-not $DryRun) { New-Item -ItemType Directory -Force $panel | Out-Null }
    if (Get-Command 'pyrevit' -ErrorAction SilentlyContinue) {
        Invoke-Tool 'pyrevit' @('extensions', 'paths', 'add', (Join-Path $Root 'pyrevit'))
        Invoke-Tool 'pyrevit' @('extensions', 'paths', 'add', $library)
        Invoke-Tool 'pyrevit' @('configs', 'routes', 'enable')
    }
    else {
        Write-Warning 'pyrevit CLI not found. Install pyRevit (docs\REQUIRED-APPS.md), then run:'
        Write-Host "  pyrevit extensions paths add `"$(Join-Path $Root 'pyrevit')`""
        Write-Host "  pyrevit extensions paths add `"$library`""
        Write-Host '  pyrevit configs routes enable'
    }
}

# 4. Skills
if (-not $SkipSkills) {
    foreach ($target in @((Join-Path $HOME '.claude\skills'), (Join-Path $HOME '.codex\skills'))) {
        Step "Skills -> $target"
        Get-ChildItem (Join-Path $Root 'skills') -Directory | ForEach-Object {
            $dest = Join-Path $target $_.Name
            Write-Host "  $($_.Name)" -ForegroundColor DarkGray
            if (-not $DryRun) {
                New-Item -ItemType Directory -Force $dest | Out-Null
                Copy-Item (Join-Path $_.FullName '*') $dest -Recurse -Force
            }
        }
    }
}

# 5. Register the MCP server
if ($RegisterClaude) {
    Step 'Register in Claude Code (user scope)'
    Invoke-Tool 'claude' @('mcp', 'add', '--scope', 'user', $McpName, '--', $ServerExe)
}
if ($RegisterCodex) {
    Step 'Register in Codex CLI'
    Invoke-Tool 'codex' @('mcp', 'add', $McpName, '--', $ServerExe)
}

Step 'Done'
Write-Host "  Check the setup:  & `"$ServerExe`" doctor"
Write-Host '  Start Revit, then open First Option > AI Bridge > MCP Panel.'
