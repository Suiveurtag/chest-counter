# Builds the release folders and zips in dist/:
#   dist/ChestCounterSquad-win-x64/          the desktop app: Tauri shell exe + engine\ (self-contained .NET exe + wwwroot)
#   dist/ChestCounterSquad-Server-win-x64/   the squad server for Windows (self-contained, single exe)
# Uses the portable toolchain in .tools/ when present, else dotnet/npm from PATH.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$node = Join-Path $root '.tools\node'
if (Test-Path $node) { $env:Path = "$node;$env:Path" }
$dist = Join-Path $root 'dist'

Write-Host '== web interface'
Push-Location (Join-Path $root 'web')
try {
    if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' } }
    npm run build; if ($LASTEXITCODE) { throw 'web build failed' }
} finally { Pop-Location }

Write-Host '== tests'
& $dotnet test (Join-Path $root 'tests\ChestCounter.Tests\ChestCounter.Tests.csproj') --nologo -v q
if ($LASTEXITCODE) { throw 'tests failed' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=none', '--nologo')

Write-Host '== engine (the desktop shell starts it from its engine\ folder)'
$engine = Join-Path $root 'desktop\engine'
if (Test-Path $engine) { Remove-Item $engine -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\ChestCounter.Host\ChestCounter.Host.csproj') @common -o $engine
if ($LASTEXITCODE) { throw 'engine publish failed' }

Write-Host '== desktop shell (Tauri)'
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path $cargo)) { $cargo = 'cargo' }
Push-Location (Join-Path $root 'desktop')
try {
    & $cargo build --release --features custom-protocol
    if ($LASTEXITCODE) { throw 'desktop build failed' }
} finally { Pop-Location }
$app = Join-Path $dist 'ChestCounterSquad-win-x64'
New-Item -ItemType Directory $app | Out-Null
Copy-Item (Join-Path $root 'desktop\target\release\ChestCounterSquad.exe') $app
Copy-Item $engine (Join-Path $app 'engine') -Recurse
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'NOTICE.md') $app

Write-Host '== squad server'
$srv = Join-Path $dist 'ChestCounterSquad-Server-win-x64'
& $dotnet publish (Join-Path $root 'src\ChestCounter.Server\ChestCounter.Server.csproj') @common -o $srv
if ($LASTEXITCODE) { throw 'server publish failed' }
Copy-Item (Join-Path $root '.env.example'), (Join-Path $root 'LICENSE'), (Join-Path $root 'NOTICE.md') $srv

Write-Host '== zips'
foreach ($dir in $app, $srv) {
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath "$dir.zip" -CompressionLevel Optimal
}
Get-ChildItem $dist -File | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
