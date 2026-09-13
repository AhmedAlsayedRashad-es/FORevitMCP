# Generates the UI mockups (HTML) with Codex CLI into this folder.
# Usage: powershell -ExecutionPolicy Bypass -File mockups\generate-mockups.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
Get-Content (Join-Path $here 'mockup-prompt.md') -Raw |
    codex exec -s workspace-write --skip-git-repo-check -C $root -
if ($LASTEXITCODE -ne 0) { throw "codex exec failed with exit code $LASTEXITCODE" }
Write-Host "Open $(Join-Path $here 'index.html')"
