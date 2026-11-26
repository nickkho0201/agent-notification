# Регистрирует периодический обход (вход в Windows + каждые 5 минут) для текущего пользователя.
# Требует собранный exe: scripts\build.ps1 -SkipInstall, затем этот скрипт.
[CmdletBinding()]
param([string]$Exe)

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path $env:LOCALAPPDATA 'AgentNotification\app\AgentNotify.exe' }
if (-not (Test-Path $Exe)) { throw "Нет $Exe — сначала scripts\build.ps1" }

& $Exe install
exit $LASTEXITCODE
