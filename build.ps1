# Compiles the app with the C# compiler that ships with Windows. Nothing to install.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

# Secrets.cs holds your token and is git-ignored; create it from the template on first build.
$secrets = Join-Path $src 'Secrets.cs'
if (-not (Test-Path $secrets)) { Copy-Item (Join-Path $src 'Secrets.cs.example') $secrets }

New-Item -ItemType Directory -Force $dist | Out-Null
$out = Join-Path $dist 'WarThunderSocialTap.exe'

$cscArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$out",
             '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
             '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', '/r:System.Security.dll')

# Use the War Thunder logo from your own game install as the .exe icon (it is not shipped in this repo).
$icon = @("$env:LOCALAPPDATA\WarThunder\icon.ico",
          "${env:ProgramFiles(x86)}\Steam\steamapps\common\War Thunder\icon.ico") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if ($icon) { $cscArgs += "/win32icon:$icon" }

$cscArgs += (Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName })

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $out"
