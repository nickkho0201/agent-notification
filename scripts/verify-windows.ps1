# Безопасная проверка на реальной Windows БЕЗ выключения, выхода и сна.
# Что проверяем:
#   1) обход и точный триггер запускаются через оконно-невидимый лаунчер (GUI-подсистема,
#      Action/Command = AgentNotify.Launcher.exe), а не напрямую консольным exe;
#   2) Windows сам запускает доставку по сроку (запись становится sent без OpenCode и ручного run-due),
#      при этом процессы лаунчера и дочернего exe не имеют видимых окон;
#   3) миграция уже поставленной (legacy) задачи на лаунчер сохраняет StartBoundary и интерактивный контекст;
#   4) сборка без лаунчера честно предупреждает, а не показывает окно молча;
#   5) догон через периодический обход, если точный триггер пропущен (задача отключена);
#   6) уборка: после доставки задача удалена, тестовые напоминания удалены.
# Общая история уведомлений пользователя НЕ очищается: скрипт работает со своими напоминаниями,
# а при запуске с другим AGENTNOTIFY_HOME — и со своим изолированным хранилищем и задачами.
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $env:LOCALAPPDATA 'AgentNotification\app\AgentNotify.exe'),
    [string]$Launcher,
    [int]$DeliveryWaitSeconds = 240,
    [int]$SweepWaitSeconds = 90
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host "$mark $name :: $detail"
}

function Invoke-AgentNotify([string[]]$arguments) {
    $out = & $Exe @arguments 2>&1
    $code = $LASTEXITCODE
    return [pscustomobject]@{ Code = $code; Text = ($out -join "`n") }
}

function Invoke-AgentNotifyFrom([string]$path, [string[]]$arguments) {
    $out = & $path @arguments 2>&1
    $code = $LASTEXITCODE
    return [pscustomobject]@{ Code = $code; Text = ($out -join "`n") }
}

function Get-Reminder([string]$id) {

    $json = (& $Exe list --all --json) -join "`n"
    $data = $json | ConvertFrom-Json
    return $data.reminders | Where-Object { $_.id -eq $id } | Select-Object -First 1
}

