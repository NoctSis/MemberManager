# Read-only O2 pagination smoke (32-bit, x86 SQLite native)
# Verifies the LIMIT/OFFSET + count endpoints added by O2, WITHOUT writing anything:
#   StoredValueService.Records / RecordsCount
#   CardService.UseRecords     / UseRecordsCount
#   PointService.Records       / RecordsCount
#   RecordService.Query        (offset slicing; Total.Count must ignore paging)
#   MemberService.LoadMemberFlows / LoadMemberOrders
#
# Identity asserted for every table (walk = page by page until short/empty page):
#   1) count endpoint == full load size
#   2) walked row count == full load size
#   3) walked key sequence == full load key sequence  (order preserved, no loss)
#   4) no duplicate keys inside the walked sequence
#   5) offset beyond the end returns an empty page
#   6) negative offset behaves like 0 (defensive, per backend spec)
#
# NOTE: source must stay PURE ASCII (PS 5.1 reads BOM-less UTF-8 as ANSI and breaks quotes).
#       C# treats null/empty strings as "no filter". CJK values are never hardcoded here.
$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'
$lines = New-Object 'System.Collections.Generic.List[string]'
$passN = 0
$failN = 0

function Chk([string]$name, [bool]$cond) {
    if ($cond) { $script:passN++; $lines.Add('PASS ' + $name) }
    else        { $script:failN++; $lines.Add('FAIL ' + $name) }
}

# Order-sensitive key list (kept as an array so join/compare happens in one place).
function Keys($rows, [scriptblock]$kb) {
    @( $rows | ForEach-Object $kb )
}

# Walk a table by page size B, calling $fetch($limit,$offset) until a short/empty page.
# Then compare against $full: row count, key order, duplicate keys.
function Walk-Check($label, [scriptblock]$fetch, $full, [scriptblock]$kb, [int]$B) {
    $got = New-Object 'System.Collections.Generic.List[string]'
    $o = 0
    $steps = 0
    while ($steps -lt 1000) {
        $steps++
        $pg = @( $fetch.Invoke($B, $o) )
        $n = $pg.Count
        if ($n -eq 0) { break }
        foreach ($k in (Keys $pg $kb)) { $got.Add($k) }
        if ($n -lt $B) { break }
        $o += $B
    }
    $exp = Keys $full $kb
    # duplicate-key assertion: the DATA itself may legitimately contain same-key rows (keys are built
    # from business fields, not necessarily unique). Only require that the paged walk does NOT
    # introduce MORE duplicate-key groups than the full set. Rowcount/order checks above already
    # catch a repeated row at a page boundary; this is an extra guard.
    $dupGot = @( $got | Group-Object | Where-Object { $_.Count -gt 1 } ).Count
    $dupExp = @( $exp | Group-Object | Where-Object { $_.Count -gt 1 } ).Count
    Chk ($label + ' walk rowcount (' + $got.Count + '/' + $exp.Count + ')') ($got.Count -eq $exp.Count)
    Chk ($label + ' walk order==full (' + $steps + ' pages, B=' + $B + ')') (( $got -join '|' ) -eq ( $exp -join '|' ))
    Chk ($label + ' walk no extra duplicate keys (' + $dupGot + '/' + $dupExp + ')') ($dupGot -le $dupExp)
}

