# Builds the app, installs it for the current user, makes it start with Windows, and launches it.
# Run this again any time you change Config.cs or Secrets.cs.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root 'build.ps1')

$name    = 'WarThunderSocialTap'
$target  = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe     = Join-Path $target "$name.exe"

Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $root "dist\$name.exe") $exe -Force

# Start with Windows (current user only, no admin rights needed).
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $name -Value "`"$exe`""

Start-Process $exe
"Installed to $exe, registered for startup, and started."
