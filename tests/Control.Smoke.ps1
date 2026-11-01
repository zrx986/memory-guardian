param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
$evidence=Join-Path (Split-Path -Parent $PSScriptRoot) 'raw'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$records=@()
function Invoke-Control([string]$Command,[string]$Tag) {
    $file=Join-Path $evidence ('control-'+$Tag+'.json')
    $process=Start-Process -FilePath $Executable -ArgumentList $Command -WindowStyle Hidden -RedirectStandardOutput $file -PassThru -Wait
    [pscustomobject]@{Command=$Command;ExitCode=$process.ExitCode;Data=(Get-Content -LiteralPath $file -Raw|ConvertFrom-Json)}
}
foreach($index in 1..20) {
    $record=Invoke-Control '--status' ('status-'+$index)
    if($record.ExitCode -ne 0 -or -not $record.Data.Running -or -not $record.Data.ReadOnly){throw "Status handshake failed at request $index"}
    $records+=$record
}
$monitorId=$records[0].Data.ProcessId
$paused=Invoke-Control '--pause' 'pause'
if(-not $paused.Data.Paused){throw 'Pause failed'}
$resumed=Invoke-Control '--resume' 'resume'
if($resumed.Data.Paused){throw 'Resume failed'}
$manual=Invoke-Control '--clean' 'readonly-clean'
if($manual.ExitCode -ne 1 -or $manual.Data.Success -ne $false){throw 'Read-only guard failed'}
$after=Invoke-Control '--status' 'after-clean'
if($null -ne $after.Data.LastAttemptUtc){throw 'Read-only mode recorded a cleanup attempt'}
$stop=Invoke-Control '--stop' 'stop'
Start-Sleep -Milliseconds 1000
if(Get-Process -Id $monitorId -ErrorAction SilentlyContinue){throw 'Monitor did not exit'}
[pscustomobject]@{Passed=25;NativeCleanupExecuted=$false;MonitorId=$monitorId;Time=(Get-Date -Format o)} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $evidence 'control-smoke-result.json')
