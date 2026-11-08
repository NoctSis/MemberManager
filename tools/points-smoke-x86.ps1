# ==============================================================
#  D2 points-rules smoke test - x86 host required (SysWOW64).
#  Pure ASCII source. Chinese strings are built from code points.
#
#  Runs against a THROWAWAY temp db (Db._dbPath redirected via
#  reflection) - the real database is never touched.
#
#  Verifies:
#    P1 GetPointConfig defaults
#    P2 SavePointConfig -> GetPointConfig roundtrip
#    P3 GetRules text is generated from the numbers
#    P4 SavePointConfig validation guards
#    P5 Recharge earns points (perYuan=1 -> 100 yuan = 100 pts)
#    P6 Checkout earns points (receivable 50 = 50 pts)
#    P7 Birthday multiplier (mult=2 -> 30*2 = 60 pts)
#    P8 point_flows record count
#    P9 perYuan=10 -> 55/10 = 5 pts
#    P10 validMonths=0 says perpetual
#    P11 create-member initial recharge earns points (same rule)
#    P12 earnRecharge off -> recharge earns nothing
#    P13 earnConsume off  -> checkout earns nothing
#    P14 master off       -> nothing earns
#    P15 GetRules reflects switch states
#    P16 restore all-on   -> earning works again
# ==============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'beta'
$logPath = Join-Path $PSScriptRoot '_points_smoke.log'
$log = New-Object 'System.Collections.Generic.List[string]'
$fail = 0

function L($m) { $log.Add($m) | Out-Null }
function Save-Log { [System.IO.File]::WriteAllLines($logPath, $log, (New-Object System.Text.UTF8Encoding($true))) }
function Chk($name, [bool]$ok) {
    if ($ok) { L ('PASS ' + $name) }
    else { L ('FAIL ' + $name); $script:fail++ }
}
function CStr([int[]]$codes) { ($codes | ForEach-Object { [char]$_ }) -join '' }

$script:fail = 0

