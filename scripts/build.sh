#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${1:-}" != --frontend-built ]; then npm --prefix frontend ci; npm --prefix frontend run build; fi
command -v fnpack >/dev/null
command -v dotnet >/dev/null
mkdir -p artifacts
stage="$(pwd)/artifacts/fpk"
# Only delete our fixed build staging directory inside the repository.
rm -rf -- "$stage"
mkdir -p "$stage"
cp -R packaging/. "$stage/"
cp LICENSE "$stage/LICENSE"
dotnet publish backend/Studio.csproj -c Release -r linux-x64 --self-contained true -o "$stage/app/server" -p:DebugType=None -p:DebugSymbols=false
dotnet publish agent/MiMoStudio.Agent.csproj -c Release -r linux-x64 --self-contained true -o "$stage/app/agent" -p:DebugType=None -p:DebugSymbols=false
python3 scripts/notices.py "$stage"
mkdir -p "$stage/app/server/wwwroot"
cp -R frontend/dist/. "$stage/app/server/wwwroot/"
python3 scripts/icons.py "$stage"
chmod 755 "$stage"/cmd/* "$stage/app/server/MiMoStudio" "$stage/app/agent/MiMoStudio.Agent"
find "$stage" -name '*.pdb' -delete
cd artifacts
fnpack build -d "$stage"
sha256sum ./*.fpk
