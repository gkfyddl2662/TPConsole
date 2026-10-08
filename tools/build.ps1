# Builds the web UI and the app, then runs the tests.
# Quit TPConsole first (tray icon -> Quit): a running app locks its DLLs.
#   powershell -ExecutionPolicy Bypass -File tools\build.ps1            (Release)
#   powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Debug
param([switch]$Debug)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$config = if ($Debug) { 'Debug' } else { 'Release' }

if (Get-Process TPConsole.App -ErrorAction SilentlyContinue) {
    Write-Host 'TPConsole is running. Quit it from the tray icon, then run this again.' -ForegroundColor Yellow
    exit 1
}

Push-Location (Join-Path $root 'web')
try {
    if (-not (Test-Path node_modules)) { npm install; if ($LASTEXITCODE) { exit $LASTEXITCODE } }
    npm run build; if ($LASTEXITCODE) { exit $LASTEXITCODE }
} finally { Pop-Location }

dotnet build (Join-Path $root 'src\TPConsole.App') -c $config; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test (Join-Path $root 'tests\TPConsole.Tests') -c $config --nologo -v q; if ($LASTEXITCODE) { exit $LASTEXITCODE }

$exe = Join-Path $root "src\TPConsole.App\bin\$config\net10.0-windows\TPConsole.App.exe"
Write-Host "Built: $exe" -ForegroundColor Green
