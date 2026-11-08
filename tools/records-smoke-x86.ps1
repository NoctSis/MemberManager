# Read-only RecordService smoke (32-bit, x86 SQLite native)
# Verifies every SQL path of the "consumer records" query service WITHOUT writing:
#   Query(no filter) -> Query(date/kw/kind/status/operator) -> Operators -> Detail(each order)
# Cross-check identity: sum(rows.receivable) == totals.Receivable
# NOTE: source must stay PURE ASCII (PS 5.1 reads BOM-less UTF-8 as ANSI and breaks quotes).
#       C# BuildWhere treats null/empty as "no filter", so "" is used for the "all" case.
#       Values that must be Chinese come from the DB at runtime, never from this file.
$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'
$lines = New-Object 'System.Collections.Generic.List[string]'
$ALL = ''
# BuildWhere treats these exact CJK strings as the "all" sentinel; the real filter
# values are the member / walk-in customers, plus the "all levels" sentinel.
$MEMBER = [string]([char]0x4F1A + [char]0x5458)        # member
$WALKIN = [string]([char]0x6563 + [char]0x5BA2)        # walk-in
$ALLLVL = [string]([char]0x5168 + [char]0x90E8 + [char]0x7B49 + [char]0x7EA7)  # all levels
$NORMAL = [string]([char]0x6B63 + [char]0x5E38)                                # order status: normal
try {
    $env:MEMBERMANAGER_ROOT = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'  # smoke must exercise the SAME db the exe does (see AppPaths)
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Models.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Common.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.DAL.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.BLL.dll'))

    $svc = New-Object MemberManager.BLL.RecordService

    # 1) no filter at all
    $r0 = $svc.Query($ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $ALL, 500, 0)
    $lines.Add('query-all: rows=' + $r0.Rows.Count + ' count=' + $r0.Total.Count +
        ' receivable=' + $r0.Total.Receivable + ' balance=' + $r0.Total.Balance +
        ' cash=' + $r0.Total.Cash + ' other=' + $r0.Total.Other + ' cardCovered=' + $r0.Total.CardCovered)

    if ($r0.Rows.Count -eq 0) {
        $lines.Add('WARN: no orders in DB - make one sale in POS to exercise the filters')
    } else {
        # 2) date range = newest order's business date
        $newest = $r0.Rows[0].BizDate
        $r1 = $svc.Query($newest, $newest, $ALL, $ALL, $ALL, $ALL, $ALL, $ALL, 500, 0)
        $lines.Add('query-date: rows=' + $r1.Rows.Count + ' receivable=' + $r1.Total.Receivable)

        # 3) keyword by order no
        $ono = $r0.Rows[0].OrderNo
        $r3 = $svc.Query($ALL, $ALL, $ono, $ALL, $ALL, $ALL, $ALL, $ALL, 500, 0)
        $lines.Add('query-orderno: rows=' + $r3.Rows.Count)

        # 4) keyword by member name (skip walk-in rows whose name has no real member)
        $named = $null
        foreach ($row in $r0.Rows) { if ($row.MemberCard -ne '') { $named = $row; break } }
        if ($named -ne $null) {
            $r4 = $svc.Query($ALL, $ALL, $named.MemberName, $ALL, $ALL, $ALL, $ALL, $ALL, 500, 0)
            $lines.Add('query-member: rows=' + $r4.Rows.Count)
        } else {
            $lines.Add('query-member: skipped (no member-linked order yet)')
        }

        # 5) keyword miss
        $r5 = $svc.Query($ALL, $ALL, '__no_such_keyword__', $ALL, $ALL, $ALL, $ALL, $ALL, 500, 0)
        $lines.Add('query-miss: rows=' + $r5.Rows.Count)

        # 6) kind / status filters - values taken from the DB at runtime (never hardcoded here)
        foreach ($k in @($r0.Rows[0].Kind)) {
            $rk = $svc.Query($ALL, $ALL, $ALL, $k, $ALL, $ALL, $ALL, $ALL, 500, 0)
            $lines.Add('query-kind: rows=' + $rk.Rows.Count)
        }
        foreach ($s in @($r0.Rows[0].Status)) {
            $rs = $svc.Query($ALL, $ALL, $ALL, $ALL, $s, $ALL, $ALL, $ALL, 500, 0)
            $lines.Add('query-status: rows=' + $rs.Rows.Count)
        }

        # 7) operator filter
        $ops = $svc.Operators()
        $lines.Add('operators: ' + $ops.Count)
        foreach ($o in $ops) {
            $ro = $svc.Query($ALL, $ALL, $ALL, $ALL, $ALL, $o, $ALL, $ALL, 500, 0)
            $lines.Add('query-op: rows=' + $ro.Rows.Count)
        }

        # 7b) customer type filters (walk-in vs member)
        $walk = $svc.Query($ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $WALKIN, 500, 0)
        $mems = $svc.Query($ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $MEMBER, 500, 0)
        $lines.Add('query-walkin: rows=' + $walk.Rows.Count + '  member: rows=' + $mems.Rows.Count +
                   '  sum=' + ($walk.Rows.Count + $mems.Rows.Count) + '  expect=' + $r0.Rows.Count)

        # 7c) member level filter (pick the first non-empty Level from rows)
        $lvl = ''
        foreach ($row in $r0.Rows) { if ($row.Level -ne '') { $lvl = $row.Level; break } }
        if ($lvl -ne '') {
            $rl = $svc.Query($ALL, $ALL, $ALL, $ALL, $ALL, $ALL, $lvl, $ALL, 500, 0)
            $lines.Add('query-level: level=[' + $lvl + '] rows=' + $rl.Rows.Count)
        } else {
            $lines.Add('query-level: skipped (no member with a level set)')
        }

        # Levels() should return a non-empty list
        $lvls = $svc.Levels()
        $lines.Add('levels: ' + $lvls.Count + ' [' + ($lvls -join ',') + ']')

        # 8) identity: listed rows sum must equal the aggregate total (only when not truncated)
        # The money columns are deliberately CASH-BASIS (M4, owner 10-14): sales total only
        # counts status=NORMAL, while the row list also shows held orders (not yet paid) and
        # refunded ones (reversed). So the identity must be checked against NORMAL rows only.
        if ($r0.Rows.Count -eq $r0.Total.Count) {
            $sum = [decimal]0
            $skipped = 0
            $skippedSum = [decimal]0
            foreach ($row in $r0.Rows) {
                if ($row.Status -eq $NORMAL) { $sum += $row.Receivable }
                else { $skipped++; $skippedSum += $row.Receivable }
            }
            $diff = [Math]::Abs($sum - $r0.Total.Receivable)
            $lines.Add('identity: rowSum(normal)=' + $sum + ' total=' + $r0.Total.Receivable +
                ' diff=' + $diff + ' excluded=' + $skipped + ' rows / ' + $skippedSum)
            if ($diff -gt 0.01) { $lines.Add('IDENTITY FAIL: rowSum != aggregate total') }
        } else {
            $lines.Add('identity: skipped (result truncated by limit)')
        }

        # 9) detail for every order
        $bad = 0
        foreach ($row in $r0.Rows) {
            $d = $svc.Detail($row.Id)
            if ($d.OrderNo -ne $row.OrderNo) { $bad++; $lines.Add('MISMATCH detail id=' + $row.Id) }
            $lines.Add(('detail: id={0} items={1} pays={2} receivable={3}' -f $d.Id, $d.Items.Count, $d.Pays.Count, $d.Receivable))
        }
        $lines.Add('detail-check: orders=' + $r0.Rows.Count + ' mismatches=' + $bad)
        if ($bad -eq 0) { $lines.Add('RECORDS OK') } else { $lines.Add('RECORDS FAIL: detail mismatch') }
    }
}
catch {
    $lines.Add('RECORDS FAIL: ' + $_.Exception.ToString())
}
try { [System.IO.File]::WriteAllLines((Join-Path $PSScriptRoot '_records_smoke.log'), $lines, [System.Text.Encoding]::UTF8) } catch { }
$lines
# Was always "exit 0"; on failure the runner could only tell from stdout
# FAIL lines. Add an honest exit code (fixed 10-13).
if ($lines -contains 'RECORDS OK') { exit 0 } else { exit 1 }
