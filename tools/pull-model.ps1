# Resilient model puller: keeps retrying with resume until the model is fully downloaded.
#
# Why this exists: large pulls (17 GB) on a flaky network stall repeatedly --
# Ollama logs "part N stalled; retrying" and can sit there for a long time.
# `ollama pull` resumes from the partial blobs, so simply retrying is safe and
# does NOT restart the download from zero.
#
# NOTE: this file must stay pure ASCII. Windows PowerShell 5.1 decodes .ps1 files
# using the ANSI codepage, so non-ASCII source breaks parsing.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\pull-model.ps1 -Model smtek/Qwen3.8-27B:Q4_K_M
param(
    [Parameter(Mandatory = $true)][string]$Model,
    [int]$MaxAttempts = 200,
    [int]$SleepSeconds = 30
)

$ErrorActionPreference = 'Continue'
$logPath = Join-Path $PSScriptRoot 'pull-model.log'

function Write-Log([string]$message) {
    $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $message
    Write-Output $line
    try { Add-Content -Path $logPath -Value $line -Encoding UTF8 } catch { }
}

# Already present? Nothing to do.
$present = & ollama list 2>$null | Select-String -SimpleMatch $Model
if ($present) { Write-Log "already present: $Model"; exit 0 }

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    Write-Log "attempt $attempt/$MaxAttempts : ollama pull $Model"
    & ollama pull $Model *>> $logPath
    if ($LASTEXITCODE -eq 0) {
        Write-Log "SUCCESS: $Model"
        exit 0
    }
    Write-Log "pull returned exit code $LASTEXITCODE; resuming in $SleepSeconds s (resume, not restart)"
    Start-Sleep -Seconds $SleepSeconds
}

Write-Log "gave up after $MaxAttempts attempts"
exit 1
