# Export staff pivot Excel smoke test.
# Runs against a COPY of the production db so real data is never modified.
# ASCII-only on purpose (PS 5.1 + BOM-less UTF8 + CJK silently breaks parsing).
$ErrorActionPreference = "Continue"
$log = "$PSScriptRoot\_export_pivot_smoke.log"
Set-Content -Path $log -Value "start" -Encoding UTF8
function Say([string]$m) { try { Add-Content -Path $log -Value $m } catch {} }

$pass = 0; $fail = 0
function Check([string]$name, [bool]$cond, [string]$detail) {
    if ($cond) { $script:pass++; Say ("PASS: " + $name + "  " + $detail) }
    else       { $script:fail++; Say ("FAIL: " + $name + "  " + $detail) }
}

try {
    $asDal = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.DAL.dll")
    $asBll = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.BLL.dll")
    $asCom = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.Common.dll")
    $db = $asDal.GetType("MemberManager.DAL.Db")
    $rs = $asBll.GetType("MemberManager.BLL.ReportService")
    $ap = $asCom.GetType("MemberManager.Common.AppPaths")
    Say "loaded assemblies"
} catch {
    Say ("load error: " + $_.Exception.Message)
    Say "RESULT: pass=0 fail=1"
    exit 0
}

$tmp = Join-Path $env:TEMP ("mmexp-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path (Join-Path $tmp "data") -Force | Out-Null
foreach ($f in @("member.db","member.db-wal","member.db-shm")) {
    $src = Join-Path "$PSScriptRoot\..\beta\data" $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $tmp "data") -Force }
}
$dbPath = Join-Path $tmp "data\member.db"
Say ("db copy: " + $dbPath + "  exists=" + (Test-Path $dbPath))

try {
    $bf = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static
    $ap.GetField("_rootDir", $bf).SetValue($null, $tmp)
    $ap.GetField("_portable", $bf).SetValue($null, $true)
    $ap.GetField("_resolved", $bf).SetValue($null, $true)
    $db.GetField("_dbPath", $bf).SetValue($null, $dbPath)
    Say "pinned rootDir/dbPath"
} catch { Say ("pin error: " + $_.Exception.Message) }

$ch_dianzhang = [string][char]0x5E97 + [char]0x957F
$outDir = "$PSScriptRoot\..\beta\data\exports"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$instance = [Activator]::CreateInstance($rs)
$method = $rs.GetMethod("ExportStaffDetailExcel")
Say ("method found=" + ($method -ne $null))

try {
    $res = $method.Invoke($instance, @("2026-09-01","2026-09-28",$ch_dianzhang,$outDir))
    Say ("EXPORT name=" + $res.Name)
    Say ("EXPORT path=" + $res.Path)
    Say ("EXPORT size=" + $res.Size)
    Check "file non-empty" ([int64]$res.Size -gt 0) ("size=" + $res.Size)
    Check "file on disk" (Test-Path $res.Path) $res.Path
} catch {
    Say ("invoke error: " + $_.Exception.Message)
    Check "export succeeded" $false $_.Exception.Message
}

Say ("RESULT: pass=" + $pass + " fail=" + $fail)
exit 0
