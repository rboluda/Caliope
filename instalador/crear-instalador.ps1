# Publica Calíope (autocontenida, con .NET) y crea el instalador con Inno Setup.
# Uso: pwsh instalador\crear-instalador.ps1
# Resultado: publish\Caliope-<versión>-Instalador.exe
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent

[xml]$props = Get-Content (Join-Path $raiz 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
Write-Host "Calíope $version"

$app = Join-Path $raiz 'publish\app'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish (Join-Path $raiz 'src\Constantino.App') -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -o $app -nologo
if ($LASTEXITCODE) { throw 'Ha fallado la publicación.' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Falta Inno Setup 6: winget install JRSoftware.InnoSetup' }

& $iscc "/DVersion=$version" (Join-Path $PSScriptRoot 'Caliope.iss')
if ($LASTEXITCODE) { throw 'Ha fallado Inno Setup.' }
Get-Item (Join-Path $raiz "publish\Caliope-$version-Instalador.exe")
