<#
.SYNOPSIS
    Build and install FirstOption Revit MCP on this computer.

.DESCRIPTION
    All files go to one folder, %LOCALAPPDATA%\First Option\RevitMCP:
      Server\             the MCP server (FirstOption.RevitMcp.exe)
      Revit Add-in\<ver>\ the Revit add-in for each Revit version
      pyRevit Bridge\     the pyRevit extension with the Routes API (startup.py)
      Command Library\    the saved commands (a pyRevit extension)
      Activity Log\       activity.jsonl (the Revit panel reads it)
      Settings\           settings.json (GitHub Settings window)
    Only two things live elsewhere, because Revit and the agents look only there:
      %APPDATA%\Autodesk\Revit\Addins\<ver>\FirstOption.RevitMcp.addin (points to Revit Add-in\<ver>)
      ~\.claude\skills and ~\.codex\skills

    Steps:
    0. Moves files from the old folders (%LOCALAPPDATA%\FirstOption\RevitMCP, Documents\FirstOption\RevitCommandLibrary,
       the add-in folders in %APPDATA%\Autodesk\Revit\Addins) and deletes the old folders
    1. Publishes the MCP server
    2. Builds the Revit add-in for each Revit version and writes the .addin manifest
    3. Copies the pyRevit bridge, sets the pyRevit extension paths, and turns on Routes
    4. Copies the skills to ~/.claude/skills and ~/.codex/skills
    5. Registers the MCP server in Claude Code (-RegisterClaude) and Codex CLI (-RegisterCodex);
       registrations that already exist are always changed to the new server path

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
$DataDir = Join-Path $env:LOCALAPPDATA 'First Option\RevitMCP'
$ServerDir = Join-Path $DataDir 'Server'
$ServerExe = Join-Path $ServerDir 'FirstOption.RevitMcp.exe'
$AddinRoot = Join-Path $DataDir 'Revit Add-in'
$BridgeDir = Join-Path $DataDir 'pyRevit Bridge'
$DefaultLibrary = Join-Path $DataDir 'Command Library'
$ActivityDir = Join-Path $DataDir 'Activity Log'
$SettingsDir = Join-Path $DataDir 'Settings'
$SettingsFile = Join-Path $SettingsDir 'settings.json'
$McpName = 'firstoption-revit'

# old locations (before version 0.2.0)
$OldDataDirs = @((Join-Path $env:LOCALAPPDATA 'FirstOption\RevitMCP'), $DataDir)
$OldLibrary = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'FirstOption\RevitCommandLibrary'
$OldBridge = Join-Path $Root 'pyrevit'
$OldServerExes = @((Join-Path $env:LOCALAPPDATA 'FirstOption\RevitMCP\server\FirstOption.RevitMcp.exe'),
                   (Join-Path $env:LOCALAPPDATA 'First Option\RevitMCP\server\FirstOption.RevitMcp.exe'))

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Note([string]$Text) { Write-Host "  $Text" -ForegroundColor DarkGray }

function Invoke-Tool([string]$Exe, [string[]]$Arguments, [switch]$AllowFail) {
    Note "$Exe $($Arguments -join ' ')"
    if ($DryRun) { return }
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0 -and -not $AllowFail) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

function Move-Folder([string]$From, [string]$To) {
    if (-not (Test-Path $From)) { return }
    if (Test-Path $To) { Write-Warning "Not moved, because the target exists: $From -> $To"; return }
    Note "move $From -> $To"
    if (-not $DryRun) {
        New-Item -ItemType Directory -Force (Split-Path $To) | Out-Null
        Move-Item $From $To
    }
}

function Move-File([string]$From, [string]$ToDir) {
    if (-not (Test-Path $From -PathType Leaf)) { return }
    $to = Join-Path $ToDir (Split-Path $From -Leaf)
    if (Test-Path $to) { Write-Warning "Not moved, because the target exists: $From -> $to"; return }
    Note "move $From -> $to"
    if (-not $DryRun) {
        New-Item -ItemType Directory -Force $ToDir | Out-Null
        Move-Item $From $to
    }
}