function Get-TaskXml([string]$taskName) {
    $xml = (& schtasks.exe /Query /TN $taskName /XML 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { return $null }
    return $xml
}

function Get-TaskField([string]$xml, [string]$field) {
    if (-not $xml) { return $null }
    if ($xml -match "<$field>([^<]*)</$field>") { return $matches[1] }
    return $null
}

function Get-PeSubsystem([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    return [BitConverter]::ToUInt16($bytes, $peOffset + 0x5C)
}

function Get-LauncherObservations([hashtable]$seen) {
    $observations = @()
    foreach ($launcherProcess in (Get-Process -Name 'AgentNotify.Launcher' -ErrorAction SilentlyContinue)) {
        $observations += [pscustomobject]@{
            Kind = 'launcher'; Id = $launcherProcess.Id
            MainWindowHandle = $launcherProcess.MainWindowHandle
            MainWindowTitle = $launcherProcess.MainWindowTitle
        }
        if (-not $seen.ContainsKey($launcherProcess.Id)) {
            $seen[$launcherProcess.Id] = $true
            foreach ($child in (Get-CimInstance Win32_Process -Filter "ParentProcessId=$($launcherProcess.Id)" -ErrorAction SilentlyContinue)) {
                if ($child.Name -ne 'AgentNotify.exe') { continue }
                $childProcess = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                if ($childProcess) {
                    $observations += [pscustomobject]@{
                        Kind = 'child'; Id = $childProcess.Id
                        MainWindowHandle = $childProcess.MainWindowHandle
                        MainWindowTitle = $childProcess.MainWindowTitle
                    }
                }
            }
        }
    }
    return $observations
}

function Wait-SentWithSampling([string]$id, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $seen = @{}
    $observed = @()
    $launcherSamples = 0
    $childSamples = 0
    $ticks = 0
    while ((Get-Date) -lt $deadline) {
        foreach ($observation in (Get-LauncherObservations $seen)) {
            if ($observation.Kind -eq 'launcher') { $launcherSamples++ } else { $childSamples++ }
            if ($observation.MainWindowHandle -ne 0) { $observed += "$($observation.Kind):$($observation.Id):$($observation.MainWindowTitle)" }
        }
        $ticks++
        if ($ticks % 4 -eq 0) {
            $rem = Get-Reminder $id
            if ($rem -and $rem.state -eq 'sent') {
                return [pscustomobject]@{ Reminder = $rem; LauncherSamples = $launcherSamples; ChildSamples = $childSamples; VisibleWindows = $observed }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return [pscustomobject]@{ Reminder = (Get-Reminder $id); LauncherSamples = $launcherSamples; ChildSamples = $childSamples; VisibleWindows = $observed }
}

if (-not (Test-Path $Exe)) { throw "Нет exe: $Exe (сначала scripts\build.ps1)" }
if (-not $Launcher) { $Launcher = Join-Path (Split-Path -Parent $Exe) 'AgentNotify.Launcher.exe' }

$ids = @()
$taskNames = @()
try {
    Write-Host "Exe: $Exe"
    Write-Host "Launcher: $Launcher"
    Write-Host "Данные: $($env:AGENTNOTIFY_HOME)$(if (-not $env:AGENTNOTIFY_HOME) { ' (по умолчанию)' })"

    Write-Host '--- Оконно-невидимый запуск: лаунчер ---'
    Check 'лаунчер существует рядом с exe' (Test-Path $Launcher) $Launcher
    if (Test-Path $Launcher) {
        $subsystem = Get-PeSubsystem $Launcher
        Check 'лаунчер имеет GUI-подсистему (окно консоли не создаётся)' ($subsystem -eq 2) "PE subsystem=$subsystem (2=GUI)"
    } else {
        Check 'лаунчер имеет GUI-подсистему (окно консоли не создаётся)' $false 'лаунчер отсутствует'
    }

    Write-Host '--- Установка обхода ---'
    $install = Invoke-AgentNotify @('install', '--json')
    $installJson = $install.Text | ConvertFrom-Json
    Check 'install: sweep зарегистрирован' ($installJson.sweepRegistered -eq $true) $installJson.sweepTask
    $sweepTask = $installJson.sweepTask
    Check 'install: обход использует лаунчер' ($installJson.usesLauncher -eq $true) "$($installJson.sweepCommand)"
    $sweepXml = Get-TaskXml $sweepTask
    Check 'задача обхода запускает лаунчер' ((Get-TaskField $sweepXml 'Command') -eq $Launcher) "Command=$(Get-TaskField $sweepXml 'Command')"

    Write-Host '--- Лаунчер передаёт код выхода дочернего процесса ---'
    $blockedHome = Join-Path ([System.IO.Path]::GetTempPath()) ("agentnotify-not-a-dir-" + [Guid]::NewGuid().ToString('n') + ".txt")
    Set-Content -Path $blockedHome -Value 'файл вместо каталога' -Encoding UTF8
    try {
        $null = & $Launcher run-due --home $blockedHome 2>&1 | Out-Null
        $launcherExit = $LASTEXITCODE
        $null = & $Exe run-due --home $blockedHome 2>&1 | Out-Null
        $childExit = $LASTEXITCODE
        Check 'код выхода дочернего процесса передан лаунчером' ($launcherExit -eq $childExit -and $launcherExit -ne 0) "launcher=$launcherExit child=$childExit"
    } finally {
        Remove-Item -LiteralPath $blockedHome -Force -ErrorAction SilentlyContinue
    }
    $null = & $Launcher schedule 2>&1 | Out-Null
    Check 'лаунчер отклоняет чужие команды' ($LASTEXITCODE -eq 2) "exit=$LASTEXITCODE"

    Write-Host '--- Точный триггер: граница не раньше срока ---'
    $due = (Get-Date).AddSeconds(95.400)
    $at = $due.ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
    $created = (Invoke-AgentNotify @('schedule', '--at', $at, '--text', "E2E точный триггер: <b>&""кавычки""</b>`nстрока 2 — Юникод ✓", '--json')).Text | ConvertFrom-Json
    $ids += $created.id
    $taskNames += $created.windowsTaskName
    Check 'schedule: точный триггер зарегистрирован' ($created.windowsTaskRegistered -eq $true) "$($created.id) $($created.windowsTaskName)"

    $taskXml = Get-TaskXml $created.windowsTaskName
    Check 'задача напоминания запускает лаунчер' ((Get-TaskField $taskXml 'Command') -eq $Launcher) "Command=$(Get-TaskField $taskXml 'Command')"
    if ($taskXml -match '<StartBoundary>([^<]+)</StartBoundary>') {
        $boundary = [datetimeoffset]::Parse($matches[1], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None)
        # ConvertFrom-Json превращает ISO-строку в DateTime: привод обратно к instant без потери смещения.
        if ($created.dueUtc -is [datetime]) {
            $utc = if ($created.dueUtc.Kind -eq 'Utc') { $created.dueUtc } else { [datetime]::SpecifyKind($created.dueUtc, [DateTimeKind]::Utc) }
            $dueInstant = [datetimeoffset]::new($utc)
        } else {
            $dueInstant = [datetimeoffset]::Parse($created.dueUtc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal)
        }
        Check 'StartBoundary не раньше due' ($boundary -ge $dueInstant) "boundary=$($boundary.ToString('o')) due=$($dueInstant.ToString('o'))"
        Check 'StartBoundary в пределах 1 с от due' (($boundary - $dueInstant).TotalSeconds -le 1.0) "delta=$(($boundary - $dueInstant).TotalSeconds)s"
    } else {
        Check 'StartBoundary найден в XML задачи' $false 'не найден'
    }

    Write-Host '--- Ожидание доставки Windows по сроку (OpenCode не участвует) ---'
    $sweepResult = Wait-SentWithSampling $created.id $DeliveryWaitSeconds
    $sent = $sweepResult.Reminder
    Check 'Windows доставил напоминание по сроку' ($sent.state -eq 'sent') "state=$($sent.state) sentUtc=$($sent.sentUtc) attempts=$($sent.attempts)"
    Check 'лаунчер наблюдался при запуске Планировщиком' ($sweepResult.LauncherSamples -gt 0) "наблюдений лаунчера=$($sweepResult.LauncherSamples), дочернего exe=$($sweepResult.ChildSamples)"
    $visible = @($sweepResult.VisibleWindows | Where-Object { $_ })
    Check 'у процессов доставки не наблюдалось видимых окон' ($visible.Count -eq 0) $(if ($visible.Count -eq 0) { 'видимых окон не зафиксировано' } else { $visible -join '; ' })
    Start-Sleep -Seconds 3
    $taskLeft = (& schtasks.exe /Query /TN $created.windowsTaskName 2>&1) -join "`n"
    Check 'одноразовая задача убрана после доставки' ($LASTEXITCODE -ne 0) 'schtasks /Query не находит задачу'

    Write-Host '--- Миграция уже поставленной (legacy) задачи на лаунчер ---'
    $dueLegacy = (Get-Date).AddMinutes(30)
    $atLegacy = $dueLegacy.ToString('yyyy-MM-ddTHH:mm:sszzz')
    $legacy = (Invoke-AgentNotify @('schedule', '--at', $atLegacy, '--text', 'E2E миграция legacy задачи', '--json')).Text | ConvertFrom-Json
    $ids += $legacy.id
    $taskNames += $legacy.windowsTaskName
    $beforeXml = Get-TaskXml $legacy.windowsTaskName
    $boundaryBefore = Get-TaskField $beforeXml 'StartBoundary'
    $userBefore = Get-TaskField $beforeXml 'UserId'
    # Имитируем задачу, поставленную прошлой сборкой: Command = консольный exe.
    $legacyXml = $beforeXml -replace '(?<=<Command>)[^<]*(?=</Command>)', [System.Security.SecurityElement]::Escape($Exe)
    $legacyFile = Join-Path ([System.IO.Path]::GetTempPath()) ("agentnotify-legacy-" + [Guid]::NewGuid().ToString('n') + ".xml")
    try {
        [System.IO.File]::WriteAllText($legacyFile, $legacyXml, [System.Text.Encoding]::Unicode)
        & schtasks.exe /Create /TN $legacy.windowsTaskName /XML $legacyFile /F | Out-Null
        Check 'legacy-задача подготовлена (Command = консольный exe)' ($LASTEXITCODE -eq 0 -and (Get-TaskField (Get-TaskXml $legacy.windowsTaskName) 'Command') -eq $Exe) "Command=$(Get-TaskField (Get-TaskXml $legacy.windowsTaskName) 'Command')"
        $repair = (Invoke-AgentNotify @('repair', '--json')).Text | ConvertFrom-Json
        Check 'repair перевёл legacy-задачу на лаунчер' ($repair.migrated -ge 1) "migrated=$($repair.migrated) alreadyOk=$($repair.alreadyOk) failed=$($repair.failed) remaining=$($repair.remaining)"
        $afterXml = Get-TaskXml $legacy.windowsTaskName
        Check 'после миграции задача запускает лаунчер' ((Get-TaskField $afterXml 'Command') -eq $Launcher) "Command=$(Get-TaskField $afterXml 'Command')"
        Check 'миграция сохранила StartBoundary' ($boundaryBefore -and $boundaryBefore -eq (Get-TaskField $afterXml 'StartBoundary')) "до=$boundaryBefore после=$(Get-TaskField $afterXml 'StartBoundary')"
        Check 'миграция сохранила интерактивный контекст' ((Get-TaskField $afterXml 'LogonType') -eq 'InteractiveToken' -and $userBefore -eq (Get-TaskField $afterXml 'UserId')) "LogonType=$(Get-TaskField $afterXml 'LogonType') userId=$(Get-TaskField $afterXml 'UserId')"
        Check 'запись напоминания не изменилась' ((Get-Reminder $legacy.id).state -eq 'pending') "state=$((Get-Reminder $legacy.id).state)"
    } finally {
        Remove-Item -LiteralPath $legacyFile -Force -ErrorAction SilentlyContinue
    }

    Write-Host '--- Обход продолжает миграцию, если установка её не закончила ---'
    # Отметка задачи-команды: RunDue мигрирует только при расхождении, поэтому удаляем её,
    # имитируя прерванную установку, и запускаем обход штатно через Планировщик.
    $homeDir = if ($env:AGENTNOTIFY_HOME) { $env:AGENTNOTIFY_HOME } else { Join-Path $env:LOCALAPPDATA 'AgentNotification' }
    $markerFile = Join-Path $homeDir 'task-command.txt'
    $dueSweep = (Get-Date).AddMinutes(50)
    $bySweep = (Invoke-AgentNotify @('schedule', '--at', $dueSweep.ToString('yyyy-MM-ddTHH:mm:sszzz'), '--text', 'E2E миграция обходом', '--json')).Text | ConvertFrom-Json
    $ids += $bySweep.id
    $taskNames += $bySweep.windowsTaskName
    $sweepLegacyXml = (Get-TaskXml $bySweep.windowsTaskName) -replace '(?<=<Command>)[^<]*(?=</Command>)', [System.Security.SecurityElement]::Escape($Exe)
    $sweepLegacyFile = Join-Path ([System.IO.Path]::GetTempPath()) ("agentnotify-legacy2-" + [Guid]::NewGuid().ToString('n') + ".xml")
    try {
        [System.IO.File]::WriteAllText($sweepLegacyFile, $sweepLegacyXml, [System.Text.Encoding]::Unicode)
        & schtasks.exe /Create /TN $bySweep.windowsTaskName /XML $sweepLegacyFile /F | Out-Null
    } finally {
        Remove-Item -LiteralPath $sweepLegacyFile -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $markerFile -Force -ErrorAction SilentlyContinue
    & schtasks.exe /Run /TN $sweepTask | Out-Null
    $migratedBySweep = $false
    $sweepMigrateDeadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $sweepMigrateDeadline) {
        if ((Get-TaskField (Get-TaskXml $bySweep.windowsTaskName) 'Command') -eq $Launcher -and (Test-Path $markerFile)) {
            $migratedBySweep = $true
            break
        }
        Start-Sleep -Seconds 3
    }
    Check 'обход перевёл legacy-задачу и записал отметку' $migratedBySweep "Command=$(Get-TaskField (Get-TaskXml $bySweep.windowsTaskName) 'Command'), отметка=$(Test-Path $markerFile)"

    Write-Host '--- Сборка без лаунчера не должна молча показывать окно ---'
    $noLauncherDir = Join-Path ([System.IO.Path]::GetTempPath()) ("agentnotify-nolauncher-" + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $noLauncherDir | Out-Null
    try {
        Get-ChildItem (Split-Path -Parent $Exe) -File | Where-Object { $_.Name -notlike 'AgentNotify.Launcher.*' } | Copy-Item -Destination $noLauncherDir
        $noLauncherExe = Join-Path $noLauncherDir 'AgentNotify.exe'
        $noLauncherJson = (Invoke-AgentNotifyFrom $noLauncherExe @('schedule', '--at', (Get-Date).AddMinutes(45).ToString('yyyy-MM-ddTHH:mm:sszzz'), '--text', 'E2E без лаунчера', '--json')).Text | ConvertFrom-Json
        $ids += $noLauncherJson.id
        $noLauncherImportant = @($noLauncherJson.warnings) -join ' | '
        Check 'без лаунчера есть явное предупреждение об окне' ($noLauncherImportant -match 'окно терминала') $noLauncherImportant
        if ($noLauncherJson.windowsTaskName) {
            $taskNames += $noLauncherJson.windowsTaskName
            Check 'без лаунчера задача указывает на консольный exe (видно в статусе)' ((Get-TaskField (Get-TaskXml $noLauncherJson.windowsTaskName) 'Command') -eq $noLauncherExe) "Command=$(Get-TaskField (Get-TaskXml $noLauncherJson.windowsTaskName) 'Command')"
        } else {
            Check 'без лаунчера задача указывает на консольный exe (видно в статусе)' $false 'задача не зарегистрирована'
        }
    } finally {
        Remove-Item -LiteralPath $noLauncherDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host '--- Догон обходом при пропущенном точном триггере ---'
    $due2 = (Get-Date).AddSeconds(60)
    $at2 = $due2.ToString('yyyy-MM-ddTHH:mm:sszzz')
    $created2 = (Invoke-AgentNotify @('schedule', '--at', $at2, '--text', 'E2E догон обходом', '--json')).Text | ConvertFrom-Json
    $ids += $created2.id
    $taskNames += $created2.windowsTaskName
    & schtasks.exe /Change /TN $created2.windowsTaskName /DISABLE | Out-Null
    Check 'точный триггер отключён для проверки обхода' ($LASTEXITCODE -eq 0) $created2.windowsTaskName

    Start-Sleep -Seconds ([Math]::Max(0, [int]($due2 - (Get-Date)).TotalSeconds + 20))
    $run = & schtasks.exe /Run /TN $sweepTask 2>&1
    Check 'обход запущен вручную (эмуляция входа/пробуждения)' ($LASTEXITCODE -eq 0) (($run -join ' ').Trim())
    $deadline2 = (Get-Date).AddSeconds($SweepWaitSeconds)
    $sent2 = $null
    while ((Get-Date) -lt $deadline2) {
        $sent2 = Get-Reminder $created2.id
        if ($sent2 -and $sent2.state -eq 'sent') { break }
        Start-Sleep -Seconds 5
    }
    Check 'просроченная запись доставлена обходом' ($sent2.state -eq 'sent') "state=$($sent2.state) attempts=$($sent2.attempts)"
} finally {
    Write-Host '--- Уборка тестовых артефактов ---'
    foreach ($id in $ids) {
        $res = Invoke-AgentNotify @('cancel', $id, '--purge', '--json')
        Write-Host "cancel $id -> $($res.Text)"
        # Если запись уже доставлена, cancel честно откажет; тестовую запись убираем из истории.
        $state = Get-Reminder $id
        if ($state -and $state.state -in @('sent', 'cancelled')) {
            $purged = Invoke-AgentNotify @('purge', $id, '--json')
            Write-Host "purge $id -> $($purged.Text)"
        }
    }
    foreach ($task in $taskNames) {
        if ($task) { & schtasks.exe /Delete /TN $task /F 2>&1 | Out-Null }
    }
    $left = (& $Exe list --all --json) -join "`n" | ConvertFrom-Json
    $leftover = @($left.reminders | Where-Object { $ids -contains $_.id })
    Check 'тестовых напоминаний не осталось' ($leftover.Count -eq 0) "осталось=$($leftover.Count), всего в истории=$(@($left.reminders).Count)"
}

$failed = @($results | Where-Object { -not $_.Ok }).Count
Write-Host ""
Write-Host "Итого: $($results.Count - $failed)/$($results.Count) PASS"
if ($failed -gt 0) { exit 1 }
exit 0
