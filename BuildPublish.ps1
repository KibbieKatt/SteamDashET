$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build.ps1')

$projectDirectory = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64\publish'))
if (-not $publishDirectory.StartsWith($projectDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish directory must be inside the project.'
}
# Clear generated output so a previous publish cannot leave loose runtime files behind.
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

& dotnet publish (Join-Path $PSScriptRoot 'SteamDashET.csproj') -c Release -r win-x64 --self-contained true --nologo `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded `
    -p:PublishTrimmed=false -p:PublishAot=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'SteamDashET publish failed.' }

# A Windows GUI executable must be waited on explicitly to check its exit code.
$publishedChecks = Start-Process -FilePath (Join-Path $publishDirectory 'SteamDashET.exe') `
    -ArgumentList '--self-test' -WorkingDirectory $publishDirectory -NoNewWindow -Wait -PassThru
if ($publishedChecks.ExitCode -ne 0) { throw 'Published SteamDashET checks failed.' }

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'SteamDashET.exe') {
    throw 'The publish output must contain only SteamDashET.exe.'
}

Write-Host "Publish complete: $publishDirectory"
