# Read-only DashboardService smoke (32-bit, x86 SQLite native)
$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'
$lines = New-Object 'System.Collections.Generic.List[string]'
try {
    $env:MEMBERMANAGER_ROOT = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'  # smoke must exercise the SAME db the exe does (see AppPaths)
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Models.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Common.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.DAL.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.BLL.dll'))
    $svc = New-Object MemberManager.BLL.DashboardService
    $m = $svc.GetTodayMetrics('2026-09-22', 2)
    $lines.Add(('metrics revenue={0} yest={1} recharge={2} consume={3} new={4} newMonth={5}' -f $m.Revenue, $m.RevenueYesterday, $m.Recharge, $m.Consume, $m.NewMembers, $m.NewMembersMonth))
    $t = $svc.GetTrend(7, '2026-09-22')
    $lines.Add(('trend count={0} first={1} last={2}' -f $t.Count, $t[0].BizDate, $t[$t.Count - 1].BizDate))
    $lines.Add(('birthday={0} sleep={1} pend={2} refund={3}' -f $svc.GetBirthdayToday().Count, $svc.GetSleepingMembers(60).Count, $svc.CountPendingOrders(), $svc.CountPendingRefunds()))
    $lines.Add('DASH OK')
}
catch {
    $lines.Add('DASH FAIL: ' + $_.Exception.ToString())
}
[System.IO.File]::WriteAllLines((Join-Path $PSScriptRoot '_dash_smoke.log'), $lines, [System.Text.Encoding]::UTF8)
# MUST also emit to stdout: _smoke_runner.ps1 decides PASS/FAIL from stdout
# only. This script used to write the log FILE only, so any failure here
# still showed as PASS in the summary (fixed 10-13).
$lines | Write-Output
if ($lines -contains 'DASH OK') { exit 0 } else { exit 1 }
