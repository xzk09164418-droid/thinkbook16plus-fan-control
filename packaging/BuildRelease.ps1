$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$version = (Get-Content (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid version' }
$name = 'thinkbook16plus-fan-control'
$dist = Join-Path (Get-Location) 'dist'
$packageName = "$name-$version-windows-x64"
$package = Join-Path $dist $packageName
if (Test-Path -LiteralPath $package) { throw 'Package directory already exists; use a clean checkout' }
New-Item -ItemType Directory -Path $package -Force | Out-Null
& ./BuildCurve.ps1 -OutputPath (Join-Path $package 'FanCurve.exe')
& ./RunRegressionTests.ps1
foreach ($file in @('README.md','LICENSE','ACKNOWLEDGEMENTS.md','PRIVACY.md','THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath $file -Destination $package
}
Copy-Item -LiteralPath packaging/QUICKSTART.md -Destination $package
$commit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit' }
"Version: $version`nCommit: $commit`nPlatform: Windows x64`nUnsigned build from project source." | Set-Content -LiteralPath (Join-Path $package 'BUILD-INFO.txt') -Encoding UTF8
$forbidden = Get-ChildItem $package -Recurse -File | Where-Object {
    $_.Extension -in @('.log','.bak','.pdb') -or $_.Name -in @('test-target.txt','test-adapter.txt','curve-settings.json','curve-recovery.json')
}
if ($forbidden) { throw 'Unexpected private or generated files in package' }
$zip = Join-Path $dist ($packageName + '.zip')
Compress-Archive -LiteralPath $package -DestinationPath $zip
$digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  $packageName.zip" | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Created $packageName.zip"
