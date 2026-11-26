# laptop-trigger.ps1 - launched over SSH from the VM (trendline/laptop-render.sh).
# Starts the laptop chunk render DETACHED so it survives the SSH session ending.
#
# Why not `start /b` or a plain background call: the Windows OpenSSH server
# tears down the session's process tree on disconnect, and a render started
# that way dies with it. Win32_Process.Create (via Invoke-CimMethod) spawns
# the process outside the SSH session, so it keeps running after the VM
# hangs up - and keeps running through a VM restart, which is the whole
# point of the laptop path. Its stdout/stderr go nowhere (no console), so
# the bash command below redirects everything to the trigger log.
#
# Usage (from the VM):
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\trendline-render\production\laptop-trigger.ps1 -Job job-20261123-150439 -Orientation landscape
# Exit codes: 0 = launched (prints TRIGGERED pid=...), 2 = a render is
# already running for this job+orientation (prints ALREADY-RUNNING),
# 1 = error.
param([string]$Job, [string]$Orientation)

$ProductionDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PkgRoot = Split-Path -Parent $ProductionDir
$JobDir = Join-Path $PkgRoot $Job
$ChunksDir = Join-Path $JobDir "chunks-$Orientation"
$Log = Join-Path $JobDir "chunks-$Orientation.log"
$LockDir = Join-Path $JobDir "chunks-$Orientation.lockdir"
$Marker = Join-Path $JobDir "chunks-$Orientation.triggered"
$TriggerLog = Join-Path $JobDir "chunks-$Orientation.trigger.log"

if ([string]::IsNullOrWhiteSpace($Job) -or ($Orientation -ne "vertical" -and $Orientation -ne "landscape")) {
    Write-Error "usage: laptop-trigger.ps1 -Job <job-id> -Orientation vertical|landscape"
    exit 1
}
if (-not (Test-Path $JobDir)) { Write-Error "no such job dir: $JobDir"; exit 1 }
if (-not (Test-Path (Join-Path $JobDir "remotion-props-1920x1080.json")) -and $Orientation -eq "landscape") {
    Write-Error "landscape props missing in $JobDir - the VM must ship the job first"
    exit 1
}
if (-not (Test-Path (Join-Path $JobDir "remotion-props-1080x1920.json")) -and $Orientation -eq "vertical") {
    Write-Error "vertical props missing in $JobDir - the VM must ship the job first"
    exit 1
}

# Staleness check (mirror of render-chunks.sh's mkdir lock): a lock dir
# with a fresh chunk log means a live render - refuse. A lock with a log
# silent for 30+ minutes means the render died; clear it and take over.
# The render's own mkdir lock is the real enforcer; this is the fast-path
# guard so a duplicate trigger fails here instead of racing it.
if (Test-Path $LockDir) {
    $LogAgeMin = 9999
    if (Test-Path $Log) { $LogAgeMin = ((Get-Date) - (Get-Item $Log).LastWriteTime).TotalMinutes }
    if ($LogAgeMin -lt 30) {
        Write-Output "ALREADY-RUNNING lock=$LockDir log_age_min=$([int]$LogAgeMin)"
        exit 2
    }
    Write-Output "stale lock $LockDir (log silent $([int]$LogAgeMin)m) - taking over"
    Remove-Item -Recurse -Force $LockDir
}

# Git Bash paths: C:\trendline-render -> /c/trendline-render (no spaces in
# any of these paths, so the bash command below needs no quoting at all).
function To-BashPath([string]$WinPath) {
    $p = $WinPath -replace '^([A-Za-z]):', '/$1'
    return $p -replace '\\', '/'
}
$PkgBash = To-BashPath $PkgRoot
$JobBash = "$PkgBash/$Job"
$AppBash = "$PkgBash/remotion-assembler"
$ProdBash = "$PkgBash/production"
$TriggerLogBash = "$JobBash/chunks-$Orientation.trigger.log"

# npm ci first when node_modules is missing or stale (package-lock newer
# than the installed lock). This runs inside the detached process, so the
# trigger SSH call itself stays fast; the poll step sees no chunks until
# the install finishes and the render starts.
$NpmBash = 'cd ' + $AppBash + '; if [ ! -d node_modules ] || [ ! -f node_modules/.package-lock.json ] || [ package-lock.json -nt node_modules/.package-lock.json ]; then npm ci >> ' + $TriggerLogBash + ' 2>&1 || exit 2; fi'
$RenderBash = $ProdBash + '/render-chunks.sh --job ' + $JobBash + ' --orientation ' + $Orientation + ' >> ' + $TriggerLogBash + ' 2>&1'
$BashCmd = "$NpmBash; $RenderBash"

$BashExe = "C:\Program Files\Git\bin\bash.exe"
if (-not (Test-Path $BashExe)) {
    $BashExe = "C:\Program Files (x86)\Git\bin\bash.exe"
    if (-not (Test-Path $BashExe)) { Write-Error "Git Bash not found (looked in Program Files and Program Files (x86))"; exit 1 }
}
$CmdLine = '"' + $BashExe + '" -lc "' + $BashCmd + '"'

$result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $CmdLine }
if ($result.ReturnValue -ne 0) {
    Write-Error "Win32_Process.Create failed (ReturnValue $($result.ReturnValue)) for: $CmdLine"
    exit 1
}

$Stamp = Get-Date -Format o
Set-Content -Path $Marker -Value "triggered_at=$Stamp`npid=$($result.ProcessId)`norientation=$Orientation"
Write-Output "TRIGGERED pid=$($result.ProcessId) job=$Job orientation=$Orientation"
exit 0
