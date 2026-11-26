# Изолированная проверка update_reminder на реальном Планировщике Windows.
# Создаёт ОДНУ собственную тестовую задачу в изолированном AGENTNOTIFY_HOME (имя задачи включает
# хэш хранилища), переносит срок, проверяет XML задачи до и после и удаляет только свои артефакты.
# Live-очередь, live-задачи и пользовательские записи не затрагиваются.
# Параметры:
#   -Exe  путь к AgentNotify.exe тестовой сборки (обязателен)
#   -Home отдельный каталог данных (по умолчанию временный, удаляется в конце)
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$DataHome
)

$ErrorActionPreference = 'Stop'
$Exe = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $Exe)) { throw "Не найден $Exe" }
$launcher = Join-Path (Split-Path -Parent $Exe) 'AgentNotify.Launcher.exe'

$ownHome = -not $DataHome
if ($ownHome) {
    $DataHome = Join-Path ([System.IO.Path]::GetTempPath()) ('agentnotify-update-' + [guid]::NewGuid().ToString('n'))
}
$DataHome = [System.IO.Path]::GetFullPath($DataHome)
New-Item -ItemType Directory -Force -Path $DataHome | Out-Null
$previousHome = $env:AGENTNOTIFY_HOME
$env:AGENTNOTIFY_HOME = $DataHome

$results = New-Object System.Collections.Generic.List[object]
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail })
    $status = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0} {1}{2}" -f $status, $Name, $(if ($Detail) { "  [$Detail]" } else { '' }))
}

function Invoke-Cli([string[]]$Arguments) {
    $out = & $Exe @Arguments 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($out -join "`n") }
}
function Cli-Json([string[]]$Arguments) {
    $result = Invoke-Cli (@($Arguments) + '--json')
    return [pscustomobject]@{ Code = $result.Code; Output = $result.Output; Json = ($result.Output | ConvertFrom-Json) }
}
function Get-TaskXml([string]$TaskName) {
    if (-not $TaskName) { return $null }
    $raw = & schtasks.exe /Query /TN $TaskName /XML 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($raw -join "`n")
}
function Get-StartBoundary([string]$Xml) {
    $doc = [xml]$Xml
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    return $doc.SelectSingleNode('//t:Triggers/t:TimeTrigger/t:StartBoundary', $ns).InnerText
}
function Get-XmlHash([string]$Xml) {
    return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Xml)))
}

