param([switch]$SkipNative)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:MSBuildEnableWorkloadResolver = 'false'
    if (!$SkipNative) { & (Join-Path $PSScriptRoot 'build-native.ps1') }
    dotnet build windows/WirelessCanvasRGB.csproj -c Release -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet windows/bin/Release/net10.0-windows/WirelessCanvasRGB.dll --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Self-tests failed. No release was created.' }
    $output = 'dist/WirelessCanvasRGB'
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    dotnet publish windows/WirelessCanvasRGB.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:UseSharedCompilation=false -p:DebugType=None -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    Copy-Item -LiteralPath README.md, README.ru.md, LICENSE, THIRD-PARTY-NOTICES.md, CHANGELOG.md -Destination $output -Force
    Copy-Item -LiteralPath LICENSES -Destination $output -Recurse -Force
    Copy-Item -LiteralPath artifacts/self-test-results.txt -Destination "$output/self-test-results.txt" -Force
    Copy-Item -LiteralPath scripts/diagnose.cmd, scripts/start.cmd -Destination $output -Force
    $projectXml = [xml](Get-Content -LiteralPath windows/WirelessCanvasRGB.csproj -Raw)
    $releaseVersion = [string]$projectXml.Project.PropertyGroup.Version
    $archivePath = "dist/WirelessCanvasRGB-$releaseVersion-win-x64.zip"
    Compress-Archive -Path "$output/*" -DestinationPath $archivePath -Force
    Get-FileHash -LiteralPath $archivePath -Algorithm SHA256 | Format-List
}
finally { Pop-Location }
