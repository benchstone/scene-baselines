<#
.SYNOPSIS
    Replays a repository's history through Scene Baselines, newest commit first.

.DESCRIPTION
    The Windows twin of replay-history.sh, for the many Unity developers who have no bash. Both
    drivers do the same small job: the editor decides everything, and this only does the one thing
    an editor cannot do for itself -- check a commit out while no editor is running. Checking out
    underneath a live editor crashes Unity outright when the commit changes scripts
    ([SerializeReference] state cannot survive a reload that swaps assemblies), so each step gets
    its own editor process.

    -Commits counts commits worth replaying, not commits back: one that changed nothing these
    scenes depend on cannot have changed how they look, and is never opened. -Scan bounds how far
    back the editor may look for them, and -AllCommits replays every commit instead.

    The report, the baselines and the run's state file all live outside the inspected project.

    Point this at a throwaway clone rather than the repository you work in. It refuses to start
    when a tracked file has uncommitted changes, when a scene is unsaved, or in Play Mode, and it
    puts the branch back when it finishes or is interrupted -- but a clone makes all of that moot.

.EXAMPLE
    .\replay-history.ps1 -Project C:\clones\my-game -Unity "C:\Program Files\Unity\Hub\Editor\6000.5.1f1\Editor\Unity.exe" -Commits 10 -Scene Assets/Scenes/Level_01.unity
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]   $Project,
    [Parameter(Mandatory = $true)] [string]   $Unity,
                                   [int]      $Commits = 10,
                                   [string[]] $Scene = @(),
                                   [string]   $Out = "",
                                   [string]   $Baselines = "",
                                   [int]      $Scan = 0,
                                   [switch]   $AllCommits
)

$ErrorActionPreference = "Stop"

if ($Out -eq "")       { $Out = Join-Path (Get-Location).Path "scene-baselines-replay" }
if ($Baselines -eq "") { $Baselines = Join-Path $Out "baselines" }

$statePath  = Join-Path $Out "replay-state.json"
$logPath    = Join-Path $Out "replay.log"
$logMarker  = "[Scene Baselines]"

New-Item -ItemType Directory -Force -Path $Out       | Out-Null
New-Item -ItemType Directory -Force -Path $Baselines | Out-Null
if (Test-Path $statePath) { Remove-Item $statePath -Force }

# What the editor prints as it works. Unity says nothing until it exits, and a step takes about a
# minute, so its own lines are followed out of the log while it runs -- silence is the thing that
# makes a first run feel broken. The log is rewritten by each step, so the count restarts with it.
function Show-NewLines {
    param([string] $Path, [int] $Shown)

    if (-not (Test-Path $Path)) { return $Shown }

    try {
        $lines = @(Select-String -Path $Path -SimpleMatch $logMarker | ForEach-Object { $_.Line })
    }
    catch {
        # Unity holds the log open; a read that loses the race is worth nothing but the next tick.
        return $Shown
    }

    if ($lines.Count -gt $Shown) {
        $lines[$Shown..($lines.Count - 1)] | ForEach-Object { Write-Host "  $_" }
        return $lines.Count
    }

    return $Shown
}

# Start-Process joins -ArgumentList with spaces and quotes nothing it is given, so a project path
# with a space in it -- C:\Users\Someone\My Games\x, which is most of Windows -- reaches the editor
# as two arguments and the run dies at step 1 with a project path Unity cannot make sense of. Every
# value is quoted here instead. A path may end in a backslash, which would escape the closing quote,
# so a trailing run of them is doubled the way the Windows command line parser expects. The bash
# twin never had this: "$PROJECT" was quoted there from the start.
function ConvertTo-QuotedArg {
    param([string] $Value)

    if ($Value -notmatch '\s') { return $Value }

    return '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}

# The way back, read before anything moves. The editor records it too and asks for it as the last
# checkout of the run; this is the belt to that pair of braces, for a run killed halfway.
$original = (& git -C $Project rev-parse --abbrev-ref HEAD | Out-String).Trim()
if ($original -eq "HEAD") {
    $original = (& git -C $Project rev-parse HEAD | Out-String).Trim()
}

function Restore-Project {
    Write-Host "Restoring '$Project' to '$original'."
    & git -C $Project checkout --force --quiet $original | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "COULD NOT RESTORE. Run: git -C '$Project' checkout --force '$original'"
    }
}

Write-Host "Replaying $Commits commit(s) in $Project. Will return to '$original'."

$unityArgs = @(
    "-batchmode", "-nographics",
    "-projectPath", $Project,
    "-executeMethod", "SceneBaselines.HistoryReplay.RunStep",
    "-replayState", $statePath,
    "-replayCommits", $Commits,
    "-baselineDir", $Baselines,
    "-reportDir", $Out,
    "-logFile", $logPath
)

foreach ($s in $Scene) { $unityArgs += @("-replayScene", $s) }
if ($Scan -gt 0)       { $unityArgs += @("-replayScan", $Scan) }
if ($AllCommits)       { $unityArgs += @("-replayFilter", "off") }

$editor = $null
$finished = $false
$step = 0

try {
    while ($true) {
        $step++

        if ($step -eq 1) {
            Write-Host "Step 1: starting the editor. This first launch also imports the project, which on a large one takes considerably longer than the minute or so each later step costs."
        }
        else {
            Write-Host "Step ${step}: starting the editor."
        }

        $quotedArgs = @($unityArgs | ForEach-Object { ConvertTo-QuotedArg $_ })
        $editor = Start-Process -FilePath $Unity -ArgumentList $quotedArgs -PassThru
        $shown = 0

        while (-not $editor.HasExited) {
            $shown = Show-NewLines -Path $logPath -Shown $shown
            Start-Sleep -Seconds 2
        }

        $editor.WaitForExit()
        $shown = Show-NewLines -Path $logPath -Shown $shown
        $code = $editor.ExitCode
        $editor = $null

        if ($code -eq 2) {
            Write-Host "Replay refused or failed at step ${step} -- see $logPath"
            exit 2
        }

        if ($code -ne 0 -and $code -ne 7) {
            Write-Host "Unity exited $code at step ${step} -- see $logPath"
            exit $code
        }

        # Both "continue" and "complete" name the next checkout; on the last step it is the way back.
        $next = ""
        $done = $false

        if (Test-Path $statePath) {
            $state = Get-Content -Raw -Path $statePath | ConvertFrom-Json
            $next = $state.nextCheckout
            $done = [bool] $state.done
        }

        if ($next) {
            & git -C $Project -c advice.detachedHead=false checkout --force --quiet $next | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Could not check out $next"
                exit 3
            }
        }

        if ($done) {
            $finished = $true
            Write-Host "Replay complete. Report and state in $Out"
            exit 0
        }
    }
}
finally {
    # Every way out lands here: Ctrl-C, a refusal, a failed checkout, or the end of the run. That is
    # why the error paths above only report and exit -- they used to restore as well, and the run
    # then announced it was restoring twice. An editor left running would hold the project and the
    # log open, and a tree left on some commit from the middle of the run is worse than the one the
    # user started with.
    if ($null -ne $editor) {
        if (-not $editor.HasExited) {
            $editor.Kill()
            $editor.WaitForExit()
        }
    }

    if (-not $finished) { Restore-Project }
}
