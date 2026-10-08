# Builds the installer dist\TPConsole-Setup-<version>.exe: double-click to install; the self-contained x64 app
# is inside, no .NET needed on the target PC. Uninstall from Settings -> Apps -> Installed apps.
# CI (.github/workflows/release.yml) passes the release version; locally the csproj <Version> is used.
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-Version 1.2.3]
param([string]$Version)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\TPConsole.App\TPConsole.App.csproj'
$setup = Join-Path $root 'src\TPConsole.Setup\TPConsole.Setup.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$build = Join-Path $root 'dist\build'
$app = Join-Path $build 'app'

Push-Location (Join-Path $root 'web')
try {
    if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE) { exit $LASTEXITCODE } }
    npm run build; if ($LASTEXITCODE) { exit $LASTEXITCODE }
} finally { Pop-Location }

dotnet test (Join-Path $root 'tests\TPConsole.Tests') -c Release --nologo -v q; if ($LASTEXITCODE) { exit $LASTEXITCODE }

if (Test-Path $build) { Remove-Item $build -Recurse -Force }
# Own build folder: a copy of the app started from src\...\bin would lock that one.
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:DebugType=none -p:Version=$Version -p:OutputPath="$build\bin\" -o $app -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# Uninstall.exe = the setup project built without a payload; it goes into the app folder.
# --no-incremental: the two builds differ only in the embedded resource.
dotnet build $setup -c Release --no-incremental -p:Version=$Version -o "$build\uninstall" -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Copy-Item "$build\uninstall\TPConsole-Setup.exe" "$app\Uninstall.exe"

$payload = Join-Path $build 'app.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($app, $payload, 'Optimal', $false)
dotnet build $setup -c Release --no-incremental -p:Version=$Version -p:Payload=$payload -o "$build\setup" -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$exe = Join-Path $root "dist\TPConsole-Setup-$Version.exe"
New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null
Copy-Item "$build\setup\TPConsole-Setup.exe" $exe -Force
Remove-Item $build -Recurse -Force
Write-Host "Installer: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)" -ForegroundColor Green
