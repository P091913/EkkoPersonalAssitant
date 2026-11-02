# Publishes Ekko.PcAgent and runs it at logon as you. It must run in your desktop session: a Windows service
# (session 0) can't press keys or lock the screen. Re-run after changing the agent's code or config.
#
#   .\install.ps1            publish, (re)register the logon task, start it
#   .\install.ps1 -Remove    stop it and remove the task
param([switch]$Remove)
$ErrorActionPreference = 'Stop'

$Task = 'Ekko PC Agent'
$Home_ = Join-Path $env:LOCALAPPDATA 'Ekko\pc-agent'
$Runner = Join-Path $Home_ 'run.cmd'

Stop-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
Get-Process Ekko.PcAgent -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Remove) {
    Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Removed. Files are left in $Home_."
    return
}

# The shared secret lives in the solution's .env, next to the Brain's copy of it.
$envFile = Join-Path (Split-Path $PSScriptRoot -Parent) '.env'
$key = (Get-Content $envFile | Where-Object { $_ -like 'PcService__ApiKey=*' } | Select-Object -First 1) -replace '^PcService__ApiKey=', ''
if (-not $key) { throw "PcService__ApiKey is missing from $envFile" }

dotnet publish (Join-Path $PSScriptRoot 'Ekko.PcAgent.csproj') -c Release -o $Home_ --nologo -v q
if ($LASTEXITCODE) { throw 'Publish failed; the old copy was left in place but is stopped.' }
@{ Api = @{ Key = $key } } | ConvertTo-Json | Set-Content (Join-Path $Home_ 'appsettings.Production.json')

Set-Content $Runner -Encoding ascii -Value @(
    '@echo off',
    "cd /d `"$Home_`"",
    "`"$Home_\Ekko.PcAgent.exe`" >> `"$Home_\pc-agent.log`" 2>&1")
# conhost --headless: no console window on your desktop.
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless cmd.exe /c `"$Runner`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -MultipleInstances IgnoreNew -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $Task -Action $action -Trigger $trigger -Settings $settings -Force `
    -Description 'Ekko PC agent: presses configured keys and launches configured apps for the Ekko Brain.' | Out-Null
Start-ScheduledTask -TaskName $Task
Write-Host "Started. Log: $Home_\pc-agent.log"