$id = $null
$taskName = $null
try {
    # 1. Создание собственного напоминания в изолированном хранилище.
    $at1 = (Get-Date).AddMinutes(11)
    $at2 = (Get-Date).AddMinutes(23)
    $iso1 = $at1.ToString("yyyy-MM-ddTHH:mm:sszzz")
    $iso2 = $at2.ToString("yyyy-MM-ddTHH:mm:sszzz")
    $created = Cli-Json @('schedule', '--at', $iso1, '--text', 'TASK-4 изолированная проверка update')
    Check 'schedule: запись создана и триггер зарегистрирован' ($created.Code -eq 0 -and $created.Json.windowsTaskRegistered -eq $true) $created.Output
    $id = $created.Json.id
    $taskName = $created.Json.windowsTaskName
    Check 'schedule: имя задачи получено' ([bool]$taskName) "$taskName"

    $xml1 = Get-TaskXml $taskName
    $boundary1 = if ($xml1) { Get-StartBoundary $xml1 } else { $null }
    Check 'задача Windows существует до переноса' ([bool]$boundary1) "$boundary1"
    $hash1 = if ($xml1) { Get-XmlHash $xml1 } else { $null }

    # 2. Текстовая правка: XML задачи не должен измениться.
    $textUpdate = Cli-Json @('update', $id, '--text', 'TASK-4 новый текст ✓')
    Check 'update text: код 0, changed/textChanged' ($textUpdate.Code -eq 0 -and $textUpdate.Json.changed -eq $true -and $textUpdate.Json.textChanged -eq $true) $textUpdate.Output
    Check 'update text: срок и id не изменились' ($textUpdate.Json.dueChanged -eq $false -and $textUpdate.Json.id -eq $id -and $textUpdate.Json.syncPending -eq $false)
    $xmlText = Get-TaskXml $taskName
    $hashText = if ($xmlText) { Get-XmlHash $xmlText } else { $null }
    Check 'update text: XML задачи не изменился (триггер не тронут)' ($hashText -and $hashText -eq $hash1)

    # 3. Перенос срока: та же задача, новая граница не раньше нового срока.
    $dueUpdate = Cli-Json @('update', $id, '--at', $iso2)
    Check 'update due: код 0, dueChanged, syncPending=false' ($dueUpdate.Code -eq 0 -and $dueUpdate.Json.dueChanged -eq $true -and $dueUpdate.Json.syncPending -eq $false) $dueUpdate.Output
    Check 'update due: id и имя задачи прежние' ($dueUpdate.Json.id -eq $id -and $dueUpdate.Json.windowsTaskName -eq $taskName) "$($dueUpdate.Json.id) / $($dueUpdate.Json.windowsTaskName)"

    $xml2 = Get-TaskXml $taskName
    $boundary2 = if ($xml2) { Get-StartBoundary $xml2 } else { $null }
    Check 'update due: задача перерегистрирована с новым StartBoundary' ($boundary2 -and $boundary2 -ne $boundary1) "$boundary1 -> $boundary2"

    $expected = [DateTimeOffset]::Parse($iso2).ToLocalTime()
    $remainder = $expected.Ticks % [TimeSpan]::TicksPerSecond
    if ($remainder -ne 0) { $expected = $expected.AddTicks([TimeSpan]::TicksPerSecond - $remainder) }
    $actual = [DateTimeOffset]::Parse($boundary2)
    Check 'update due: граница равна новому сроку (в пределах секунды, не раньше)' ([math]::Abs(($actual - $expected).TotalSeconds) -le 1 -and $actual -ge [DateTimeOffset]::Parse($iso2).AddSeconds(-1)) "$boundary2 vs $($expected.ToString('yyyy-MM-ddTHH:mm:sszzz'))"

    if (Test-Path $launcher) {
        $command = (([xml]$xml2).SelectSingleNode('//*[local-name()="Actions"]/*[local-name()="Exec"]/*[local-name()="Command"]')).InnerText
        Check 'update due: задача по-прежнему запускает лаунчер' ($command -eq $launcher) $command
    }
    else {
        Check 'update due: лаунчер рядом со сборкой' $false "нет $launcher"
    }

    # 4. Идемпотентный повтор и отсутствие дубликатов.
    $noop = Cli-Json @('update', $id, '--at', $iso2, '--text', 'TASK-4 новый текст ✓')
    Check 'update: повтор идемпотентен' ($noop.Code -eq 0 -and $noop.Json.changed -eq $false)
    $queryAll = & schtasks.exe /Query /FO CSV /NH 2>&1
    $sameName = @($queryAll | Where-Object { $_ -like "*$taskName*" })
    Check 'в Планировщике ровно одна задача с этим именем' ($sameName.Count -eq 1) "$($sameName.Count)"

    $listed = Cli-Json @('list', '--id', $id)
    Check 'очередь: одна pending-запись с прежним id' ($listed.Json.returned -eq 1 -and $listed.Json.reminders[0].id -eq $id -and $listed.Json.reminders[0].state -eq 'pending') $listed.Output
}
catch {
    Check 'исключение проверки' $false $_.Exception.Message
}
finally {
    # 5. Очистка только собственных тестовых артефактов.
    if ($id) {
        $cancel = Invoke-Cli @('cancel', $id, '--purge', '--json')
        Check 'очистка: тестовая запись отменена и удалена' ($cancel.Code -eq 0) $cancel.Output
    }
    if ($taskName) {
        $left = Get-TaskXml $taskName
        Check 'очистка: тестовой задачи Windows не осталось' ($null -eq $left)
    }
    if ($previousHome) { $env:AGENTNOTIFY_HOME = $previousHome } else { Remove-Item Env:AGENTNOTIFY_HOME -ErrorAction SilentlyContinue }
    if ($ownHome) {
        try { Remove-Item -Recurse -Force $DataHome } catch { }
    }
}

$failed = @($results | Where-Object { -not $_.Ok }).Count
Write-Host ("`nИтого: {0}/{1} PASS" -f ($results.Count - $failed), $results.Count)
if ($failed -gt 0) { exit 1 }
exit 0

