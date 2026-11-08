$ErrorActionPreference = "Continue"
$log = "$PSScriptRoot\_pkgdelete_smoke.log"
Set-Content -Path $log -Value "start" -Encoding UTF8
function Say([string]$m) {
    try { Add-Content -Path $log -Value $m } catch {}
}

Say "phase0: imports"
$asDal = $null
$asCom = $null
$asBll = $null
$db    = $null
$dbi   = $null
$svs   = $null
$auth  = $null
$ap    = $null

try {
    Say "loading DLLs"
    $asDal = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.DAL.dll")
    $asCom = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.Common.dll")
    $asBll = [System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\..\beta\MemberManager.BLL.dll")
    $db    = $asDal.GetType("MemberManager.DAL.Db")
    $dbi   = $asDal.GetType("MemberManager.DAL.DatabaseInitializer")
    $svs   = $asBll.GetType("MemberManager.BLL.StoredValueService")
    $auth  = $asBll.GetType("MemberManager.BLL.AuthService")
    $ap    = $asCom.GetType("MemberManager.Common.AppPaths")
    Say "loaded"
} catch {
    Say "load error: $($_.Exception.Message)"
}

# Temp DB
$tmp = Join-Path $env:TEMP "pkgdelete-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$dbPath = Join-Path $tmp "test.db"
Say "tmp=$tmp"

# Pin DLL paths
try {
    $rootF = $ap.GetField("_rootDir", [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static)
    if ($rootF) { $rootF.SetValue($null, $tmp); Say "pinned rootDir" }
    $dbPathF = $db.GetField("_dbPath", [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static)
    if ($dbPathF) { $dbPathF.SetValue($null, $dbPath); Say "pinned dbPath" }
} catch {
    Say "pin error: $($_.Exception.Message)"
}

# Initialize DB
try {
    $dbi.InvokeMember("EnsureDatabase", 'InvokeMethod', $null, $null, @())
    Say "EnsureDatabase OK"
} catch {
    Say "EnsureDatabase error: $($_.Exception.Message)"
    if ($_.Exception.InnerException) { Say "inner: $($_.Exception.InnerException.Message)" }
}

# Create service instance
$instance = $null
try {
    $instance = [Activator]::CreateInstance($svs)
    Say "instance created"
} catch {
    Say "instance error: $($_.Exception.Message)"
}

# Phase A: schema check
$colExists = $false
try {
    $scm = New-Object System.Data.SQLite.SQLiteConnection("Data Source=$dbPath")
    $scm.Open()
    $cmd = $scm.CreateCommand()
    $cmd.CommandText = "PRAGMA table_info(recharge_packages)"
    $r = $cmd.ExecuteReader()
    while ($r.Read()) {
        $name = $r.GetString(1)
        Say "col: $name"
        if ($name -eq "deleted_at") { $colExists = $true }
    }
    $r.Close()
    $scm.Close()
} catch {
    Say "schema check error: $($_.Exception.Message)"
}

$pass = 0; $fail = 0
function Check([string]$n, [bool]$ok) {
    if ($ok) { $script:pass++; Say "PASS  $n" }
    else     { $script:fail++; Say "FAIL  $n" }
}

Check "A1 recharge_packages has deleted_at" $colExists

# Phase B: seed
try {
    Invoke-Svc = $null
} catch {}

# Use helper function - must pass object[] not positional varargs
function CallSvc($method, [object[]]$argv) {
    $m = $svs.GetMethod($method)
    return $m.Invoke($instance, $argv)
}

try {
    # Use amounts not in the default seed (300/500/800/1000/2000/3000)
    CallSvc "SavePackage" @([int]0, [decimal]100, [decimal]0,   [bool]$true)
    CallSvc "SavePackage" @([int]0, [decimal]200, [decimal]20,  [bool]$true)
    CallSvc "SavePackage" @([int]0, [decimal]250, [decimal]30,  [bool]$true)
    Say "seeded 3 extra packages"
} catch {
    Say "seed error: $($_.Exception.Message)"
    if ($_.Exception.InnerException) { Say "seed inner: $($_.Exception.InnerException.Message)" }
}

# Get packages
$pkgs = $null
try {
    $pkgs = CallSvc "Packages" @()
    Say ("packages count: " + $pkgs.Count)
} catch {
    Say "packages error: $($_.Exception.Message)"
}

Check "B1 packages count >= 9 (6 default + 3 added)" ($pkgs -and $pkgs.Count -ge 9)

# Phase C: edit
$tid = 0
try {
    $target = $null
    foreach ($p in $pkgs) { if ($p.Amount -eq [decimal]200) { $target = $p; break } }
    $tid = $target.Id
    CallSvc "SavePackage" @([int]$tid, [decimal]200, [decimal]40, [bool]$true)
    $after = $null
    foreach ($p in (CallSvc "Packages" @())) { if ($p.Id -eq $tid) { $after = $p; break } }
    Check "C1 edit gift 20->40" ($after.Gift -eq [decimal]40)
    Check "C2 id stable" ($after.Id -eq $tid)
} catch {
    Say "edit error: $($_.Exception.Message)"
    if ($_.Exception.InnerException) { Say "edit inner: $($_.Exception.InnerException.Message)" }
}

# Phase D: edit to same amount (self) should not throw
try {
    CallSvc "SavePackage" @([int]$tid, [decimal]200, [decimal]40, [bool]$true)
    $after2 = $null
    foreach ($p in (CallSvc "Packages" @())) { if ($p.Id -eq $tid) { $after2 = $p; break } }
    Check "D1 edit to same amount (self)" ($after2.Id -eq $tid)
} catch {
    Say "D error: $($_.Exception.Message)"
    if ($_.Exception.InnerException) { Say "D inner: $($_.Exception.InnerException.Message)" }
}

# Phase E: duplicate rejected + soft delete
$delId = $tid
$beforeCount = $pkgs.Count
try {
    # Try inserting 200 -> duplicate
    $dupRejected = $false
    try {
        CallSvc "SavePackage" @([int]0, [decimal]200, [decimal]0, [bool]$true)
    } catch {
        $dupRejected = $true
    }
    Check "E1 duplicate rejected" $dupRejected

    # Delete target (200)
    CallSvc "DeletePackage" @([int]$delId)
    Say "delete called"

    $afterDel = CallSvc "Packages" @()
    $stillThere = $false
    foreach ($p in $afterDel) { if ($p.Id -eq $delId) { $stillThere = $true } }
    Check "E2 deleted package invisible" (-not $stillThere)
    Check "E3 remaining count = before-1" ($afterDel.Count -eq ($beforeCount - 1))

    # Verify soft-delete actually wrote deleted_at + enabled=0
    $scm2 = New-Object System.Data.SQLite.SQLiteConnection("Data Source=$dbPath")
    $scm2.Open()
    $cmd3 = $scm2.CreateCommand()
    $cmd3.CommandText = "SELECT deleted_at, enabled FROM recharge_packages WHERE id=@i"
    $cmd3.Parameters.AddWithValue("@i", $delId) | Out-Null
    $dr = $cmd3.ExecuteReader()
    $softOk = $false; $enblOk = $false
    if ($dr.Read()) {
        $softOk = $null -ne $dr["deleted_at"] -and $dr["deleted_at"] -ne [DBNull]::Value
        $enblOk = $dr["enabled"] -eq 0
    }
    $dr.Close()
    $scm2.Close()
    Check "E4 soft delete wrote deleted_at" $softOk
    Check "E5 soft delete set enabled=0" $enblOk

    # Try inserting 200 AFTER delete -> still rejected because UNIQUE constraint still holds
    $dupAfterDelRejected = $false
    try {
        CallSvc "SavePackage" @([int]0, [decimal]200, [decimal]0, [bool]$true)
    } catch {
        $dupAfterDelRejected = $true
    }
    Check "E6 same-amount after delete still rejected by UNIQUE" $dupAfterDelRejected
} catch {
    Say "E error: $($_.Exception.Message)"
    if ($_.Exception.InnerException) { Say "E inner: $($_.Exception.InnerException.Message)" }
}

# Phase F: delete missing
try {
    $missingOk = $false
    try {
        CallSvc "DeletePackage" @([int]999999)
    } catch {
        $missingOk = $true
    }
    Check "F1 delete missing throws" $missingOk
} catch {
    Say "F error: $($_.Exception.Message)"
}

# Phase G: Auth.Logout
try {
    $authInst = [Activator]::CreateInstance($auth)
    $logoutMethod = $auth.GetMethod("Logout")
    Check "G1 AuthService.Logout exists" ($null -ne $logoutMethod)
    $logoutMethod.Invoke($authInst, @())
    Check "G2 AuthService.Logout invoked" $true
} catch {
    Say "G error: $($_.Exception.Message)"
    if ($_.Exception.InnerException) { Say "G inner: $($_.Exception.InnerException.Message)" }
}

Say "---"
Say "PASS=$pass FAIL=$fail"

# Cleanup
try { Remove-Item $tmp -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue } catch {}
Say "temp cleanup attempted"
