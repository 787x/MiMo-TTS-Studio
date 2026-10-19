$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
npm --prefix frontend ci
if ($LASTEXITCODE) { throw 'npm ci failed' }
npm --prefix frontend run build
if ($LASTEXITCODE) { throw 'frontend build failed' }
wsl -d Ubuntu -- bash scripts/build.sh --frontend-built
if ($LASTEXITCODE) { throw 'Linux publish or fnpack failed' }
