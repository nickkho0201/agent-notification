# Откат: удаляет задачи Планировщика ЭТОГО пользователя, созданные AgentNotify.
# -PurgeData дополнительно удаляет очередь SQLite и журнал (безвозвратно).
[CmdletBinding()]
param(
    [string]$Exe,
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path $env:LOCALAPPDATA 'AgentNotification\app\AgentNotify.exe' }
if (-not (Test-Path $Exe)) { throw "Нет $Exe" }

if ($PurgeData) { & $Exe uninstall --purge-data } else { & $Exe uninstall }
exit $LASTEXITCODE
