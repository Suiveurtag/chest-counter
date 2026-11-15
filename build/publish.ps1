# Builds the release folders and zips in dist/:
#   dist/ChestCounterSquad-win-x64/          the desktop app (self-contained, single exe + wwwroot)
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

Write-Host '== desktop app'
$app = Join-Path $dist 'ChestCounterSquad-win-x64'
& $dotnet publish (Join-Path $root 'src\ChestCounter.App\ChestCounter.App.csproj') @common -o $app
if ($LASTEXITCODE) { throw 'app publish failed' }
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
