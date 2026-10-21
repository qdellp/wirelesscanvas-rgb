$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    if (!(Test-Path -LiteralPath 'upstream/Cargo.toml')) {
        git clone --branch v1.2.2 --depth 1 https://github.com/sgtaziz/lian-li-linux.git upstream
        if ($LASTEXITCODE -ne 0) { throw 'Could not download the lian-li-linux v1.2.2 sources.' }
    }
    if (Test-Path -LiteralPath upstream/.git) {
        $sourceCommit = git -C upstream rev-parse HEAD
        if ($sourceCommit -ne '73653a10c41318568df79e821f82a856cfcfadeb') { throw 'upstream/ must be the exact lian-li-linux v1.2.2 snapshot.' }
        git -C upstream submodule update --init --depth 1
        if ($LASTEXITCODE -ne 0) { throw 'Could not download tinyuz/HDiffPatch.' }
    }
    elseif (!(Test-Path -LiteralPath upstream/vendor/tinyuz/compress/tuz_enc.cpp) -or !(Test-Path -LiteralPath upstream/vendor/HDiffPatch/LICENSE)) { throw 'The source archive is missing tinyuz/HDiffPatch.' }
    if (!(Test-Path -LiteralPath '.tools/zig-x86_64-windows-0.15.2/zig.exe')) {
        New-Item -ItemType Directory -Force .tools | Out-Null
        Invoke-WebRequest -Uri 'https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip' -OutFile '.tools/zig.zip'
        $downloadIndex = Invoke-RestMethod -Uri 'https://ziglang.org/download/index.json'
        $expected = $downloadIndex.'0.15.2'.'x86_64-windows'.shasum
        if (!$expected -or (Get-FileHash -LiteralPath '.tools/zig.zip' -Algorithm SHA256).Hash -ne $expected) { throw 'Zig checksum mismatch.' }
        Expand-Archive -LiteralPath '.tools/zig.zip' -DestinationPath '.tools' -Force
    }
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Building requires the .NET 10 SDK. The released app does not need it.' }
    Write-Output 'Sources and compiler are ready. Run scripts/build.ps1.'
}
finally { Pop-Location }
