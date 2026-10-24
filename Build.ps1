[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectPath '.build-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectPath
try {
    dotnet restore --configfile NuGet.Config --ignore-failed-sources --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet publish -c Release --self-contained false -o dist --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
} finally {
    Pop-Location
}
