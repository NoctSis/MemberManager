# Read-only MemberService smoke (32-bit, x86 SQLite native)
# Verifies every SQL path in module 02 without writing anything:
#   SearchMembers (empty/kw/status/tag combos) -> Summary -> ListTags
#   -> per member: GetMember / LoadMemberFlows / LoadMemberOrders / GetBalanceCheck
# Balance identity: recharge+gift-consume-refund+adjust == balance (tolerance 0.005)
# NOTE: source must stay pure ASCII (PS 5.1 reads BOM-less UTF-8 as ANSI).
#       Chinese filter literals come from UiText constants, DB values at runtime.
$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'
$lines = New-Object 'System.Collections.Generic.List[string]'
try {
    $env:MEMBERMANAGER_ROOT = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'  # smoke must exercise the SAME db the exe does (see AppPaths)
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Models.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Common.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.DAL.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.BLL.dll'))

    $svc = New-Object MemberManager.BLL.MemberService
    $allStatus = [MemberManager.Common.UiText]::MemberFilterAllStatus

    $all = $svc.SearchMembers('', $allStatus, '', 500)
    $lines.Add('search-all: ' + $all.Count)

    if ($all.Count -gt 0) {
        $first = $all[0]
        $kw = $first.Name.Substring(0, 1)
        $byKw = $svc.SearchMembers($kw, $allStatus, '', 500)
        $lines.Add('search-kw: ' + $byKw.Count)
        $byStatus = $svc.SearchMembers('', $first.Status, '', 500)
        $lines.Add('search-status[' + $first.Status + ']: ' + $byStatus.Count)
        if ($first.Tags) {
            $tag0 = $first.Tags.Split([char]12289)[0]
            $byTag = $svc.SearchMembers('', $allStatus, $tag0, 500)
            $lines.Add('search-tag: ' + $byTag.Count)
        } else {
            $lines.Add('search-tag: skipped (no tag on first member)')
        }
        $byMiss = $svc.SearchMembers('__no_such__', $allStatus, '', 500)
        $lines.Add('search-miss: ' + $byMiss.Count)
    }

    $c = 0; $b = [decimal]0; $abn = 0
    $svc.Summary('', $allStatus, '', [ref]$c, [ref]$b, [ref]$abn)
    $lines.Add(('summary: count={0} balance={1} abnormal={2}' -f $c, $b, $abn))

    $tags = $svc.ListTags()
    $lines.Add('tags: ' + $tags.Count)

    $bad = 0
    foreach ($m in $all) {
        $g = $svc.GetMember($m.Id)
        if ($g.CardNo -ne $m.CardNo) { $bad++; $lines.Add('MISMATCH get-member id=' + $m.Id) }
        $chk = $svc.GetBalanceCheck($m.Id)
        if (-not $chk.Ok) {
            $bad++
            $lines.Add(('MISMATCH balance id={0} card={1} computed={2} actual={3}' -f $m.Id, $m.CardNo, $chk.Computed, $chk.Actual))
        }
        $fl = $svc.LoadMemberFlows($m.Id, 1000, 0)
        $od = $svc.LoadMemberOrders($m.Id, 1000, 0)
        if ($fl.Count -gt 0 -or $od.Count -gt 0) {
            $lines.Add(('member id={0} flows={1} orders={2} reconOk={3}' -f $m.Id, $fl.Count, $od.Count, $chk.Ok))
        }
    }
    $lines.Add('identity-check: members=' + $all.Count + ' mismatches=' + $bad)
    if ($bad -eq 0) { $lines.Add('MEMBER OK') } else { $lines.Add('MEMBER FAIL: identity mismatches found') }
}
catch {
    $lines.Add('MEMBER FAIL: ' + $_.Exception.ToString())
}
[System.IO.File]::WriteAllLines((Join-Path $PSScriptRoot '_member_smoke.log'), $lines, [System.Text.Encoding]::UTF8)
# MUST also emit to stdout: _smoke_runner.ps1 decides PASS/FAIL from stdout
# only. This script used to write the log FILE only, so any failure here
# still showed as PASS in the summary (fixed 10-13).
$lines | Write-Output
if ($lines -contains 'MEMBER OK') { exit 0 } else { exit 1 }
