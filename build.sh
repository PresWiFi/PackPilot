#!/usr/bin/env bash
# Cross-builds the Windows exe from Linux/WSL (WPF builds fine with EnableWindowsTargeting). Same output as build.ps1.
set -euo pipefail
cd "$(dirname "$0")"
dotnet run --project tests/PackPilot.Tests
dotnet publish src/PackPilot -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist
