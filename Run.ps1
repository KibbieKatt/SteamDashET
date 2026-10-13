$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build.ps1')
# This is the user-facing control window requested by the user.
Start-Process -FilePath (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\SteamDashET.exe') -WorkingDirectory $PSScriptRoot
