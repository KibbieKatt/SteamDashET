$ErrorActionPreference = 'Stop'
& dotnet build (Join-Path $PSScriptRoot 'SteamDashET.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'SteamDashET build failed.' }
& dotnet (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\SteamDashET.dll') --self-test
if ($LASTEXITCODE -ne 0) { throw 'SteamDashET checks failed.' }
