# ==============================================================
#  shop-smoke  (real DB; one probe write of shop_name, restored in finally)
#  Verifies SystemInfoService.GetStatus() exposes the new fields
#  (Portable / RootDir / DataDir / LogDir / BackupDir / DbPath /
#   ShopName / ShopPhone) and that Db.Portable / Db.RootDir match.
#  PURE ASCII on purpose: PS 5.1 reads BOM-less UTF-8 as ANSI.
#  Must run under 32-bit PowerShell (x86 SQLite interop).
# ==============================================================
$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot '_shop.txt'
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
    # NOTE (09-29 13:52 fix): load ONLY the 4 project assemblies, exactly like the other
    # working smoke scripts. Do NOT LoadFrom:
    #   - 'x86\SQLite.Interop.dll'  -> NATIVE image, LoadFrom throws BadImageFormatException
    #   - 'System.Data.SQLite.dll'  -> puts it in the LoadFrom context and makes
    #                                   Type.GetType('...Db, MemberManager.DAL') return $null
    # The managed SQLite assembly resolves by itself when Db.OpenConnection() first runs.

    # Type ACCELERATORS, not Type.GetType('...Name, ...Assembly'): the latter is
    # flaky once a SQLite assembly has been LoadFrom'd in the same process.
    $DAL = [Type]'MemberManager.DAL.Db'
    $SIS = [Type]'MemberManager.BLL.SystemInfoService'

    W ('dbPath   : ' + $DAL.GetProperty('DbPath').GetValue($null, $null))
    W ('portable : ' + $DAL.GetProperty('Portable').GetValue($null, $null))
    W ('rootDir  : ' + $DAL.GetProperty('RootDir').GetValue($null, $null))

    $svc = [System.Activator]::CreateInstance($SIS)
    $st = $SIS.GetMethod('GetStatus').Invoke($svc, @())

    $t = $st.GetType()
    foreach ($f in @('BizDate','DayCut','DbOk','JournalMode','LastBackup','ShopName','ShopPhone','Portable','RootDir','DataDir','LogDir','BackupDir','DbPath')) {
        $p = $t.GetField($f)
        if ($null -eq $p) { W ('MISSING FIELD: ' + $f); continue }
        W ($f.PadRight(12) + ': ' + $p.GetValue($st))
    }

    # --- SaveShop round-trip on a TEMP value, then restore ---
    $name0 = $t.GetField('ShopName').GetValue($st)
    $DI = [Type]'MemberManager.DAL.DatabaseInitializer'
    # SetSetting has TWO overloads since 09-29 (2-arg own-connection, 4-arg caller
    # conn/txn), so the name-only lookup throws AmbiguousMatchException. Also, PS 5.1
    # does NOT bind GetMethod('X', @([string],[string])) to Type[] -- it tries
    # BindingFlags. Filter GetMethods() by name + param count instead.
    $setSetting = $null
    foreach ($m in $DI.GetMethods()) {
        if ($m.Name -eq 'SetSetting' -and $m.GetParameters().Count -eq 2) { $setSetting = $m; break }
    }
    if ($null -eq $setSetting) { W 'CANNOT RESOLVE SetSetting(string,string)'; throw 'setSetting null' }
    # The probe write below now lands in the REAL db (MEMBERMANAGER_ROOT), so the
    # restore must be unconditional: wrap the write in try/finally so shop_name can
    # never be left as SMOKE_TMP_NAME if the read-back throws.
    try {
        $setSetting.Invoke($null, @('shop_name','SMOKE_TMP_NAME')) | Out-Null
        $st2 = $SIS.GetMethod('GetStatus').Invoke($svc, @())
        $name1 = $t.GetField('ShopName').GetValue($st2)
    }
    finally {
        try { $setSetting.Invoke($null, @('shop_name',$name0)) | Out-Null }
        catch { W ('RESTORE FAILED: ' + $_.Exception.Message) }
    }
    $st3 = $SIS.GetMethod('GetStatus').Invoke($svc, @())
    $name2 = $t.GetField('ShopName').GetValue($st3)
    W ('roundtrip : tmp=' + $name1 + '  restored=' + $name2 + '  same=' + ($name2 -eq $name0))

    # --- SaveShop validation guard (empty name must be rejected) ---
    $rejected = 'NO'
    try {
        $SIS.GetMethod('SaveShop').Invoke($svc, @('', 'x')) | Out-Null
    } catch {
        $rejected = 'YES (' + $_.Exception.InnerException.GetType().Name + ')'
    }
    W ('emptyNameRejected: ' + $rejected)
    W 'SHOP OK'
    Save
    exit 0
}
catch {
    W ('SHOP FAILED: ' + $_.Exception.Message)
    W ($_ | Out-String)
    W ($_.ScriptStackTrace)
    Save
    exit 1
}
