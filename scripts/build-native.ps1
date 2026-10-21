$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $zigPath = Join-Path $projectRoot '.tools\zig-x86_64-windows-0.15.2\zig.exe'
    if (!(Test-Path -LiteralPath $zigPath)) { throw 'Put Zig 0.15.2 into .tools/zig-x86_64-windows-0.15.2 (scripts/bootstrap.ps1 does this).' }
    New-Item -ItemType Directory -Force 'windows/native', '.tools/zig-cache', '.tools/zig-global' | Out-Null
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $projectRoot '.tools/zig-cache'
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $projectRoot '.tools/zig-global'
    New-Item -ItemType Directory -Force '.tools/temp' | Out-Null
    $env:TEMP = Join-Path $projectRoot '.tools/temp'
    $env:TMP = $env:TEMP
    & $zigPath cc -target x86_64-windows-gnu -O2 -D_IS_USED_MULTITHREAD=0 -shared '-Wl,--export-all-symbols' -Iupstream/vendor/tinyuz upstream/vendor/tinyuz/decompress/tuz_dec.c -o .tools/tuz-dec.dll
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the decompressor.' }
    & $zigPath c++ -target x86_64-windows-gnu -O2 -DNDEBUG -D_IS_USED_MULTITHREAD=0 -shared '-Wl,--export-all-symbols' -Iupstream/vendor -Iupstream/vendor/tinyuz -Iupstream/vendor/HDiffPatch upstream/vendor/tuz_wrapper.cpp upstream/vendor/tinyuz/compress/tuz_enc.cpp upstream/vendor/tinyuz/compress/tuz_enc_private/tuz_enc_clip.cpp upstream/vendor/tinyuz/compress/tuz_enc_private/tuz_enc_code.cpp upstream/vendor/tinyuz/compress/tuz_enc_private/tuz_enc_match.cpp upstream/vendor/tinyuz/compress/tuz_enc_private/tuz_sstring.cpp upstream/vendor/HDiffPatch/libHDiffPatch/HDiff/private_diff/libdivsufsort/divsufsort.cpp -o windows/native/tinyuz.dll
    if ($LASTEXITCODE -ne 0) { throw 'Could not build tinyuz.' }
    Copy-Item -LiteralPath .tools/tuz-dec.dll -Destination windows/native/tuz-dec.dll -Force
}
finally { Pop-Location }
