<#
.SYNOPSIS
    Build the setup file FirstOption-RevitMCP-Setup-<version>.exe with Inno Setup.

.DESCRIPTION
    1. Publishes the MCP server self-contained, so the user needs no .NET runtime.
    2. Builds the Revit add-in for Revit 2020-2026.
    3. Copies the pyRevit bridge, the skills and the add-in manifest to installer\stage.
    4. Compiles installer\FirstOptionRevitMcp.iss to installer\Output.

    Needs the .NET 8 SDK and Inno Setup 6 (winget install --id JRSoftware.InnoSetup -e).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string[]]$RevitVersions = @(2020..2026 | ForEach-Object { "$_" })
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Stage = Join-Path $Root 'installer\stage'
$Version = ([xml](Get-Content (Join-Path $Root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

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
}

$iscc = @(
    (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | ForEach-Object Source),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it: winget install --id JRSoftware.InnoSetup -e' }

if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force $Stage | Out-Null

Step 'MCP server (self-contained)'
Invoke-Tool 'dotnet' @('publish', (Join-Path $Root 'src\Server\FirstOption.RevitMcp.Server.csproj'), '-c', 'Release',
    '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=none', '-o', (Join-Path $Stage 'Server'))

foreach ($v in $RevitVersions) {
    Step "Revit $v add-in"
    Invoke-Tool 'dotnet' @('build', (Join-Path $Root 'src\Addin\FirstOption.RevitMcp.Addin.csproj'), '-c', 'Release', "-p:RevitVersion=$v")
    $out = Join-Path $Root "src\Addin\bin\Release\R$v"
    if (-not (Test-Path (Join-Path $out 'FirstOption.RevitMcp.Addin.dll'))) { throw "The build made no file in $out" }
    Copy-Tree $out (Join-Path $Stage "Revit Add-in\$v") @('*.addin', '*.pdb')
}

Step 'pyRevit bridge, skills, manifest'
Copy-Tree (Join-Path $Root 'pyrevit') (Join-Path $Stage 'pyRevit Bridge') @('*.pyc')
Copy-Tree (Join-Path $Root 'skills') (Join-Path $Stage 'skills') @()
Copy-Item (Join-Path $Root 'src\Addin\FirstOption.RevitMcp.addin') (Join-Path $Stage 'FirstOption.RevitMcp.addin')

Step "Compile the setup file (version $Version)"
Invoke-Tool $iscc @("/DAppVersion=$Version", "/DRevitVersions=$($RevitVersions -join ',')", (Join-Path $Root 'installer\FirstOptionRevitMcp.iss'))

$setup = Join-Path $Root "installer\Output\FirstOption-RevitMCP-Setup-$Version.exe"
Write-Host "`nSetup file: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