function Remove-Folder([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    Note "delete $Path"
    if (-not $DryRun) { Remove-Item $Path -Recurse -Force }
}

function Remove-IfEmpty([string]$Path) {
    if ((Test-Path $Path) -and -not (Get-ChildItem $Path -Force)) { Remove-Folder $Path }
}

function Get-Settings {
    if (Test-Path $SettingsFile) { return Get-Content $SettingsFile -Raw | ConvertFrom-Json }
    return $null
}

function Get-LibraryPath {
    $s = Get-Settings
    if ($s -and $s.libraryPath) { return [Environment]::ExpandEnvironmentVariables($s.libraryPath) }
    return $DefaultLibrary
}

if (-not $DryRun) {
    # Revit locks the add-in and the library; the MCP server locks its own files. Check only what this run replaces.
    $names = @()
    if (-not ($SkipAddin -and $SkipPyRevit)) { $names += 'Revit' }
    if (-not $SkipServer) { $names += 'FirstOption.RevitMcp' }
    $running = if ($names) { Get-Process $names -ErrorAction SilentlyContinue }
    if ($running) {
        throw "Close these first (Revit, or every Claude Code and Codex session for the MCP server; or use -SkipServer). Running: $(($running | ForEach-Object { "$($_.Name) ($($_.Id))" }) -join ', ')"
    }
}

if (-not $RevitVersions) {
    $RevitVersions = 2021..2026 | Where-Object { Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $_\Revit.exe") } | ForEach-Object { "$_" }
}
Write-Host "Revit versions: $($RevitVersions -join ', ')"
Write-Host "Install folder: $DataDir"

# 0. Move files from the old folders
Step 'Move files from the old folders'
foreach ($old in $OldDataDirs) {
    Move-File (Join-Path $old 'settings.json') $SettingsDir
    Move-File (Join-Path $old 'activity.jsonl') $ActivityDir
    Move-File (Join-Path $old 'activity.jsonl.old') $ActivityDir
    # the old server folder is 'server'; the new server is published again in step 1
    $oldServer = Get-ChildItem $old -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ceq 'server' }
    if ($oldServer) { Remove-Folder $oldServer.FullName }
}
Remove-IfEmpty (Join-Path $env:LOCALAPPDATA 'FirstOption\RevitMCP')
Remove-IfEmpty (Join-Path $env:LOCALAPPDATA 'FirstOption')

$settings = Get-Settings
if ($settings -and $settings.libraryPath -and
    [string]::Equals([Environment]::ExpandEnvironmentVariables($settings.libraryPath).TrimEnd('\'), $OldLibrary, 'OrdinalIgnoreCase')) {
    Note "settings.json: libraryPath was the old default; now the default is used"
    if (-not $DryRun) {
        $settings.libraryPath = ''
        $settings | ConvertTo-Json -Depth 5 | Set-Content $SettingsFile -Encoding UTF8
    }
}
if ((Get-LibraryPath) -eq $DefaultLibrary) { Move-Folder $OldLibrary $DefaultLibrary }
Remove-IfEmpty (Split-Path $OldLibrary)

# 1. MCP server
if (-not $SkipServer) {
    Step "MCP server -> $ServerDir"
    Remove-Folder $ServerDir
    Invoke-Tool 'dotnet' @('publish', (Join-Path $Root 'src\Server\FirstOption.RevitMcp.Server.csproj'), '-c', 'Release', '-o', $ServerDir)
}

# 2. Revit add-in
if (-not $SkipAddin) {
    $manifest = Get-Content (Join-Path $Root 'src\Addin\FirstOption.RevitMcp.addin') -Raw
    foreach ($v in $RevitVersions) {
        Step "Revit $v add-in -> $(Join-Path $AddinRoot $v)"
        Invoke-Tool 'dotnet' @('build', (Join-Path $Root 'src\Addin\FirstOption.RevitMcp.Addin.csproj'), '-c', 'Release', "-p:RevitVersion=$v")
        $out = Join-Path $Root "src\Addin\bin\Release\R$v"
        $dest = Join-Path $AddinRoot $v
        $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
        $dll = Join-Path $dest 'FirstOption.RevitMcp.Addin.dll'
        Note "copy $out -> $dest"
        Note "write $addinsDir\FirstOption.RevitMcp.addin (Assembly: $dll)"
        if (-not $DryRun) {
            Remove-Folder $dest
            New-Item -ItemType Directory -Force $dest, $addinsDir | Out-Null
            Copy-Item (Join-Path $out '*') $dest -Recurse -Force -Exclude '*.addin', '*.pdb'
            $text = $manifest -replace '<Assembly>[^<]*</Assembly>', "<Assembly>$([Security.SecurityElement]::Escape($dll))</Assembly>"
            Set-Content (Join-Path $addinsDir 'FirstOption.RevitMcp.addin') $text -Encoding UTF8
        }
        Remove-Folder (Join-Path $addinsDir 'FirstOption.RevitMcp')
    }
}

# 3. pyRevit
if (-not $SkipPyRevit) {
    Step "pyRevit bridge -> $BridgeDir"
    Remove-Folder $BridgeDir
    if (-not $DryRun) {
        New-Item -ItemType Directory -Force $BridgeDir | Out-Null
        Copy-Item (Join-Path $Root 'pyrevit\*') $BridgeDir -Recurse -Force -Exclude '__pycache__'
        Get-ChildItem $BridgeDir -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force
    }
    $library = Get-LibraryPath
    $panel = Join-Path $library 'FirstOptionLibrary.extension\FO Library.tab\Commands.panel'
    Note "library: $library"
    if (-not $DryRun) { New-Item -ItemType Directory -Force $panel | Out-Null }

    Step 'pyRevit extension paths and Routes'
    if (Get-Command 'pyrevit' -ErrorAction SilentlyContinue) {
        foreach ($old in @($OldBridge, $OldLibrary)) {
            if ((pyrevit extensions paths) -contains $old) { Invoke-Tool 'pyrevit' @('extensions', 'paths', 'forget', $old) -AllowFail }
        }
        Invoke-Tool 'pyrevit' @('extensions', 'paths', 'add', $BridgeDir)
        Invoke-Tool 'pyrevit' @('extensions', 'paths', 'add', $library)
        Invoke-Tool 'pyrevit' @('configs', 'routes', 'enable')
    }
    else {
        Write-Warning 'pyrevit CLI not found. Install pyRevit (docs\REQUIRED-APPS.md), then run:'
        Write-Host "  pyrevit extensions paths add `"$BridgeDir`""
        Write-Host "  pyrevit extensions paths add `"$library`""
        Write-Host '  pyrevit configs routes enable'
    }
}

# 4. Skills
if (-not $SkipSkills) {
    # Skills go to the agents you register; with neither switch, to both.
    $targets = @()
    if ($RegisterClaude -or -not $RegisterCodex) { $targets += (Join-Path $HOME '.claude\skills') }
    if ($RegisterCodex -or -not $RegisterClaude) { $targets += (Join-Path $HOME '.codex\skills') }
    foreach ($target in $targets) {
        Step "Skills -> $target"
        Get-ChildItem (Join-Path $Root 'skills') -Directory | ForEach-Object {
            $dest = Join-Path $target $_.Name
            Note $_.Name
            if (-not $DryRun) {
                New-Item -ItemType Directory -Force $dest | Out-Null
                Copy-Item (Join-Path $_.FullName '*') $dest -Recurse -Force
            }
        }
    }
}

# 5. Register the MCP server
$claudeJson = Join-Path $HOME '.claude.json'
$claudeOld = (Test-Path $claudeJson) -and ($OldServerExes | Where-Object { (Get-Content $claudeJson -Raw).Replace('\\', '\').Contains($_) })
if ($RegisterClaude -or $claudeOld) {
    Step 'Register in Claude Code (user scope)'
    Invoke-Tool 'claude' @('mcp', 'remove', '--scope', 'user', $McpName) -AllowFail
    Invoke-Tool 'claude' @('mcp', 'add', '--scope', 'user', $McpName, '--', $ServerExe)
}

$codexToml = Join-Path $HOME '.codex\config.toml'
$codexText = if (Test-Path $codexToml) { Get-Content $codexToml -Raw } else { '' }
$codexOld = $OldServerExes | Where-Object { $codexText.Contains($_) }
if ($codexOld) {
    Step 'Change the server path in Codex CLI'
    Note "$codexToml -> $ServerExe"
    if (-not $DryRun) {
        foreach ($old in $codexOld) { $codexText = $codexText.Replace($old, $ServerExe) }
        [IO.File]::WriteAllText($codexToml, $codexText)
    }
}
elseif ($RegisterCodex) {
    Step 'Register in Codex CLI'
    Invoke-Tool 'codex' @('mcp', 'add', $McpName, '--', $ServerExe)
}

Step 'Done'
Write-Host "  Check the setup:  & `"$ServerExe`" doctor"
Write-Host '  Start Revit, then open First Option > AI Bridge > MCP Panel.'