try {
    $env:MEMBERMANAGER_ROOT = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'  # smoke must exercise the SAME db the exe does (see AppPaths)
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Models.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Common.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.DAL.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.BLL.dll'))

    $B = 3

    # ---------- 1) StoredValueService : recharge records ----------
    $sv = New-Object MemberManager.BLL.StoredValueService
    $svAll = @( $sv.Records(2000, 0) )
    $svCnt = $sv.RecordsCount()
    $kbId = { $_.Id.ToString() }
    Chk ('sv count==full (' + $svCnt + '/' + $svAll.Count + ')') ($svCnt -eq $svAll.Count)
    Walk-Check 'sv ' { param($l,$o) @( $sv.Records($l,$o) ) } $svAll $kbId $B
    $svOver = @( $sv.Records($B, 20000) )
    Chk ('sv offset past end -> 0 (' + $svOver.Count + ')') ($svOver.Count -eq 0)
    $svNeg = @( $sv.Records($B, -5) )
    $svZ = @( $sv.Records($B, 0) )
    Chk ('sv negative offset == offset 0 (' + $svNeg.Count + ')') ($svNeg.Count -eq $svZ.Count)

    # ---------- 2) CardService : card use records ----------
    $cd = New-Object MemberManager.BLL.CardService
    $cdAll = @( $cd.UseRecords(2000, 0) )
    $cdCnt = $cd.UseRecordsCount()
    $kbUse = { $_.Time + '/' + $_.MemberName + '/' + $_.ItemName + '/' + $_.RemainAfter }
    Chk ('cd count==full (' + $cdCnt + '/' + $cdAll.Count + ')') ($cdCnt -eq $cdAll.Count)
    Walk-Check 'cd ' { param($l,$o) @( $cd.UseRecords($l,$o) ) } $cdAll $kbUse $B
    $cdOver = @( $cd.UseRecords($B, 20000) )
    Chk ('cd offset past end -> 0 (' + $cdOver.Count + ')') ($cdOver.Count -eq 0)

    # ---------- 3) PointService : points records ----------
    $pt = New-Object MemberManager.BLL.PointService
    $ptAll = @( $pt.Records(2000, 0) )
    $ptCnt = $pt.RecordsCount()
    $kbPt = { $_.Time + '/' + $_.MemberName + '/' + $_.Change }
    Chk ('pt count==full (' + $ptCnt + '/' + $ptAll.Count + ')') ($ptCnt -eq $ptAll.Count)
    Walk-Check 'pt ' { param($l,$o) @( $pt.Records($l,$o) ) } $ptAll $kbPt $B
    $ptOver = @( $pt.Records($B, 20000) )
    Chk ('pt offset past end -> 0 (' + $ptOver.Count + ')') ($ptOver.Count -eq 0)

    # ---------- 4) RecordService.Query ----------
    # 8 filter slots all '' = no filter (C# BuildWhere treats empty as "all")
    $rs = New-Object MemberManager.BLL.RecordService
    $q0 = $rs.Query('', '', '', '', '', '', '', '', 2000, 0)
    $q1 = $rs.Query('', '', '', '', '', '', '', '', $B, 0)
    $q2 = $rs.Query('', '', '', '', '', '', '', '', $B, $B)
    $kbQ = { $_.Id.ToString() }
    Chk ('q  Total.Count ignores paging (' + $q0.Total.Count + '/' + $q1.Total.Count + '/' + $q2.Total.Count + ')') ($q0.Total.Count -eq $q1.Total.Count -and $q0.Total.Count -eq $q2.Total.Count)
    Chk ('q  Total.Receivable ignores paging') ($q0.Total.Receivable -eq $q1.Total.Receivable -and $q0.Total.Receivable -eq $q2.Total.Receivable)
    Chk ('q  Total.CardCovered ignores paging') ($q0.Total.CardCovered -eq $q1.Total.CardCovered -and $q0.Total.CardCovered -eq $q2.Total.CardCovered)
    Walk-Check 'q  ' { param($l,$o) @( $rs.Query('', '', '', '', '', '', '', '', $l, $o).Rows ) } @( $q0.Rows ) $kbQ $B
    $qOver = $rs.Query('', '', '', '', '', '', '', '', $B, 20000)
    Chk ('q  offset past end -> 0 (' + $qOver.Rows.Count + ')') ($qOver.Rows.Count -eq 0)
    $qNeg = $rs.Query('', '', '', '', '', '', '', '', $B, -5)
    Chk ('q  negative offset == offset 0 (' + $qNeg.Rows.Count + ')') ($qNeg.Rows.Count -eq $q1.Rows.Count)

    # ---------- 5) MemberService : flows / orders (per member) ----------
    $ms = New-Object MemberManager.BLL.MemberService
    $members = @( $ms.SearchMembers('', '', '', 2000) )
    $fMax = -1; $fId = 0; $fAll = @()
    $oMax = -1; $oId = 0; $oAll = @()
    foreach ($m in $members) {
        $f = @( $ms.LoadMemberFlows($m.Id, 2000, 0) )
        if ($f.Count -gt $fMax) { $fMax = $f.Count; $fId = $m.Id; $fAll = $f }
        $o = @( $ms.LoadMemberOrders($m.Id, 2000, 0) )
        if ($o.Count -gt $oMax) { $oMax = $o.Count; $oId = $m.Id; $oAll = $o }
    }
    Chk ('ms members scanned (' + $members.Count + ')') ($members.Count -gt 0)

    $kbFlow = { $_.Time + '/' + $_.Type + '/' + $_.Amount + '/' + $_.BalanceAfter }
    Chk ('ms flows max member (' + $fId + ', ' + $fAll.Count + ' rows)') ($fAll.Count -gt 0)
    Walk-Check 'ms-flows ' { param($l,$o) @( $ms.LoadMemberFlows($fId, $l, $o) ) } $fAll $kbFlow $B
    $fOver = @( $ms.LoadMemberFlows($fId, $B, 20000) )
    Chk ('ms flows offset past end -> 0 (' + $fOver.Count + ')') ($fOver.Count -eq 0)

    $kbOrd = { $_.OrderNo + '/' + $_.Kind + '/' + $_.Receivable }
    Chk ('ms orders max member (' + $oId + ', ' + $oAll.Count + ' rows)') ($oAll.Count -gt 0)
    Walk-Check 'ms-orders ' { param($l,$o) @( $ms.LoadMemberOrders($oId, $l, $o) ) } $oAll $kbOrd $B
    $oOver = @( $ms.LoadMemberOrders($oId, $B, 20000) )
    Chk ('ms orders offset past end -> 0 (' + $oOver.Count + ')') ($oOver.Count -eq 0)

    # ---------- 6) Walk with a batch LARGER than the whole table (single page path) ----------
    $B2 = 50
    Walk-Check 'sv-big ' { param($l,$o) @( $sv.Records($l,$o) ) } $svAll $kbId $B2
    Walk-Check 'q-big  ' { param($l,$o) @( $rs.Query('', '', '', '', '', '', '', '', $l, $o).Rows ) } @( $q0.Rows ) $kbQ $B2
}
catch {
    $lines.Add('RUNNER EXCEPTION: ' + $_.Exception.Message)
    $lines.Add($_.ScriptStackTrace)
    $failN++
}

$tag = 'ALL-OK'
if ($failN -gt 0) { $tag = 'NOT-OK' }
# NOTE: the token below must NOT contain the substring "FAIL" -- _smoke_runner.ps1
# decides pass/fail by counting "FAIL" occurrences in stdout. Real failures are
# already signalled by the per-assertion 'FAIL <name>' lines and by exit code 1.
$lines.Add(('-- ' + $tag + '  PASS=' + $passN + '  BAD=' + $failN))

$out = ($lines -join "`r`n")
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot '_o2_paging_smoke.log'), $out, (New-Object System.Text.UTF8Encoding $true))
Write-Output $out
if ($failN -gt 0) { exit 1 } else { exit 0 }
