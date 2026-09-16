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
    1. Publishes the MCP server
    2. Builds the Revit add-in for each Revit version and writes the .addin manifest
    3. Copies the pyRevit bridge, sets the pyRevit extension path, and turns on Routes
    4. Copies the skills to ~/.claude/skills and ~/.codex/skills
    5. Registers the MCP server in Claude Code (-RegisterClaude) and Codex CLI (-RegisterCodex)
    6. Checks that every file is installed

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
$SettingsFile = Join-Path $DataDir 'Settings\settings.json'
$McpName = 'firstoption-revit'

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Note([string]$Text) { Write-Host "  $Text" -ForegroundColor DarkGray }

function Invoke-Tool([string]$Exe, [string[]]$Arguments, [switch]$AllowFail) {
    Note "$Exe $($Arguments -join ' ')"
    if ($DryRun) { return }
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0 -and -not $AllowFail) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

# Copy-Item -Recurse with -Exclude skips files inside sub-folders in Windows PowerShell 5.1, which left installs
# with missing files. Copy every file one by one instead, and leave out only the names in $Exclude.
function Copy-Tree([string]$From, [string]$To, [string[]]$Exclude) {
    $from = (Resolve-Path $From).Path.TrimEnd('\')
    $files = Get-ChildItem $from -Recurse -File | Where-Object {
        $name = $_.Name
        -not ($Exclude | Where-Object { $name -like $_ }) -and $_.FullName -notlike '*\__pycache__\*'
    }
    foreach ($f in $files) {
        $target = Join-Path $To $f.FullName.Substring($from.Length + 1)
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $f.FullName $target -Force
    }
    Note "copied $(@($files).Count) files -> $To"
}

# Set-Content -Encoding UTF8 writes a byte order mark in Windows PowerShell 5.1. Revit and pyRevit read these files better without one.
function Write-TextFile([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function Remove-Folder([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    Note "delete $Path"
    if (-not $DryRun) { Remove-Item $Path -Recurse -Force }
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

function Find-RevitVersions {
    # Revit is not always in %ProgramFiles%, and a new Revit version can come out after this script was written.
    # Look in the usual folders, in the registry, and in every "Revit <year>" folder of every drive folder Autodesk uses.
    $found = @{}
    function Keep([string]$Version, [string]$Exe) {
        if ($Version -and (Test-Path $Exe) -and -not $found.ContainsKey($Version)) { $found[$Version] = $Exe }
    }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, 'C:\Program Files', 'D:\Program Files') | Where-Object { $_ } | Select-Object -Unique) {
        foreach ($dir in Get-ChildItem (Join-Path $base 'Autodesk') -Directory -Filter 'Revit *' -ErrorAction SilentlyContinue) {
            if ($dir.Name -match '^Revit (\d{4})$') { Keep $Matches[1] (Join-Path $dir.FullName 'Revit.exe') }
        }
    }
    foreach ($key in @('HKLM:\SOFTWARE\Autodesk\Revit', 'HKLM:\SOFTWARE\WOW6432Node\Autodesk\Revit')) {
        foreach ($product in Get-ChildItem $key -Recurse -Depth 1 -ErrorAction SilentlyContinue) {
            $dir = (Get-ItemProperty $product.PSPath -ErrorAction SilentlyContinue).InstallationLocation
            if (-not $dir) { continue }
            $exe = Join-Path $dir 'Revit.exe'
            if (Test-Path $exe) {
                $version = if ("$($product.PSPath)$dir" -match '(\d{4})') { $Matches[1] } else { $null }
                Keep $version $exe
            }
        }
    }
    return $found
}

# The add-in is built for every Revit version the project supports, whether or not that Revit is on this computer.
# Revit reads only the manifest of the versions it has, so the extra files do nothing.
$AllVersions = 2020..2026 | ForEach-Object { "$_" }

$installed = Find-RevitVersions
foreach ($v in ($installed.Keys | Sort-Object)) { Note "Revit ${v} is on this computer: $($installed[$v])" }
if (-not $RevitVersions) { $RevitVersions = $AllVersions }
$extra = $installed.Keys | Where-Object { $_ -notin $RevitVersions }
if ($extra) { Write-Warning "Revit $($extra -join ', ') is installed, but the add-in has no build for it." }
Write-Host "Revit versions to build: $($RevitVersions -join ', ')"
Write-Host "Install folder: $DataDir"

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
        # One version that does not build must not stop the other versions; the check at the end lists what is missing.
        try {
            Invoke-Tool 'dotnet' @('build', (Join-Path $Root 'src\Addin\FirstOption.RevitMcp.Addin.csproj'), '-c', 'Release', "-p:RevitVersion=$v")
            $out = Join-Path $Root "src\Addin\bin\Release\R$v"
            $dest = Join-Path $AddinRoot $v
            $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
            $dll = Join-Path $dest 'FirstOption.RevitMcp.Addin.dll'
            Note "copy $out -> $dest"
            Note "write $addinsDir\FirstOption.RevitMcp.addin (Assembly: $dll)"
            if (-not $DryRun) {
                if (-not (Test-Path (Join-Path $out 'FirstOption.RevitMcp.Addin.dll'))) { throw "The build made no file in $out" }
                Remove-Folder $dest
                New-Item -ItemType Directory -Force $dest, $addinsDir | Out-Null
                Copy-Tree $out $dest @('*.addin', '*.pdb')
                $text = $manifest -replace '<Assembly>[^<]*</Assembly>', ('<Assembly>' + [Security.SecurityElement]::Escape($dll) + '</Assembly>')
                Write-TextFile (Join-Path $addinsDir 'FirstOption.RevitMcp.addin') $text
            }
        }
        catch {
            Write-Warning "Revit ${v}: the add-in was not installed. $($_.Exception.Message)"
        }
    }
}

# 3. pyRevit
if (-not $SkipPyRevit) {
    Step "pyRevit bridge -> $BridgeDir"
    Remove-Folder $BridgeDir
    if (-not $DryRun) {
        New-Item -ItemType Directory -Force $BridgeDir | Out-Null
        Copy-Tree (Join-Path $Root 'pyrevit') $BridgeDir @('*.pyc')
        Get-ChildItem $BridgeDir -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force
    }
    $library = Get-LibraryPath
    $panel = Join-Path $library 'FirstOptionLibrary.extension\FO Library.tab\Commands.panel'
    Note "library: $library"
    if (-not $DryRun) { New-Item -ItemType Directory -Force $panel | Out-Null }

    Step 'pyRevit extension paths and Routes'
    if (Get-Command 'pyrevit' -ErrorAction SilentlyContinue) {
        # pyRevit loads only the bridge. The command library is not a pyRevit extension path, so Revit shows no
        # "FO Library" tab; the agents run saved commands through the MCP (library_run).
        Invoke-Tool 'pyrevit' @('extensions', 'paths', 'add', $BridgeDir)
        Invoke-Tool 'pyrevit' @('configs', 'routes', 'enable')
    }
    else {
        Write-Warning 'pyrevit CLI not found. Install pyRevit (docs\REQUIRED-APPS.md), then run:'
        Write-Host "  pyrevit extensions paths add `"$BridgeDir`""
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
if ($RegisterClaude) {
    Step 'Register in Claude Code (user scope)'
    # remove first, so that running the script again does not fail on an existing registration
    Invoke-Tool 'claude' @('mcp', 'remove', '--scope', 'user', $McpName) -AllowFail
    Invoke-Tool 'claude' @('mcp', 'add', '--scope', 'user', $McpName, '--', $ServerExe)
}
if ($RegisterCodex) {
    Step 'Register in Codex CLI'
    Invoke-Tool 'codex' @('mcp', 'add', $McpName, '--', $ServerExe)
}

# 6. Check the install
if (-not $DryRun) {
    Step 'Check the install'
    $problems = @()
    function Check([string]$Text, [bool]$Ok, [string]$Fix) {
        Write-Host ("  [{0}] {1}" -f $(if ($Ok) { 'ok  ' } else { 'FAIL' }), $Text) -ForegroundColor $(if ($Ok) { 'DarkGray' } else { 'Red' })
        if (-not $Ok) { $script:problems += "$Text -> $Fix" }
    }

    if (-not $SkipServer) {
        Check "MCP server: $ServerExe" (Test-Path $ServerExe) 'Run the script again and read the dotnet publish output.'
    }
    if (-not $SkipAddin) {
        foreach ($v in $RevitVersions) {
            $dll = Join-Path $AddinRoot "$v\FirstOption.RevitMcp.Addin.dll"
            $addin = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v\FirstOption.RevitMcp.addin"
            if (-not (Test-Path $dll) -and -not $installed.ContainsKey($v)) {
                # Revit $v is not on this computer, so a missing build stops nobody here.
                Write-Host "  [skip] Revit ${v}: not installed here and not built" -ForegroundColor DarkGray
                continue
            }
            Check "Revit ${v} add-in: $dll" (Test-Path $dll) 'The build failed, or a file was locked because Revit was open.'
            $assembly = $null
            if (Test-Path $addin) {
                try { $assembly = ([xml](Get-Content $addin -Raw)).RevitAddIns.AddIn.Assembly } catch { $assembly = $null }
            }
            Check "Revit ${v} manifest points to the add-in" ($assembly -and (Test-Path $assembly)) "Delete $addin and run the script again."
            # Roslyn runs the C# of the agent; without it the add-in loads but revit_execute_csharp fails.
            Check "Revit ${v} C# runner files" (Test-Path (Join-Path $AddinRoot "$v\Microsoft.CodeAnalysis.CSharp.dll")) 'Run the script again with Revit closed.'
        }
    }
    if (-not $SkipPyRevit) {
        Check "pyRevit bridge: $BridgeDir" (Test-Path (Join-Path $BridgeDir 'FirstOptionMCP.extension\startup.py')) 'Run the script again.'
        $library = Get-LibraryPath
        Check "Command library: $library" (Test-Path (Join-Path $library 'FirstOptionLibrary.extension\FO Library.tab\Commands.panel')) 'Run the script again.'
        if (Get-Command 'pyrevit' -ErrorAction SilentlyContinue) {
            $paths = pyrevit extensions paths
            Check 'pyRevit loads the bridge' ($paths -contains $BridgeDir) "Run: pyrevit extensions paths add `"$BridgeDir`""
            Check 'pyRevit does not load the command library (no FO Library tab)' (-not ($paths -contains $library)) "Run: pyrevit extensions paths forget `"$library`""
        }
    }
    if (-not $SkipSkills) {
        foreach ($t in @((Join-Path $HOME '.claude\skills'), (Join-Path $HOME '.codex\skills'))) {
            if (Test-Path $t) { Check "Skills: $t" (Test-Path (Join-Path $t 'revit-mcp\SKILL.md')) 'Run the script again.' }
        }
    }

    if ($problems) {
        Write-Host ''
        Write-Warning "The install is not complete ($($problems.Count) problem(s)):"
        $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        throw 'Install incomplete. Fix the problems above and run the script again.'
    }
}

Step 'Done'
Write-Host "  Check the setup:  & `"$ServerExe`" doctor"
Write-Host '  Start Revit, then open First Option > AI Bridge > MCP Panel.'