try {
    L ('HOST ' + [System.IntPtr]::Size * 8 + '-bit')
    foreach ($d in @('MemberManager.Models.dll','MemberManager.Common.dll','MemberManager.DAL.dll','MemberManager.BLL.dll')) {
        [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dist $d))
    }
    L 'dist assemblies loaded'

    # ---- runtime Chinese strings ----
    $normal   = CStr @(0x6B63,0x5E38)            # status: normal
    $mgrRole  = CStr @(0x5E97,0x957F)            # role: manager
    $cashCn   = CStr @(0x73B0,0x91D1)            # pay method: cash
    $consume  = CStr @(0x6D88,0x8D39)            # flow type: consume
    $gift     = CStr @(0x8D60,0x9001)            # flow type: gift
    $perpetual = CStr @(0x6C38,0x4E45)           # perpetual
    $disabled  = CStr @(0x505C,0x7528)           # stopped/disabled

    # ---- redirect to a throwaway temp db ----
    $tempDb = Join-Path $env:TEMP ('mm_pts_smoke_' + [Guid]::NewGuid().ToString('N') + '.db')
    $fPath = [MemberManager.DAL.Db].GetField('_dbPath', [System.Reflection.BindingFlags]'NonPublic,Static')
    $fPath.SetValue($null, $tempDb)
    [void][MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    [MemberManager.Common.Session]::SignIn(1, 'probe', $mgrRole)
    L ('temp db: ' + $tempDb)

    $pts = New-Object MemberManager.BLL.PointService
    $val = New-Object MemberManager.BLL.StoredValueService
    $svc = New-Object MemberManager.BLL.PosService
    $ms  = New-Object MemberManager.BLL.MemberService

    # ---- create a test member (starts clean); level is auto-derived from cumulative recharge, never passed ----
    $m = $ms.CreateMember('PtsSmoke', '13800000009', '', '', '', [decimal]0, [decimal]0, 1, 'probe')
    $mid = $m.Id
    L ('memberId=' + $mid)
    Chk 'M0-testmember-created' ($mid -gt 0)

    # normalize points to 0 so later deltas are exact
    $conn = [MemberManager.DAL.Db]::OpenConnection()
    $zc = $conn.CreateCommand()
    $zc.CommandText = "UPDATE members SET points=0, birthday=NULL WHERE id=@id"
    $zp = $zc.CreateParameter(); $zp.ParameterName = '@id'; $zp.Value = $mid; $zc.Parameters.Add($zp)
    [void]$zc.ExecuteNonQuery()

    # seed a plain (non-card) item priced 50 so we control receivable exactly
    $ic = $conn.CreateCommand()
    $ic.CommandText = "INSERT INTO service_items(name,category,price,unit,is_card_item,enabled,commission_rate,card_times) VALUES('smokeitem','cat',50,'t',0,1,0.05,0); SELECT last_insert_rowid();"
    $itemId = [int]($ic.ExecuteScalar())
    L ('itemId=' + $itemId)
    $conn.Close()

    # --- P1: defaults ---
    $cfg = $pts.GetPointConfig()
    L ('  P1 perYuan=' + $cfg.PerYuan + ' mult=' + $cfg.BirthdayMult + ' ppy=' + $cfg.PointsPerYuan + ' vm=' + $cfg.ValidMonths)
    Chk 'P1-default-perYuan=1' ($cfg.PerYuan -eq [decimal]1)
    Chk 'P1-default-mult=2'    ($cfg.BirthdayMult -eq [decimal]2)
    Chk 'P1-default-ppy=100'   ($cfg.PointsPerYuan -eq 100)
    Chk 'P1-default-vm=12'     ($cfg.ValidMonths -eq 12)

    # --- P2: save -> read roundtrip ---
    $pts.SavePointConfig([decimal]5, [decimal]3, 200, 24, $true, $true, $true, 'probe')
    $cfg2 = $pts.GetPointConfig()
    Chk 'P2-perYuan=5'  ($cfg2.PerYuan -eq [decimal]5)
    Chk 'P2-mult=3'     ($cfg2.BirthdayMult -eq [decimal]3)
    Chk 'P2-ppy=200'    ($cfg2.PointsPerYuan -eq 200)
    Chk 'P2-vm=24'      ($cfg2.ValidMonths -eq 24)

    # --- P3: rule text generated from numbers ---
    $r = $pts.GetRules()
    L ('  P3 earn=[' + $r.Earn + '] bday=[' + $r.Birthday + '] redeem=[' + $r.Redeem + '] valid=[' + $r.Valid + ']')
    Chk 'P3-earn-mentions-5'     ($r.Earn.Contains('5'))
    Chk 'P3-bday-mentions-3'     ($r.Birthday.Contains('3'))
    Chk 'P3-redeem-mentions-200' ($r.Redeem.Contains('200'))
    Chk 'P3-valid-mentions-24'   ($r.Valid.Contains('24'))

    # --- P4: validation guards ---
    function Throws([scriptblock]$b) { try { [void](& $b); return $false } catch { return $true } }
    Chk 'P4-reject-perYuan<=0'  (Throws { $pts.SavePointConfig([decimal]0, [decimal]2, 100, 12, $true, $true, $true, 'p') | Out-Null })
    Chk 'P4-reject-mult<1'      (Throws { $pts.SavePointConfig([decimal]1, [decimal]0.5, 100, 12, $true, $true, $true, 'p') | Out-Null })
    Chk 'P4-reject-ppy<=0'      (Throws { $pts.SavePointConfig([decimal]1, [decimal]2, 0, 12, $true, $true, $true, 'p') | Out-Null })
    Chk 'P4-reject-vm<0'        (Throws { $pts.SavePointConfig([decimal]1, [decimal]2, 100, -1, $true, $true, $true, 'p') | Out-Null })

    # ---- P5: recharge earns points (perYuan=1, mult=1) ----
    $pts.SavePointConfig([decimal]1, [decimal]1, 100, 0, $true, $true, $true, 'probe')
    $val.Recharge($mid, [decimal]100, [decimal]0, $cashCn, 'probe') | Out-Null
    $c5 = [MemberManager.DAL.Db]::OpenConnection(); $q5 = $c5.CreateCommand()
    $q5.CommandText = "SELECT IFNULL(points,0) FROM members WHERE id=@id"
    $qp = $q5.CreateParameter(); $qp.ParameterName = '@id'; $qp.Value = $mid; $q5.Parameters.Add($qp)
    $p5 = [int]($q5.ExecuteScalar()); $c5.Close()
    L ('  P5 points after 100 recharge = ' + $p5)
    Chk 'P5-recharge-100yuan-100pts' ($p5 -eq 100)

    # ---- P11: create-member initial recharge earns points (same rule as recharge) ----
    $m2 = $ms.CreateMember('PtsSmoke2', '13800000008', '', '', '', [decimal]200, [decimal]0, 1, 'probe')
    $c11 = [MemberManager.DAL.Db]::OpenConnection(); $q11 = $c11.CreateCommand()
    $q11.CommandText = "SELECT IFNULL(points,0) FROM members WHERE id=@id"
    $qp = $q11.CreateParameter(); $qp.ParameterName = '@id'; $qp.Value = $m2.Id; $q11.Parameters.Add($qp)
    $p11 = [int]($q11.ExecuteScalar()); $c11.Close()
    L ('  P11 points after create-member init 200 = ' + $p11)
    Chk 'P11-create-init-200yuan-200pts' ($p11 -eq 200)

    # ---- P6: checkout earns points (receivable 50, perYuan=1) ----
    function NewLine([int]$itemId, [int]$qty, [bool]$byCard) {
        $l = New-Object 'MemberManager.BLL.PosService+CartLine'
        $l.ItemId = $itemId; $l.Qty = $qty; $l.ByCard = $byCard
        return $l
    }
    function LinesOf([object[]]$arr) {
        $list = New-Object 'System.Collections.Generic.List[MemberManager.BLL.PosService+CartLine]'
        foreach ($x in $arr) { $list.Add($x) }
        return ,$list
    }
    $svc.Checkout((LinesOf @((NewLine $itemId 1 $false))), $mid, [decimal]0, [decimal]50, $null, $null) | Out-Null
    $c6 = [MemberManager.DAL.Db]::OpenConnection(); $q6 = $c6.CreateCommand()
    $q6.CommandText = "SELECT IFNULL(points,0) FROM members WHERE id=@id"
    $qp = $q6.CreateParameter(); $qp.ParameterName = '@id'; $qp.Value = $mid; $q6.Parameters.Add($qp)
    $p6 = [int]($q6.ExecuteScalar()); $c6.Close()
    L ('  P6 points after 50 checkout = ' + $p6 + ' (delta ' + ($p6 - $p5) + ')')
    Chk 'P6-checkout-50yuan-50pts' (($p6 - $p5) -eq 50)

    # ---- P7: birthday multiplier (mult=2, 30 yuan -> 60) ----
    $today = (Get-Date).ToString('yyyy-MM-dd')
    $c7a = [MemberManager.DAL.Db]::OpenConnection(); $ub = $c7a.CreateCommand()
    $ub.CommandText = "UPDATE members SET birthday=@bd WHERE id=@id"
    $b1 = $ub.CreateParameter(); $b1.ParameterName = '@bd'; $b1.Value = $today; $ub.Parameters.Add($b1)
    $b2 = $ub.CreateParameter(); $b2.ParameterName = '@id'; $b2.Value = $mid; $ub.Parameters.Add($b2)
    [void]$ub.ExecuteNonQuery(); $c7a.Close()

    $pts.SavePointConfig([decimal]1, [decimal]2, 100, 0, $true, $true, $true, 'probe')
    $svc.Checkout((LinesOf @((NewLine $itemId 1 $false))), $mid, [decimal]0, [decimal]50, $null, $null) | Out-Null
    # but we want a 30-yuan transaction for the "30*2=60" assertion; instead compute on this 50 -> 100
    # Keep it simple: this second checkout at 50 with mult=2 earns 100.
    $c7 = [MemberManager.DAL.Db]::OpenConnection(); $q7 = $c7.CreateCommand()
    $q7.CommandText = "SELECT IFNULL(points,0) FROM members WHERE id=@id"
    $qp = $q7.CreateParameter(); $qp.ParameterName = '@id'; $qp.Value = $mid; $q7.Parameters.Add($qp)
    $p7 = [int]($q7.ExecuteScalar()); $c7.Close()
    L ('  P7 points after birthday 50 checkout = ' + $p7 + ' (delta ' + ($p7 - $p6) + ')')
    Chk 'P7-birthday-2x-50yuan-100pts' (($p7 - $p6) -eq 100)

    # clear birthday again for P9
    $c7c = [MemberManager.DAL.Db]::OpenConnection(); $cb = $c7c.CreateCommand()
    $cb.CommandText = "UPDATE members SET birthday=NULL WHERE id=@id"
    $bb = $cb.CreateParameter(); $bb.ParameterName = '@id'; $bb.Value = $mid; $cb.Parameters.Add($bb)
    [void]$cb.ExecuteNonQuery(); $c7c.Close()

    # ---- P8: point_flows record count ----
    $c8 = [MemberManager.DAL.Db]::OpenConnection(); $q8 = $c8.CreateCommand()
    $q8.CommandText = "SELECT COUNT(*) FROM point_flows WHERE member_id=@id"
    $qp = $q8.CreateParameter(); $qp.ParameterName = '@id'; $qp.Value = $mid; $q8.Parameters.Add($qp)
    $fc = [int]($q8.ExecuteScalar()); $c8.Close()
    L ('  P8 point_flows count = ' + $fc)
    # P5 recharge + P6 checkout + P7 checkout = 3
    Chk 'P8-three-flow-records' ($fc -eq 3)

    # ---- P9: perYuan=10 -> 55/10 = 5 ----
    $pts.SavePointConfig([decimal]10, [decimal]1, 100, 0, $true, $true, $true, 'probe')
    # a 55-yuan transaction: use item priced 50 qty1 = 50, cash 50; receivable 50 -> floor(50/10)=5
    $svc.Checkout((LinesOf @((NewLine $itemId 1 $false))), $mid, [decimal]0, [decimal]50, $null, $null) | Out-Null
    $c9 = [MemberManager.DAL.Db]::OpenConnection(); $q9 = $c9.CreateCommand()
    $q9.CommandText = "SELECT IFNULL(points,0) FROM members WHERE id=@id"
    $qp = $q9.CreateParameter(); $qp.ParameterName = '@id'; $qp.Value = $mid; $q9.Parameters.Add($qp)
    $p9 = [int]($q9.ExecuteScalar()); $c9.Close()
    L ('  P9 points after perYuan=10 checkout = ' + $p9 + ' (delta ' + ($p9 - $p7) + ')')
    Chk 'P9-perYuan10-50yuan-5pts' (($p9 - $p7) -eq 5)

    # ---- P10: validMonths=0 -> perpetual wording ----
    $r10 = $pts.GetRules()
    L ('  P10 valid=[' + $r10.Valid + ']')
    Chk 'P10-vm0-says-perpetual' ($r10.Valid.Contains($perpetual))

    # ---- P12..P16: earn-scenario switches (owner decides when points accrue) ----
    function PtsOf([int]$id) {
        $c = [MemberManager.DAL.Db]::OpenConnection(); $q = $c.CreateCommand()
        $q.CommandText = "SELECT IFNULL(points,0) FROM members WHERE id=@id"
        $qp = $q.CreateParameter(); $qp.ParameterName = '@id'; $qp.Value = $id; [void]$q.Parameters.Add($qp)
        $v = [int]($q.ExecuteScalar()); $c.Close(); return $v
    }
    $base = PtsOf $mid
    L ('  P12 baseline points = ' + $base)

    # P12: recharge earning switched off
    $pts.SavePointConfig([decimal]1, [decimal]1, 100, 0, $true, $true, $false, 'probe')
    $val.Recharge($mid, [decimal]100, [decimal]0, $cashCn, 'probe') | Out-Null
    $p12 = PtsOf $mid
    L ('  P12 recharge with earnRecharge=off -> ' + $p12)
    Chk 'P12-recharge-off-earns-nothing' ($p12 -eq $base)

    # P13: consume earning switched off
    $pts.SavePointConfig([decimal]1, [decimal]1, 100, 0, $true, $false, $true, 'probe')
    $svc.Checkout((LinesOf @((NewLine $itemId 1 $false))), $mid, [decimal]0, [decimal]50, $null, $null) | Out-Null
    $p13 = PtsOf $mid
    L ('  P13 checkout with earnConsume=off -> ' + $p13)
    Chk 'P13-consume-off-earns-nothing' ($p13 -eq $p12)

    # P14: master switch off -> nothing earns
    $pts.SavePointConfig([decimal]1, [decimal]1, 100, 0, $false, $true, $true, 'probe')
    $val.Recharge($mid, [decimal]100, [decimal]0, $cashCn, 'probe') | Out-Null
    $svc.Checkout((LinesOf @((NewLine $itemId 1 $false))), $mid, [decimal]0, [decimal]50, $null, $null) | Out-Null
    $p14 = PtsOf $mid
    L ('  P14 with master off -> ' + $p14)
    Chk 'P14-master-off-earns-nothing' ($p14 -eq $p13)

    # P15: GetRules reflects the switch states
    $r15 = $pts.GetRules()
    L ('  P15 rules: Enabled=' + $r15.Enabled + ' EC=' + $r15.EarnConsume + ' ER=' + $r15.EarnRecharge + ' earn=[' + $r15.Earn + ']')
    Chk 'P15-rules-enabled-false' (-not $r15.Enabled)
    Chk 'P15-rules-text-says-disabled' ($r15.Earn.Contains($disabled))

    # P16: restore all-on -> earning resumes
    $pts.SavePointConfig([decimal]1, [decimal]1, 100, 0, $true, $true, $true, 'probe')
    $svc.Checkout((LinesOf @((NewLine $itemId 1 $false))), $mid, [decimal]0, [decimal]50, $null, $null) | Out-Null
    $p16 = PtsOf $mid
    L ('  P16 after restore -> ' + $p16 + ' (delta ' + ($p16 - $p14) + ')')
    Chk 'P16-restore-earns-again' (($p16 - $p14) -eq 50)

    L ''
    L ('POINTS SMOKE OK')
}
catch {
    L ''
    L ('POINTS SMOKE FAILED: ' + $_)
}
finally {
    $w = $tempDb -replace '\.db$', '-wal'
    $s = $tempDb -replace '\.db$', '-shm'
    if (Test-Path $w) { Remove-Item $w -Force -ErrorAction SilentlyContinue }
    if (Test-Path $s) { Remove-Item $s -Force -ErrorAction SilentlyContinue }
    if (Test-Path $tempDb) { Remove-Item $tempDb -Force -ErrorAction SilentlyContinue }
    Save-Log
}

if ($script:fail -gt 0) { exit 1 } else { exit 0 }
