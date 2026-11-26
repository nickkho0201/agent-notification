# Сборка и публикация AgentNotify в стабильный каталог.
# AUMID toast привязан к пути exe, поэтому публикуем всегда в одно место:
#   %LOCALAPPDATA%\AgentNotification\app\AgentNotify.exe
# Рядом публикуется оконно-невидимый лаунчер AgentNotify.Launcher.exe: именно его запускает
# Планировщик заданий, чтобы окно терминала не появлялось.
# Параметры:
#   -SkipInstall  не регистрирует обход и не выполняет миграцию задач.
#   -Target       каталог публикации (по умолчанию стабильный live-каталог). Для staging
#                 укажите отдельный путь: install при этом не запускается, live не трогается.
[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [string]$Target
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$liveTarget = Join-Path $env:LOCALAPPDATA 'AgentNotification\app'
if (-not $Target) { $Target = $liveTarget }
$Target = [System.IO.Path]::GetFullPath($Target)
$liveTarget = [System.IO.Path]::GetFullPath($liveTarget)
$isLive = ($Target.TrimEnd('\') -ieq $liveTarget.TrimEnd('\'))

Write-Host "Публикация в $Target"
dotnet publish (Join-Path $root 'src\AgentNotify\AgentNotify.csproj') -c Release -o $Target --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish AgentNotify завершился с кодом $LASTEXITCODE" }

dotnet publish (Join-Path $root 'src\AgentNotify.Launcher\AgentNotify.Launcher.csproj') -c Release -o $Target --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish AgentNotify.Launcher завершился с кодом $LASTEXITCODE" }

$exe = Join-Path $Target 'AgentNotify.exe'
$launcher = Join-Path $Target 'AgentNotify.Launcher.exe'
if (-not (Test-Path $exe)) { throw "Не найден $exe" }
if (-not (Test-Path $launcher)) { throw "Не найден $launcher" }

if ($SkipInstall) {
    Write-Host "Пропуск install (-SkipInstall)."
} elseif (-not $isLive) {
    # Задачи Планировщика должны указывать на live-каталог: в staging только сборка.
    Write-Host "Staging-каталог: install и миграция задач не выполняются."
} else {
    & $exe install
    if ($LASTEXITCODE -ne 0) { throw "install завершился с кодом $LASTEXITCODE" }
    & $exe status
}

Write-Host "Готово: $exe"
Write-Host "Лаунчер: $launcher"
