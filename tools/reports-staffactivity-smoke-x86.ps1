# Reports StaffActivity smoke test
$ErrorActionPreference = "Continue"
$log = "$PSScriptRoot\_reports_staffactivity_smoke.log"
Set-Content -Path $log -Value "start" -Encoding UTF8
function Say([string]$m) { try { Add-Content -Path $log -Value $m } catch {} }

$pass = 0; $fail = 0
function Check([string]$name, [bool]$cond, [string]$detail) {
    if ($cond) { $script:pass++; Say ("PASS: " + $name + "  " + $detail) }
    else       { $script:fail++; Say ("FAIL: " + $name + "  " + $detail) }
}

$asDal = $null; $asBll = $null; $asCom = $null
$db = $null; $dbi = $null; $rs = $null; $ap = $null
try {
    $asDal = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.DAL.dll")
    $asBll = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.BLL.dll")
    $asCom = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.Common.dll")
    $db    = $asDal.GetType("MemberManager.DAL.Db")
    $dbi   = $asDal.GetType("MemberManager.DAL.DatabaseInitializer")
    $rs    = $asBll.GetType("MemberManager.BLL.ReportService")
    $ap    = $asCom.GetType("MemberManager.Common.AppPaths")
    Say "loaded"
} catch {
    Say ("load error: " + $_.Exception.Message)
    Check "dll load" $false $_.Exception.Message
    Say ("RESULT: pass=" + $pass + " fail=" + $fail)
    exit 0
}

