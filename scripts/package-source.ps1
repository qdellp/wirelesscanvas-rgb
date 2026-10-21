$ErrorActionPreference = 'Stop'
# Source archive for a release: this project plus the exact upstream snapshot needed to build it.
$projectRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $projectRoot '.tools/source-package'
if (Test-Path -LiteralPath $sourceRoot) { Remove-Item -LiteralPath $sourceRoot -Recurse -Force }
New-Item -ItemType Directory -Force $sourceRoot, "$sourceRoot/windows/native", "$sourceRoot/upstream/vendor/tinyuz", "$sourceRoot/upstream/vendor/HDiffPatch" | Out-Null
Get-ChildItem -LiteralPath "$projectRoot/windows" -File | Where-Object { $_.Extension -in '.cs', '.csproj', '.json' } | Copy-Item -Destination "$sourceRoot/windows" -Force
Copy-Item -Path "$projectRoot/windows/native/*.dll" -Destination "$sourceRoot/windows/native" -Force
Copy-Item -LiteralPath "$projectRoot/scripts", "$projectRoot/LICENSES" -Destination $sourceRoot -Recurse -Force
Copy-Item -LiteralPath "$projectRoot/README.md", "$projectRoot/README.ru.md", "$projectRoot/TECHNICAL.md", "$projectRoot/CHANGELOG.md", "$projectRoot/CONTRIBUTING.md", "$projectRoot/SECURITY.md", "$projectRoot/THIRD-PARTY-NOTICES.md", "$projectRoot/LICENSE", "$projectRoot/.gitignore" -Destination $sourceRoot -Force
foreach ($source in @('upstream', 'upstream/vendor/tinyuz', 'upstream/vendor/HDiffPatch')) {
    $archivePath = Join-Path $projectRoot '.tools/source-part.tar'
    git -C (Join-Path $projectRoot $source) archive --format=tar "--output=$archivePath" HEAD
    if ($LASTEXITCODE -ne 0) { throw "Could not archive $source." }
    tar -xf $archivePath -C (Join-Path $sourceRoot $source)
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $source." }
}
# Upstream LCD templates are not used on Windows, and some of their artwork is licensed CC BY-NC.
Remove-Item -LiteralPath "$sourceRoot/upstream/templates" -Recurse -Force -ErrorAction SilentlyContinue
$projectXml = [xml](Get-Content -LiteralPath "$projectRoot/windows/WirelessCanvasRGB.csproj" -Raw)
$releaseVersion = [string]$projectXml.Project.PropertyGroup.Version
$archivePath = "$projectRoot/dist/WirelessCanvasRGB-$releaseVersion-source.zip"
New-Item -ItemType Directory -Force "$projectRoot/dist" | Out-Null
Compress-Archive -Path "$sourceRoot/*" -DestinationPath $archivePath -Force
Write-Output "Source archive ready: $archivePath"
