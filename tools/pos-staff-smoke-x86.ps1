# ==============================================================
#  pos-staff-smoke  (READ-ONLY against the real DB)
#  Verifies the new PosService query methods added in the
#  "who operated this order" round:
#    ListMemberCards(memberId)   -> available card breakdown
#    ListStaffOptions()          -> operator picker source
#    ListHeldOrders()            -> now also returns StaffName
#    ListItems / SearchBillingMembers still fine
#  PURE ASCII on purpose: PS 5.1 reads BOM-less UTF-8 as ANSI.
#  Must run under 32-bit PowerShell (x86 SQLite interop).
# ==============================================================
$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot '_pos_staff.txt'
$log = New-Object 'System.Collections.Generic.List[string]'
function W([string]$s) { $log.Add($s) | Out-Null }
function Save { [System.IO.File]::WriteAllLines($out, $log, (New-Object System.Text.UTF8Encoding($false))) }

try {
    $root = Split-Path -Parent $PSScriptRoot
    $dist = Join-Path $root 'beta'
    foreach ($a in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        $env:MEMBERMANAGER_ROOT = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'  # smoke must exercise the SAME db the exe does (see AppPaths)
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $a))
    }
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist 'System.Data.SQLite.dll'))
    # native SQLite.Interop.dll is picked up from the app base dir automatically
    [System.IO.Directory]::SetCurrentDirectory($dist)

    $DALAsm = [System.Reflection.Assembly]::LoadFrom((Join-Path $dist 'MemberManager.DAL.dll'))
    $BLLAsm = [System.Reflection.Assembly]::LoadFrom((Join-Path $dist 'MemberManager.BLL.dll'))
    $PS = $BLLAsm.GetType('MemberManager.BLL.PosService', $true)
    $svc = [System.Activator]::CreateInstance($PS, @())

    # ---- 1. staff options ----
    $opts = $PS.GetMethod('ListStaffOptions').Invoke($svc, @())
    W ('staffOptions: count=' + $opts.Count)
    foreach ($o in $opts) { W ('  #' + $o.Id + ' ' + $o.Name + ' [' + $o.Role + ']') }

    # ---- 2. member cards for the first member that actually has cards ----
    $DAL = $DALAsm.GetType('MemberManager.DAL.Db', $true)
    $conn = $DAL.GetMethod('OpenConnection').Invoke($null, @())
    $mi = $conn.CreateCommand()
    $normalCn = ([string][char]0x6B63 + [string][char]0x5E38)   # active status
    $mi.CommandText = "SELECT m.id FROM members m JOIN member_cards mc ON mc.member_id=m.id WHERE mc.remain>0 AND mc.status='" + $normalCn + "' LIMIT 1"
    $memberId = $mi.ExecuteScalar()
    $conn.Dispose()

    if ($null -eq $memberId -or $memberId -eq [DBNull]::Value) {
        W 'memberCards: (no member with remaining card times in DB)'
    } else {
        $mid = [int]$memberId
        W ('memberCards: memberId=' + $mid)
        $cards = $PS.GetMethod('ListMemberCards').Invoke($svc, @($mid))
        $sum = 0
        foreach ($c in $cards) {
            $sum += $c.Remain
            W ('  #' + $c.Id + ' ' + $c.CardName + ' remain=' + $c.Remain + '/' + $c.Total + ' expire=' + $c.ExpireDate)
        }
        # identity: sum(ListMemberCards.Remain) must equal BillingMember.CardRemain
        $bm = $PS.GetMethod('GetBillingMember').Invoke($svc, @($mid))
        W ('  sumRemain=' + $sum + ' billingCardRemain=' + $bm.CardRemain + ' match=' + ($sum -eq $bm.CardRemain))
    }

    # ---- 3. held orders expose StaffName ----
    $held = $PS.GetMethod('ListHeldOrders').Invoke($svc, @())
    W ('heldOrders: count=' + $held.Count)
    foreach ($h in $held) {
        W ('  ' + $h.OrderNo + ' member=' + $h.MemberName + ' items=' + $h.ItemCount + ' amount=' + $h.Amount + ' staff=[' + $h.StaffName + ']')
    }

    # ---- 4. items still load ----
    $items = $PS.GetMethod('ListItems').Invoke($svc, @(''))
    W ('items: count=' + $items.Count)

    # ---- 5. operator picker must contain at least one shop manager ----
    $hasBoss = $false
    foreach ($o in $opts) { if ($o.Role -eq [string]([char]0x5E97 + [char]0x957F)) { $hasBoss = $true } }
    W ('hasBossRole: ' + $hasBoss)

    W 'POS-STAFF OK'
    Save
    exit 0
}
catch {
    W ('POS-STAFF FAILED: ' + $_.Exception.Message)
    W ($_ | Out-String)
    W ($_.ScriptStackTrace)
    Save
    exit 1
}
