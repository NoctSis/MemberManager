# ==============================================================
#  Project card-times (A4) smoke (x86 host: SysWOW64 PowerShell)
#
#  A4: service_items.card_times = how many card visits one service consumes.
#  Rules under test:
#    non-card item            -> forced to 0 (never keeps a stale number)
#    card item with 0/unset   -> falls back to 1 (must never be 0)
#    card item with N         -> stored as N and read back as N
#    legacy row (NULL column) -> read back as 0 via IFNULL, no crash
#    N > 999                  -> rejected
#
#  THROWAWAY temp db. PURE ASCII (PS 5.1 reads BOM-less UTF-8 as ANSI).
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_project_cardtimes_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$fail = 0
function L([string]$m) { $log.Add($m) | Out-Null }
function Chk([string]$n, [bool]$c) { if ($c) { L ('PASS ' + $n) } else { L ('FAIL ' + $n); $script:fail++ } }

$tempDb = $null
$conn = $null
try {
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L ('Host: ' + ([System.IntPtr]::Size * 8) + '-bit')

    $tempDb = Join-Path $env:TEMP ('mm_projct_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fld = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fld.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', ([string][char]0x5E97 + [string][char]0x957F))

    # CJK literals built from code points (file must stay pure ASCII)
    $allCn  = ([string][char]0x5168 + [string][char]0x90E8)   # all categories
    $catCn  = ([string][char]0x7F8E + [string][char]0x53D1)   # hairdressing
    $unitCn = ([string][char]0x6B21)                          # visit / time unit

    $svc = New-Object MemberManager.BLL.ProjectService
    $conn = [MemberManager.DAL.Db]::OpenConnection()
    function S([string]$sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return ,$c.ExecuteScalar() }
    function CtOf([string]$nm) { return [int](S ("SELECT IFNULL(card_times,0) FROM service_items WHERE name='" + $nm + "'")) }
    function RowOf([string]$nm) {
        foreach ($r in $svc.ListItems($allCn)) { if ($r.Name -eq $nm) { return $r } }
        return $null
    }

    $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $nPlain = 'P-plain-' + $suffix      # non-card item
    $nCard3 = 'P-card3-' + $suffix      # card item, 3 visits each
    $nCard0 = 'P-card0-' + $suffix      # card item but times omitted -> 1
    $nOld   = 'P-old-' + $suffix        # legacy row, NULL card_times

    # ---- P1: non-card item ignores cardTimes (forced 0) ----
    $svc.SaveItem(0, $nPlain, $catCn, [decimal]38, $unitCn, $false, $true, 5, [decimal]0.05)
    L ('  diag P1: stored=' + (CtOf $nPlain))
    Chk 'P1-noncard-forced-zero' ((CtOf $nPlain) -eq 0)

    # ---- P2: card item stores N and reads back N ----
    $svc.SaveItem(0, $nCard3, $catCn, [decimal]38, $unitCn, $true, $true, 3, [decimal]0.05)
    $row3 = RowOf $nCard3
    L ('  diag P2: stored=' + (CtOf $nCard3) + ' rowCardTimes=' + $row3.CardTimes + ' isCard=' + $row3.IsCardItem)
    Chk 'P2-card-times-stored-3' ((CtOf $nCard3) -eq 3)
    Chk 'P2-card-times-read-back' ($row3.CardTimes -eq 3 -and $row3.IsCardItem)

    # ---- P3: card item with 0 falls back to 1 ----
    $svc.SaveItem(0, $nCard0, $catCn, [decimal]38, $unitCn, $true, $true, 0, [decimal]0.05)
    L ('  diag P3: stored=' + (CtOf $nCard0) + ' (expect 1)')
    Chk 'P3-card-zero-falls-back-to-1' ((CtOf $nCard0) -eq 1)

    # ---- P4: switching back to non-card zeroes the number ----
    $id3 = [int](S ("SELECT id FROM service_items WHERE name='" + $nCard3 + "'"))
    $svc.SaveItem($id3, $nCard3, $catCn, [decimal]38, $unitCn, $false, $true, 3, [decimal]0.05)
    L ('  diag P4: stored=' + (CtOf $nCard3) + ' (expect 0)')
    Chk 'P4-turned-off-clears-times' ((CtOf $nCard3) -eq 0)

    # ---- P5: legacy row with NULL card_times reads back 0, no crash ----
    $ins = $conn.CreateCommand()
    $ins.CommandText = "INSERT INTO service_items(name,category,price,unit,is_card_item,enabled,commission_rate)" +
        " VALUES(@n,'" + $catCn + "',38,'" + $unitCn + "',0,1,0.05)"
    $pi = $ins.CreateParameter(); $pi.ParameterName = '@n'; $pi.Value = $nOld; [void]$ins.Parameters.Add($pi)
    [void]$ins.ExecuteNonQuery()
    $rowOld = RowOf $nOld
    L ('  diag P5: legacyRowCardTimes=' + $rowOld.CardTimes + ' (expect 0, no exception)')
    Chk 'P5-legacy-null-reads-zero' ($rowOld.CardTimes -eq 0)

    # ---- P6: absurd value rejected ----
    $threw = $false
    try { $svc.SaveItem(0, 'P-big-' + $suffix, $catCn, [decimal]38, $unitCn, $true, $true, 1000, [decimal]0.05) } catch { $threw = $true }
    Chk 'P6-reject-over-999' $threw

    # ---- P7: delete = SOFT delete -- item leaves the list, row + history stay ----
    $idDel = [int](S ("SELECT id FROM service_items WHERE name='" + $nCard0 + "'"))
    $svc.DeleteItem($idDel)
    $delAt = [string](S ("SELECT IFNULL(deleted_at,'') FROM service_items WHERE id=" + $idDel))
    $delEnabled = [int](S ("SELECT enabled FROM service_items WHERE id=" + $idDel))
    L ('  diag P7: deleted_at=' + $delAt + ' enabled=' + $delEnabled)
    Chk 'P7-gone-from-list'      ($null -eq (RowOf $nCard0))
    Chk 'P7-row-kept-soft'       ([int](S ("SELECT COUNT(*) FROM service_items WHERE id=" + $idDel)) -eq 1)
    Chk 'P7-enabled-zero'        ($delEnabled -eq 0)
    Chk 'P7-deleted-at-set'      (-not [string]::IsNullOrEmpty($delAt))

    # ---- P8: deleting an already-deleted item is rejected ----
    $threw2 = $false
    try { $svc.DeleteItem($idDel) } catch { $threw2 = $true }
    Chk 'P8-double-delete-rejected' $threw2

    # ---- P9: the name is freed up after deletion (re-create with the same name works) ----
    $svc.SaveItem(0, $nCard0, $catCn, [decimal]38, $unitCn, $true, $true, 2, [decimal]0.05)
    Chk 'P9-name-reusable-after-delete' ($null -ne (RowOf $nCard0))

    $conn.Dispose()
    $conn = $null

    L ''
    if ($fail -eq 0) { L 'PROJECT-CARDTIMES SMOKE OK' } else { L ('PROJECT-CARDTIMES SMOKE FAILED: ' + $fail) }
}
catch {
    L ('PROJECT-CARDTIMES SMOKE ERROR: ' + $_.Exception.ToString())
}
finally {
    if ($null -ne $conn) { try { $conn.Dispose() } catch { } }
    if ($tempDb) {
        foreach ($ext in @('', '-wal', '-shm')) {
            $p = $tempDb + $ext
            if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
        }
    }
    [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($false)))
}
exit 0
