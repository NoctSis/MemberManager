# Smoke suite runner - runs all smoke scripts and aggregates results
# Pure ASCII only (no BOM, no CJK)

param(
    [string]$Pattern = "*smoke*.ps1"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $PSScriptRoot "_smoke_runner_result.txt"
$summary = @()
$scripts = Get-ChildItem -Path (Join-Path $PSScriptRoot "*smoke*.ps1") | Where-Object { $_.Name -ne "_smoke_runner.ps1" } | Sort-Object Name

# Warm up the beta database before any script runs. BLL SQL now references
# incremental PatchSchema columns (orders.voided_at), but a freshly rebuilt
# beta\ keeps the old data\member.db. Scripts that only load assemblies and
# query it would then fail with "no such column" and look like a regression.
# Running EnsureDatabase once here makes the suite independent of whether the
# app happened to be launched after the last compile.
$x86 = "$env:windir\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
$warm = "`$env:MEMBERMANAGER_ROOT = '$root\beta'; " +
    "[void][System.Reflection.Assembly]::LoadFrom('$root\beta\MemberManager.Models.dll'); " +
    "[void][System.Reflection.Assembly]::LoadFrom('$root\beta\MemberManager.Common.dll'); " +
    "[void][System.Reflection.Assembly]::LoadFrom('$root\beta\MemberManager.DAL.dll'); " +
    "[void][System.Reflection.Assembly]::LoadFrom('$root\beta\MemberManager.BLL.dll'); " +
    "[void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()"
& $x86 -NoProfile -ExecutionPolicy Bypass -Command $warm 2>&1 | Out-Null
$warmExit = $LASTEXITCODE

$summary += "=== Smoke Suite Runner ==="
$summary += "Started: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
$summary += "Scripts found: " + $scripts.Count
if ($warmExit -ne 0) { $summary += "WARN: DB warm-up failed (exit=$warmExit)" }
$summary += ""

$totalPass = 0
$totalFail = 0
$totalScripts = $scripts.Count
$passScripts = 0
$failScripts = 0

foreach ($s in $scripts) {
    $name = $s.Name
    $summary += "----------------------------------------"
    $summary += "RUN: $name"
    $start = Get-Date
    try {
        # Run under x86 PowerShell for SQLite interop
        $exe = "$env:windir\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
        $out = & $exe -NoProfile -ExecutionPolicy Bypass -File $s.FullName 2>&1 | Out-String
        $exit = $LASTEXITCODE
        $duration = ((Get-Date) - $start).TotalSeconds

        # Detect PASS/FAIL from output. Match only LINE-LEADING tokens: a bare
        # substring scan false-positives on summary counters such as
        # 'FAILCOUNT=0' / 'FAILURES=0' that merely contain the substring FAIL
        # (o2-paging-smoke was marked FAIL this way on 09-29 while passing 41/41).
        #
        # 10-13: two blind spots, both bitten by real bugs:
        #  (a) Prefixed status lines were invisible - dash/smoke/member/records
        #      print 'DASH FAIL: ...' / 'RECORDS FAILED: ...' on their exception
        #      path, where FAIL is NOT at line start, so the old pattern
        #      `^[ \t]*FAIL\b` counted zero and the failure was judged PASS
        #      (exactly what hid the kindIS bug). So allow an UPPERCASE-WORD +
        #      space prefix plus FAILED, and use `(?![=A-Z0-9_])` to exclude
        #      counters such as FAIL=0 / FAILURES=0 / PASS=14 FAIL=0.
        #  (b) Some scripts end with an explicit counter 'RESULT FAIL=3' /
        #      'FAILURES=2' / 'PASS=14 FAIL=0' - parse it separately; any
        #      non-zero value is a failure regardless of line shape.
        $passCount = ([regex]::Matches($out, "(?m)^[ \t]*PASS\b")).Count
        $failCount = ([regex]::Matches($out, "(?m)^[ \t]*(?:[A-Z0-9_]+[ \t]+)*FAIL(?:ED)?\b(?![=A-Z0-9_])")).Count
        $counterFail = 0
        foreach ($mm in ([regex]::Matches($out, "(?m)(?:RESULT[ \t]+)?FAIL(?:URES)?[ \t]*=[ \t]*(\d+)"))) {
            $cv = 0
            if ([int]::TryParse($mm.Groups[1].Value, [ref]$cv) -and $cv -gt 0) { $counterFail += $cv }
        }
        $okCount = ([regex]::Matches($out, "OK\b")).Count
        $errCount = ([regex]::Matches($out, "error\b|Exception|exception")).Count

        # Heuristic: script passes if exit code 0 and no FAIL in output and no
        # non-zero failure counter. errCount is diagnostic only (not gating):
        # several scripts legitimately print exception text in passing paths.
        $scriptPass = ($exit -eq 0) -and ($failCount -eq 0) -and ($counterFail -eq 0)

        $status = if ($scriptPass) { "PASS" } else { "FAIL" }
        if ($scriptPass) { $passScripts++; $totalPass += $passCount } else { $failScripts++; $totalFail += ($failCount + $counterFail) }

        $summary += "  Exit: $exit | Duration: $duration s"
        $summary += "  PASS lines: $passCount | FAIL lines: $failCount | counters: $counterFail | OK lines: $okCount | errHits: $errCount"
        $summary += "  Script Result: $status"

        # If failed, dump last 30 lines of output for diagnosis
        if (-not $scriptPass) {
            $lines = $out -split "`n"
            $tail = $lines | Select-Object -Last 30
            $summary += "  --- Last 30 lines ---"
            foreach ($l in $tail) {
                $summary += "  | " + $l.TrimEnd()
            }
        }
    }
    catch {
        $summary += "  EXCEPTION: " + $_.Exception.Message
        $failScripts++
        $summary += "  Script Result: FAIL (exception)"
    }
    $summary += ""
}

$summary += "========================================"
$summary += "SUMMARY"
$summary += "Total scripts: $totalScripts"
$summary += "Passed: $passScripts"
$summary += "Failed: $failScripts"
$summary += "Total PASS assertions: $totalPass"
$summary += "Total FAIL assertions: $totalFail"
$summary += "Finished: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
$summary += ""

# Write to file
[System.IO.File]::WriteAllLines($outPath, $summary)
Write-Host "Runner complete. Results at: $outPath"
Write-Host "Passed: $passScripts / $totalScripts"