$tmp = Join-Path $env:TEMP ("rsact-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$dbPath = Join-Path $tmp "test.db"
Say ("tmp=" + $tmp)

try {
    $rootF = $ap.GetField("_rootDir", [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static)
    if ($rootF) { $rootF.SetValue($null, $tmp); Say "pinned rootDir" }
    $dbPathF = $db.GetField("_dbPath", [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static)
    if ($dbPathF) { $dbPathF.SetValue($null, $dbPath); Say "pinned dbPath" }
} catch { Say ("pin error: " + $_.Exception.Message) }

try {
    $dbi.InvokeMember("EnsureDatabase", 'InvokeMethod', $null, $null, @())
    Say "EnsureDatabase OK"
} catch { Say ("EnsureDatabase error: " + $_.Exception.Message) }

# Chinese values via char codes
$ch_dianzhang = [string][char]0x5E97 + [char]0x957F
$ch_shouyinyuan = [string][char]0x6536 + [char]0x94F6 + [char]0x5458
$ch_jishishi = [string][char]0x6280 + [char]0x5E08
$ch_xiaofei = [string][char]0x6D88 + [char]0x8D39
$ch_chongzhi = [string][char]0x5145 + [char]0x503C
$ch_shouka = [string][char]0x552E + [char]0x5361
$ch_zhengchang = [string][char]0x6B63 + [char]0x5E38
$ch_yihuidan = [string][char]0x5DF2 + [char]0x9000 + [char]0x6B3E
$ch_puka = [string][char]0x666E + [char]0x5361

$con = New-Object System.Data.SQLite.SQLiteConnection("Data Source=" + $dbPath)
$con.Open()
function Exec([string]$sql) {
    $c = $con.CreateCommand(); $c.CommandText = $sql
    [void]$c.ExecuteNonQuery()
}

$biz = "2026-09-26"
$testDate = $biz

try {
    Exec ("INSERT INTO staff(id,name,role,pwd_hash,enabled) VALUES(101,'Zhang Store','" + $ch_dianzhang + "','x',1)")
    Exec ("INSERT INTO staff(id,name,role,pwd_hash,enabled) VALUES(102,'Liu Cashier','" + $ch_shouyinyuan + "','x',1)")
    Exec ("INSERT INTO staff(id,name,role,pwd_hash,enabled) VALUES(103,'Wang Tech','" + $ch_jishishi + "','x',1)")
    Exec ("INSERT INTO members(id,card_no,name,level,balance,status) VALUES(201,'C001','Member Zhang','" + $ch_puka + "',0,'" + $ch_zhengchang + "')")
    Exec ("INSERT INTO orders(id,order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) VALUES(5001,'S" + $biz + "-01','" + $biz + "',201,101,100,0,100,'" + $ch_xiaofei + "','" + $ch_zhengchang + "')")
    Exec ("INSERT INTO order_items(order_id,item_name,price,qty) VALUES(5001,'Massage',50,2)")
    Exec ("INSERT INTO orders(id,order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) VALUES(5002,'S" + $biz + "-02','" + $biz + "',201,103,300,0,300,'" + $ch_xiaofei + "','" + $ch_zhengchang + "')")
    Exec ("INSERT INTO order_items(order_id,item_name,price,qty) VALUES(5002,'Spa',100,3)")
    Exec ("INSERT INTO orders(id,order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) VALUES(5003,'C" + $biz + "-01','" + $biz + "',201,102,2000,0,2000,'" + $ch_chongzhi + "','" + $ch_zhengchang + "')")
    Exec ("INSERT INTO member_flows(member_id,biz_date,type,amount,balance_after,order_id,operator) VALUES(201,'" + $biz + "','" + $ch_chongzhi + "',2000,2000,5003,'Liu Cashier')")
    Exec ("INSERT INTO orders(id,order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) VALUES(5004,'K" + $biz + "-01','" + $biz + "',201,102,500,0,500,'" + $ch_shouka + "','" + $ch_zhengchang + "')")
    Exec ("INSERT INTO orders(id,order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) VALUES(5005,'S" + $biz + "-05','" + $biz + "',201,101,100,0,100,'" + $ch_xiaofei + "','" + $ch_yihuidan + "')")
    Exec ("INSERT INTO orders(id,order_no,biz_date,member_id,staff_id,subtotal,card_covered,receivable,kind,status) VALUES(5006,'S20260928-01','2026-09-28',201,101,80,0,80,'" + $ch_xiaofei + "','" + $ch_zhengchang + "')")
    Exec ("INSERT INTO order_items(order_id,item_name,price,qty) VALUES(5006,'Wash',80,1)")
    Say "inserts done"
} catch { Say ("insert error: " + $_.Exception.Message) }
$con.Close()

$instance = [Activator]::CreateInstance($rs)
$method = $rs.GetMethod("StaffActivity")
$rows = $null
try {
    $rows = $method.Invoke($instance, @($testDate))
    Say ("returned " + $rows.Count + " rows")
} catch { Say ("invoke error: " + $_.Exception.Message) }

if ($rows -eq $null) {
    Check "StaffActivity returned rows" $false "rows is null"
} else {
    $map = @{}
    foreach ($r in $rows) { $map[[string]$r.Name] = $r }
    Say ("names: " + ($map.Keys -join ', '))

    if ($map.ContainsKey("Zhang Store")) {
        $r = $map["Zhang Store"]
        Check "A order_count=1" ($r.OrdersCount -eq 1) ("got " + $r.OrdersCount)
        Check "A items_count=2" ($r.ItemsCount -eq 2) ("got " + $r.ItemsCount)
        Check "A consume_amount=100" ([decimal]$r.ConsumeAmount -eq 100) ("got " + $r.ConsumeAmount)
        Check "A recharge_count=0" ($r.RechargeCount -eq 0) ("got " + $r.RechargeCount)
        Check "A recharge_amount=0" ([decimal]$r.RechargeAmount -eq 0) ("got " + $r.RechargeAmount)
        Check "A sellcard_count=0" ($r.SellCardCount -eq 0) ("got " + $r.SellCardCount)
        Check "A sellcard_amount=0" ([decimal]$r.SellCardAmount -eq 0) ("got " + $r.SellCardAmount)
        Check "A total=100" ([decimal]$r.TotalAmount -eq 100) ("got " + $r.TotalAmount)
        Check "A role=dianzhang" ($r.Role -eq $ch_dianzhang) ("got " + $r.Role)
    } else { Check "A row present" $false "Zhang Store not in map" }

    if ($map.ContainsKey("Liu Cashier")) {
        $r = $map["Liu Cashier"]
        Check "B order_count=2" ($r.OrdersCount -eq 2) ("got " + $r.OrdersCount)
        Check "B items_count=0" ($r.ItemsCount -eq 0) ("got " + $r.ItemsCount)
        Check "B consume_amount=0" ([decimal]$r.ConsumeAmount -eq 0) ("got " + $r.ConsumeAmount)
        Check "B recharge_count=1" ($r.RechargeCount -eq 1) ("got " + $r.RechargeCount)
        Check "B recharge_amount=2000" ([decimal]$r.RechargeAmount -eq 2000) ("got " + $r.RechargeAmount)
        Check "B sellcard_count=1" ($r.SellCardCount -eq 1) ("got " + $r.SellCardCount)
        Check "B sellcard_amount=500" ([decimal]$r.SellCardAmount -eq 500) ("got " + $r.SellCardAmount)
        Check "B total=2500" ([decimal]$r.TotalAmount -eq 2500) ("got " + $r.TotalAmount)
    } else { Check "B row present" $false "Liu Cashier not in map" }

    if ($map.ContainsKey("Wang Tech")) {
        $r = $map["Wang Tech"]
        Check "C order_count=1" ($r.OrdersCount -eq 1) ("got " + $r.OrdersCount)
        Check "C items_count=3" ($r.ItemsCount -eq 3) ("got " + $r.ItemsCount)
        Check "C consume_amount=300" ([decimal]$r.ConsumeAmount -eq 300) ("got " + $r.ConsumeAmount)
        Check "C total=300" ([decimal]$r.TotalAmount -eq 300) ("got " + $r.TotalAmount)
    } else { Check "C row present" $false "Wang Tech not in map" }

    $totalOrders = 0
    foreach ($r in $rows) { $totalOrders += [int]$r.OrdersCount }
    Check "excludes refunded/other-date (4 total)" ($totalOrders -eq 4) ("got " + $totalOrders)

    if ($rows.Count -ge 3) {
        Check "sort desc by total: B first" ($rows[0].Name -eq "Liu Cashier") ("got " + $rows[0].Name)
    }
}

try {
    $empty = $method.Invoke($instance, @(""))
    Say ("empty-date returned " + $empty.Count + " rows")
    Check "empty date falls back OK" ($empty.Count -ge 0) ("returned " + $empty.Count + " rows")
} catch { Check "empty date falls back" $false $_.Exception.Message }

try {
    $none = $method.Invoke($instance, @("2020-01-01"))
    Check "no-data date returns empty" ($none.Count -eq 0) ("got " + $none.Count)
} catch { Check "no-data date" $false $_.Exception.Message }

Say ("RESULT: pass=" + $pass + " fail=" + $fail)
exit 0
