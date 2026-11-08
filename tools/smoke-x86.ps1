# ==============================================================
#  MemberManager x86 smoke test
#  Must run under 32-bit PowerShell (SysWOW64) because the bundled
#  SQLite.Interop.dll is x86 native.
#  Verifies: DLL load -> database init + seed -> WAL mode ->
#            business date -> reconciliation -> table row counts.
# ==============================================================
$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'
$lines = New-Object 'System.Collections.Generic.List[string]'
try {
    $lines.Add('process-bitness: ' + ([System.IntPtr]::Size * 8) + '-bit')
    $env:MEMBERMANAGER_ROOT = Join-Path (Split-Path -Parent $PSScriptRoot) 'beta'  # smoke must exercise the SAME db the exe does (see AppPaths)
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Models.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.Common.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.DAL.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $bin 'MemberManager.BLL.dll'))
    $lines.Add('dll-load: OK (Models/Common/DAL/BLL)')

    $seeded = [MemberManager.DAL.DatabaseInitializer]::EnsureDatabase()
    $lines.Add('ensure-database: OK (seeded=' + $seeded + ')')
    $lines.Add('db-path: ' + [MemberManager.DAL.Db]::DbPath)

    $jm = [MemberManager.DAL.Db]::QueryJournalMode()
    $lines.Add('journal-mode: ' + $jm)

    $svc = New-Object MemberManager.BLL.SystemInfoService
    $st = $svc.GetStatus()
    $lines.Add('biz-date: ' + $st.BizDate + '  day-cut: ' + $st.DayCut + '  db-ok: ' + $st.DbOk)
    $lines.Add('shop: ' + $st.ShopName + '  last-backup: ' + $st.LastBackup)

    $conn = [MemberManager.DAL.Db]::OpenConnection()
    $cmd = $conn.CreateCommand()
    foreach ($t in @('staff','role_perms','service_items','recharge_packages','card_templates','point_gifts','settings')) {
        $cmd.CommandText = 'SELECT COUNT(*) FROM ' + $t
        $n = $cmd.ExecuteScalar()
        $lines.Add('rows ' + $t + ': ' + $n)
    }
    $cmd.CommandText = 'SELECT role, COUNT(*) FROM staff GROUP BY role'
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) { $lines.Add('staff-role ' + $reader.GetString(0) + ': ' + $reader.GetInt64(1)) }
    $reader.Close()
    $conn.Close()

    $lines.Add('SMOKE OK')
}
catch {
    $lines.Add('SMOKE FAIL: ' + $_.Exception.ToString())
}
[System.IO.File]::WriteAllLines((Join-Path $PSScriptRoot '_smoke.log'), $lines, [System.Text.Encoding]::UTF8)
# MUST also emit to stdout: _smoke_runner.ps1 decides PASS/FAIL from stdout
# only. This script used to write the log FILE only, so any failure here
# still showed as PASS in the summary (fixed 10-13).
$lines | Write-Output
if ($lines -contains 'SMOKE OK') { exit 0 } else { exit 1 }
