#!/usr/bin/env bash
set -euo pipefail
export DOTNET_CLI_HOME=/workspace/.dotnet-home
export NUGET_PACKAGES=/workspace/.nuget/packages
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH=/workspace/dotnet:$PATH
cd /workspace/TempMail
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^10.0.401 '; then
  mkdir -p /workspace/dotnet
  curl -fsSL https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json -o /tmp/tempmail-dotnet-releases.json
  python3 - <<'PYSDK'
import json, urllib.request, hashlib, pathlib
meta=json.load(open('/tmp/tempmail-dotnet-releases.json'))
f=next(f for r in meta['releases'] for sdk in r.get('sdks',[]) if sdk['version']=='10.0.401' for f in sdk['files'] if f['rid']=='linux-x64' and f['name'].endswith('.tar.gz'))
p=pathlib.Path('/tmp/tempmail-dotnet-sdk.tar.gz')
with urllib.request.urlopen(f['url']) as src, p.open('wb') as dst:
    while block:=src.read(1024*1024): dst.write(block)
if hashlib.sha512(p.read_bytes()).hexdigest().lower()!=f['hash'].lower():
    p.unlink(); raise SystemExit('SDK checksum mismatch')
PYSDK
  tar -xzf /tmp/tempmail-dotnet-sdk.tar.gz -C /workspace/dotnet
fi
dotnet tool restore
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
