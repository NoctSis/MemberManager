# ==============================================================
#  MemberManager in-process build
#  Uses the Roslyn compiler API loaded inside this process.
#  No MSBuild.exe / csc.exe is executed.
#  Output: src\<proj>\bin\Release\  and  beta\
#
#  Notes:
#  - PS 5.1 cannot omit .NET optional parameters, so constructors
#    and Emit() are invoked via reflection with defaults filled.
#  - All Roslyn dependency assemblies are pre-loaded from tools\roslyn.
# ==============================================================
$ErrorActionPreference = 'Stop'
# Fallback for ScriptBlock.Invoke() calls where $PSScriptRoot is not auto-populated.
# No hardcoded machine path: derive from the script itself and fail loudly if that
# is impossible, so a fresh clone never silently builds into the wrong folder.
if (-not $PSScriptRoot -and $PSCommandPath) { $PSScriptRoot = Split-Path -Parent $PSCommandPath }
if (-not $PSScriptRoot -and $MyInvocation.MyCommand.Path) { $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $PSScriptRoot) { throw 'tools dir not found: run via -File <repo>\tools\compile-inprocess.ps1' }
$script:Root = Split-Path -Parent $PSScriptRoot
$script:LogPath = Join-Path $PSScriptRoot '_build_inproc.log'
$script:Log = New-Object 'System.Collections.Generic.List[string]'

function Write-Log([string]$msg) {
    $script:Log.Add($msg) | Out-Null
    Write-Host $msg
}

function Save-Log {
    [System.IO.File]::WriteAllLines($script:LogPath, $script:Log, (New-Object System.Text.UTF8Encoding($true)))
}

try {
    $global:MMRoslynDir = Join-Path $PSScriptRoot 'roslyn'

    # --- pre-load all Roslyn dependency assemblies ---
    Get-ChildItem $global:MMRoslynDir -Filter 'System.*.dll' | ForEach-Object {
        [void][System.Reflection.Assembly]::LoadFrom($_.FullName)
    }

    # --- auto-resolve remaining Roslyn dependency assemblies ---
    $onResolve = [ResolveEventHandler] {
        param($s, $e)
        try {
            $n = (New-Object System.Reflection.AssemblyName($e.Name)).Name
            $p = Join-Path $global:MMRoslynDir ($n + '.dll')
            if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
        }
        catch { }
        return $null
    }
    [System.AppDomain]::CurrentDomain.add_AssemblyResolve($onResolve)

    Write-Log '[1/7] Loading Roslyn compiler API ...'
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $global:MMRoslynDir 'Microsoft.CodeAnalysis.dll'))
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $global:MMRoslynDir 'Microsoft.CodeAnalysis.CSharp.dll'))

    # --- framework reference assemblies ---
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $baseRefs = New-Object 'System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]'
    foreach ($a in @('mscorlib.dll','System.dll','System.Core.dll','System.Data.dll','System.Xml.dll','Microsoft.CSharp.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Configuration.dll','System.Transactions.dll','System.IO.Compression.dll')) {
        $p = Join-Path $fw $a
        if (-not (Test-Path $p)) { throw ('Framework assembly not found: ' + $p) }
        $baseRefs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($p)) | Out-Null
    }
    $sqliteRef = [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $script:Root 'lib\SystemDataSQLite\System.Data.SQLite.dll'))

    $parseOpts = New-Object Microsoft.CodeAnalysis.CSharp.CSharpParseOptions([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp7_3, [Microsoft.CodeAnalysis.DocumentationMode]::Parse, [Microsoft.CodeAnalysis.SourceCodeKind]::Regular)

    function Build-Project([string]$name, $kind, $plat, [object[]]$extraRefs, [string]$outPath, [string]$manifestPath) {
        $projDir = Join-Path $script:Root ('src\' + $name)
        $refs = New-Object 'System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]'
        foreach ($r in $baseRefs) { $refs.Add($r) | Out-Null }
        foreach ($r in $extraRefs) {
            if ($null -ne $r) {
                if ($r -is [string]) { $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($r)) | Out-Null }
                else { $refs.Add($r) | Out-Null }
            }
        }
        $trees = New-Object 'System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]'
        Get-ChildItem $projDir -Recurse -Filter *.cs | ForEach-Object {
            $text = [System.IO.File]::ReadAllText($_.FullName)
            $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text, $script:parseOpts, $_.FullName)
            $trees.Add($tree) | Out-Null
        }

        # Construct options via reflection: PS 5.1 cannot bind constructor optional
        # parameters, so fill omitted ones with their declared default values.
        $opts = $null
        foreach ($c in [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions].GetConstructors() | Sort-Object { $_.GetParameters().Count } -Descending) {
            $cps = $c.GetParameters()
            if ($cps.Count -lt 1) { continue }
            $vals = New-Object object[] $cps.Count
            for ($i = 0; $i -lt $cps.Count; $i++) {
                if ($i -eq 0) { $vals[$i] = $kind }
                elseif ($cps[$i].HasDefaultValue) { $vals[$i] = $cps[$i].DefaultValue }
                elseif ($cps[$i].ParameterType.IsValueType) { $vals[$i] = [System.Activator]::CreateInstance($cps[$i].ParameterType) }
                else { $vals[$i] = $null }
            }
            try { $opts = $c.Invoke($vals); break } catch { $opts = $null }
        }
        if ($null -eq $opts) { throw 'cannot construct CSharpCompilationOptions' }
        $opts = $opts.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithPlatform($plat)
        try { $opts = $opts.WithEmitPdb($false) } catch { }

        $comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($name, $trees, $refs, $opts)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null

        # Stream-based Emit via reflection; win32Resources slot gets the
        # application manifest content when provided.
        $emitMethod = $comp.GetType().GetMethods() | Where-Object { $_.Name -eq 'Emit' -and $_.GetParameters().Count -gt 0 -and $_.GetParameters()[0].ParameterType -eq [System.IO.Stream] } | Select-Object -First 1
        if ($null -eq $emitMethod) { throw 'Emit(Stream,...) overload not found' }
        $eps = $emitMethod.GetParameters()
        $evals = New-Object object[] $eps.Count
        $peFs = [System.IO.File]::Create($outPath)
        $winFs = $null
        try {
            for ($i = 0; $i -lt $eps.Count; $i++) {
                $t = $eps[$i].ParameterType
                if ($i -eq 0) { $evals[$i] = $peFs }
                elseif ($eps[$i].Name -eq 'win32Resources' -and $manifestPath -and (Test-Path $manifestPath)) {
                    $winFs = [System.IO.File]::OpenRead($manifestPath)
                    $evals[$i] = $winFs
                }
                elseif ($t -eq [System.Threading.CancellationToken]) { $evals[$i] = [System.Threading.CancellationToken]::None }
                elseif ($t -eq [bool]) { $evals[$i] = $false }
                elseif ($t.IsValueType) { $evals[$i] = [System.Activator]::CreateInstance($t) }
                else { $evals[$i] = $null }
            }
            $emit = $emitMethod.Invoke($comp, $evals)
        }
        finally {
            $peFs.Dispose()
            if ($null -ne $winFs) { $winFs.Dispose() }
        }

        if (-not $emit.Success) {
            Write-Log ('FAIL  ' + $name)
            foreach ($d in $emit.Diagnostics) {
                if ($d.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) {
                    Write-Log ('  ' + $d.ToString())
                }
            }
            return $false
        }
        Write-Log ('  OK  ' + $name + '  ->  ' + $outPath.Substring($script:Root.Length + 1))
        return $true
    }

    $binModels = Join-Path $script:Root 'src\MemberManager.Models\bin\Release\MemberManager.Models.dll'
    $binCommon = Join-Path $script:Root 'src\MemberManager.Common\bin\Release\MemberManager.Common.dll'
    $binDal    = Join-Path $script:Root 'src\MemberManager.DAL\bin\Release\MemberManager.DAL.dll'
    $binBll    = Join-Path $script:Root 'src\MemberManager.BLL\bin\Release\MemberManager.BLL.dll'

    Write-Log '[2/7] Compiling MemberManager.Models ...'
    if (-not (Build-Project 'MemberManager.Models' ([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary) ([Microsoft.CodeAnalysis.Platform]::AnyCpu) @() $binModels $null)) { throw 'Models compile failed' }

    Write-Log '[3/7] Compiling MemberManager.Common ...'
    if (-not (Build-Project 'MemberManager.Common' ([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary) ([Microsoft.CodeAnalysis.Platform]::AnyCpu) @() $binCommon $null)) { throw 'Common compile failed' }

    Write-Log '[4/7] Compiling MemberManager.DAL ...'
    if (-not (Build-Project 'MemberManager.DAL' ([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary) ([Microsoft.CodeAnalysis.Platform]::AnyCpu) @($binModels, $binCommon, $sqliteRef) $binDal $null)) { throw 'DAL compile failed' }

    Write-Log '[5/7] Compiling MemberManager.BLL ...'
    if (-not (Build-Project 'MemberManager.BLL' ([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary) ([Microsoft.CodeAnalysis.Platform]::AnyCpu) @($binModels, $binCommon, $binDal, $sqliteRef) $binBll $null)) { throw 'BLL compile failed' }

    Write-Log '[6/7] Compiling MemberManager.Shell (WinExe, x86, WebView2 host) ...'
    $binShell = Join-Path $script:Root 'src\MemberManager.Shell\bin\Release\MemberManager.exe'
    $shellRefs = @($binModels, $binCommon, $binDal, $binBll, $sqliteRef,
        (Join-Path $script:Root 'libs\Microsoft.Web.WebView2.Core.dll'),
        (Join-Path $script:Root 'libs\Microsoft.Web.WebView2.WinForms.dll'),
        (Join-Path $script:Root 'libs\Newtonsoft.Json.dll'))
    foreach ($g in @('System.Net.Http.dll','System.IO.Compression.dll','System.Runtime.Serialization.dll','System.Xml.Linq.dll','System.Numerics.dll')) {
        $gp = Join-Path $fw $g
        if (Test-Path $gp) { $shellRefs += $gp }
    }
    if (-not (Build-Project 'MemberManager.Shell' ([Microsoft.CodeAnalysis.OutputKind]::WindowsApplication) ([Microsoft.CodeAnalysis.Platform]::X86) $shellRefs $binShell $null)) { throw 'Shell compile failed' }

    Write-Log '[7/7] Assembling beta ...'
    $dist = Join-Path $script:Root 'beta'
    # Green/portable mode: data\ logs\ backups\ hold the real data (including the
    # shop's own DB). Never delete them on rebuild -- after deletion the next start
    # creates a fresh empty database and the local history is gone for good.
    # Only rebuildable artifacts (exe/dll/web, etc.) are cleared, and files that are
    # locked are skipped silently instead of aborting the package assembly.
    if (Test-Path $dist) {
        Get-ChildItem $dist -Force | Where-Object { $_.Name -notin @('data', 'logs', 'backups') } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        New-Item -ItemType Directory -Force -Path $dist | Out-Null
    }

    $distFiles = @(
        'src\MemberManager.Shell\bin\Release\MemberManager.exe',
        'src\MemberManager.Models\bin\Release\MemberManager.Models.dll',
        'src\MemberManager.Common\bin\Release\MemberManager.Common.dll',
        'src\MemberManager.DAL\bin\Release\MemberManager.DAL.dll',
        'src\MemberManager.BLL\bin\Release\MemberManager.BLL.dll',
        'libs\Microsoft.Web.WebView2.Core.dll',
        'libs\Microsoft.Web.WebView2.WinForms.dll',
        'libs\Newtonsoft.Json.dll',
        'libs\WebView2Loader.dll'
    )
    foreach ($f in $distFiles) { Copy-Item (Join-Path $script:Root $f) $dist -Force }

    $appCfg = Join-Path $script:Root 'src\MemberManager.Shell\App.config'
    if (Test-Path $appCfg) { Copy-Item $appCfg (Join-Path $dist 'MemberManager.exe.config') -Force }

    # portable marker: if this file exists next to the exe, data/logs/backups are kept
    # beside the exe (green / no-install). Delete it and data falls back to the
    # current user's %LocalAppData%\MemberManager\.
    $pm = Join-Path $dist 'portable.txt'
    [System.IO.File]::WriteAllText($pm,
        "This file makes MemberManager run in PORTABLE (green) mode.`r`n" +
        "Data / logs / backups are kept next to MemberManager.exe (data\ logs\ backups\).`r`n" +
        "Copy this whole folder anywhere and it keeps working; no installation needed.`r`n" +
        "Delete this file to fall back to %LocalAppData%\MemberManager\ (installed mode).`r`n",
        [System.Text.Encoding]::ASCII)
    Write-Log '  beta\portable.txt  (green mode marker)'

    # front-end assets: web\dist -> dist\web
    $webDist = Join-Path $script:Root 'web\dist'
    if (Test-Path $webDist) {
        Copy-Item $webDist (Join-Path $dist 'web') -Recurse -Force
    }
    else {
        Write-Log '  WARN web\dist not found, run: npm run build in web\'
    }
    $libDir = Join-Path $script:Root 'lib\SystemDataSQLite'
    Copy-Item (Join-Path $libDir 'System.Data.SQLite.dll') $dist -Force
    Copy-Item (Join-Path $libDir 'x86\SQLite.Interop.dll') (Join-Path $dist 'SQLite.Interop.dll') -Force

    Get-ChildItem $dist -Recurse -File | ForEach-Object {
        Write-Log ('  beta\' + $_.FullName.Substring($dist.Length + 1) + '  (' + [math]::Round($_.Length / 1KB) + ' KB)')
    }

    # [8/8] patch app icon + Win32 version info (RT_ICON / RT_GROUP_ICON / RT_VERSION) into both exes
    Write-Log '[8/8] Patching application icon + version info ...'
    # icon lives at <repo>\assets\app.ico (ships with the source, kept out of docs\ so
    # the public repo stays clean). Legacy projects keep the icon under
    # docs\<cn-sucai>\<cn-iconset>\planB-<cn>.ico, so that path is the fallback.
    # Anchor to $script:Root so it survives moving the repo. Non-ASCII path segments
    # are built from [char] codes: this script must stay pure ASCII (no BOM), or
    # PS 5.1 decodes it as ANSI and silently breaks assertions on Chinese strings.
    $icoPath = Join-Path $script:Root 'assets\app.ico'
    if (-not (Test-Path $icoPath)) {
        $icoPath = Join-Path $script:Root ('docs\' + [char]0x7D20 + [char]0x6750 + '\' + [char]0x8F6F + [char]0x4EF6 + [char]0x56FE + [char]0x6807 + [char]0x5305 + '\' + [char]0x4E3B + [char]0x56FE + [char]0x6807 + 'ICO\' + [char]0x65B9 + [char]0x6848 + 'B-' + [char]0x6536 + [char]0x94F6 + [char]0x5C0F + [char]0x7968 + '.ico')
    }
    if (Test-Path $icoPath) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ResPatcher {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr BeginUpdateResource(string p, bool b);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool UpdateResource(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool EndUpdateResource(IntPtr h, bool b);
}
"@
        $ico = [System.IO.File]::ReadAllBytes($icoPath)
        $count = [BitConverter]::ToUInt16($ico, 4)
        $grp = New-Object System.IO.MemoryStream
        $gw = New-Object System.IO.BinaryWriter($grp)
        $targets = @((Join-Path $script:Root 'src\MemberManager.Shell\bin\Release\MemberManager.exe'), (Join-Path $script:Root 'beta\MemberManager.exe'))
        foreach ($exe in $targets) {
            if (-not (Test-Path $exe)) { continue }
            # GRPICONDIR header (reserved/type/count = 6 bytes) must be written for EVERY
            # target. Previously it was written once before the loop while the stream is
            # reset after each iteration, so the 2nd exe (dist) got a 98-byte group icon
            # with no header -> Windows rejected it and showed the generic app icon.
            $grp.SetLength(0); $grp.Seek(0, 'Begin') | Out-Null
            $gw.Write([uint16]0); $gw.Write([uint16]1); $gw.Write([uint16]$count)
            $h = [ResPatcher]::BeginUpdateResource($exe, $false)
            if ($h -eq [IntPtr]::Zero) { throw ('BeginUpdateResource failed: ' + $exe) }
            try {
                for ($i = 0; $i -lt $count; $i++) {
                    $off = 6 + 16 * $i
                    $bytes = [BitConverter]::ToUInt32($ico, $off + 8)
                    $imgOff = [BitConverter]::ToUInt32($ico, $off + 12)
                    $img = New-Object byte[] $bytes
                    [Array]::Copy($ico, [int]$imgOff, $img, 0, [int]$bytes)
                    if (-not [ResPatcher]::UpdateResource($h, [IntPtr]3, [IntPtr]($i + 1), 0, $img, $bytes)) { throw 'UpdateResource RT_ICON failed' }
                    # GRPICONDIRENTRY: width,height,colors,reserved,planes(2),bitcount(2),bytes(4),id(2) = 14 bytes
                    $gw.Write($ico[$off]); $gw.Write($ico[$off + 1]); $gw.Write($ico[$off + 2]); $gw.Write($ico[$off + 3])
                    $gw.Write([BitConverter]::ToUInt16($ico, $off + 4)); $gw.Write([BitConverter]::ToUInt16($ico, $off + 6))
                    $gw.Write([uint32]$bytes); $gw.Write([uint16]($i + 1))
                }
                $gbuf = $grp.ToArray()
                if (-not [ResPatcher]::UpdateResource($h, [IntPtr]14, [IntPtr]1, 0, $gbuf, [uint32]$gbuf.Length)) { throw 'UpdateResource RT_GROUP_ICON failed' }
            } finally {
                $grp.SetLength(0); $grp.Seek(0, 'Begin') | Out-Null
            }
            if (-not [ResPatcher]::EndUpdateResource($h, $false)) { throw ('EndUpdateResource failed: ' + $exe) }
            Write-Log ('  icon patched: ' + $exe.Substring($script:Root.Length + 1))
        }
        $gw.Dispose()
    } else {
        Write-Log ('  WARN icon file not found: ' + $icoPath)
    }

    # ---- Win32 version info (RT_VERSION = 16) ----
    # Roslyn's Emit(Stream) does NOT synthesize a VS_VERSIONINFO resource the way the
    # csc.exe command line does, so without this the exe shows an empty Details tab
    # (no product name / file version). We build the structure by hand and inject it
    # with UpdateResource, same kernel32 trick as the icon patch above.
    if (-not ([System.Management.Automation.PSTypeName]'ResPatcher').Type) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ResPatcher {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr BeginUpdateResource(string p, bool b);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool UpdateResource(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool EndUpdateResource(IntPtr h, bool b);
}
"@
    }
    # Brand strings (built from char codes so this .ps1 stays pure ASCII)
    $mmCn = [string]([char]0x4F1A + [char]0x5458 + [char]0x7BA1 + [char]0x5BB6)   # brand name

    function New-VerNode {
        param([string]$Key, [string]$Value, [int]$Type)
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([uint16]0)
        if ($Type -eq 1) { $bw.Write([uint16]($Value.Length + 1)) } else { $bw.Write([uint16]0) }
        $bw.Write([uint16]$Type)
        $bw.Write([System.Text.Encoding]::Unicode.GetBytes($Key + [char]0))
        while (($ms.Length % 4) -ne 0) { $bw.Write([byte]0) }
        if ($Type -eq 1 -and $Value.Length -gt 0) { $bw.Write([System.Text.Encoding]::Unicode.GetBytes($Value + [char]0)) }
        while (($ms.Length % 4) -ne 0) { $bw.Write([byte]0) }
        $bw.Flush()
        $out = $ms.ToArray()
        $lb = [BitConverter]::GetBytes([uint16]$out.Length)
        $out[0] = $lb[0]; $out[1] = $lb[1]
        $bw.Dispose()
        return ,$out
    }
    function New-VerContainer {
        param([string]$Key, [byte[]]$Children)
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([uint16]0)
        $bw.Write([uint16]0)
        $bw.Write([uint16]1)
        $bw.Write([System.Text.Encoding]::Unicode.GetBytes($Key + [char]0))
        while (($ms.Length % 4) -ne 0) { $bw.Write([byte]0) }
        if ($null -ne $Children -and $Children.Length -gt 0) { $bw.Write($Children) }
        $bw.Flush()
        $out = $ms.ToArray()
        $lb = [BitConverter]::GetBytes([uint16]$out.Length)
        $out[0] = $lb[0]; $out[1] = $lb[1]
        $bw.Dispose()
        return ,$out
    }
    function New-VerVar {
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([uint16]0)
        $bw.Write([uint16]4)
        $bw.Write([uint16]0)
        $bw.Write([System.Text.Encoding]::Unicode.GetBytes('Translation' + [char]0))
        while (($ms.Length % 4) -ne 0) { $bw.Write([byte]0) }
        $bw.Write([uint32]0x040904B0)
        $bw.Flush()
        $out = $ms.ToArray()
        $lb = [BitConverter]::GetBytes([uint16]$out.Length)
        $out[0] = $lb[0]; $out[1] = $lb[1]
        $bw.Dispose()
        return ,$out
    }

    # NOTE: inside @( ... ) the comma binds tighter than '+', so 'a' + $b would be
    # split into TWO elements. Pre-compute any concatenation before the array.
    $verCopy = 'Copyright (C) ' + $mmCn
    $verPairs = @(
        @('CompanyName', $mmCn),
        @('FileDescription', $mmCn),
        @('FileVersion', '1.0.0.0'),
        @('InternalName', 'MemberManager'),
        @('LegalCopyright', $verCopy),
        @('OriginalFilename', 'MemberManager.exe'),
        @('ProductName', $mmCn),
        @('ProductVersion', '1.0.0.0')
    )
    $strMs = New-Object System.IO.MemoryStream
    foreach ($p in $verPairs) {
        $nb = New-VerNode -Key $p[0] -Value $p[1] -Type 1
        $strMs.Write($nb, 0, $nb.Length)
    }
    $strTable = New-VerContainer -Key '040904B0' -Children $strMs.ToArray()
    $strFileInfo = New-VerContainer -Key 'StringFileInfo' -Children $strTable
    $varNode = New-VerVar
    $varFileInfo = New-VerContainer -Key 'VarFileInfo' -Children $varNode

    $ffiMs = New-Object System.IO.MemoryStream
    $ffw = New-Object System.IO.BinaryWriter($ffiMs)
    $ffw.Write([uint32]0xFEEF04BDL)
    $ffw.Write([uint32]0x00010000)
    $ffw.Write([uint32]0x00010000)
    $ffw.Write([uint32]0x00000000)
    $ffw.Write([uint32]0x00010000)
    $ffw.Write([uint32]0x00000000)
    $ffw.Write([uint32]0x0000003F)
    $ffw.Write([uint32]0x00000000)
    $ffw.Write([uint32]0x00040004)
    $ffw.Write([uint32]0x00000001)
    $ffw.Write([uint32]0x00000000)
    $ffw.Write([uint32]0x00000000)
    $ffw.Write([uint32]0x00000000)
    $ffw.Flush()
    $ffiBytes = $ffiMs.ToArray()
    $ffw.Dispose()

    $rootMs = New-Object System.IO.MemoryStream
    $rw = New-Object System.IO.BinaryWriter($rootMs)
    $rw.Write([uint16]0)
    $rw.Write([uint16]52)
    $rw.Write([uint16]0)
    $rw.Write([System.Text.Encoding]::Unicode.GetBytes('VS_VERSION_INFO' + [char]0))
    while (($rootMs.Length % 4) -ne 0) { $rw.Write([byte]0) }
    $rw.Write($ffiBytes)
    while (($rootMs.Length % 4) -ne 0) { $rw.Write([byte]0) }
    $rw.Write($strFileInfo)
    $rw.Write($varFileInfo)
    $rw.Flush()
    $verRes = $rootMs.ToArray()
    $vlb = [BitConverter]::GetBytes([uint16]$verRes.Length)
    $verRes[0] = $vlb[0]; $verRes[1] = $vlb[1]
    $rw.Dispose()
    $strMs.Dispose()

    $verTargets = @((Join-Path $script:Root 'src\MemberManager.Shell\bin\Release\MemberManager.exe'), (Join-Path $script:Root 'beta\MemberManager.exe'))
    foreach ($exe in $verTargets) {
        if (-not (Test-Path $exe)) { continue }
        $vh = [ResPatcher]::BeginUpdateResource($exe, $false)
        if ($vh -eq [IntPtr]::Zero) { throw ('BeginUpdateResource failed (ver): ' + $exe) }
        if (-not [ResPatcher]::UpdateResource($vh, [IntPtr]16, [IntPtr]1, 0, $verRes, [uint32]$verRes.Length)) { throw 'UpdateResource RT_VERSION failed' }
        if (-not [ResPatcher]::EndUpdateResource($vh, $false)) { throw ('EndUpdateResource failed (ver): ' + $exe) }
        Write-Log ('  version info patched: ' + $exe.Substring($script:Root.Length + 1))
    }

    # ---- application manifest (RT_MANIFEST = 24, id = 1) ----
    # This MUST be embedded for Windows 7 support: SetProcessDpiAwarenessContext() is a
    # Windows 10 1703+ API, so on Win7 the code-side call throws EntryPointNotFound and is
    # swallowed by the empty catch -> without an embedded manifest the process would be
    # DPI-unaware there (blurry UI on scaled displays). <dpiAware> in the manifest has been
    # honoured since Vista and needs no API. Leading UTF-8 BOM is stripped (some manifest
    # parsers reject it).
    $mfPath = Join-Path $script:Root 'src\MemberManager.Shell\app.manifest'
    if (Test-Path $mfPath) {
        $mfBytes = [System.IO.File]::ReadAllBytes($mfPath)
        if ($mfBytes.Length -ge 3 -and $mfBytes[0] -eq 0xEF -and $mfBytes[1] -eq 0xBB -and $mfBytes[2] -eq 0xBF) {
            $mfBytes = $mfBytes[3..($mfBytes.Length - 1)]
        }
        foreach ($exe in $verTargets) {
            if (-not (Test-Path $exe)) { continue }
            $mh = [ResPatcher]::BeginUpdateResource($exe, $false)
            if ($mh -eq [IntPtr]::Zero) { throw ('BeginUpdateResource failed (manifest): ' + $exe) }
            if (-not [ResPatcher]::UpdateResource($mh, [IntPtr]24, [IntPtr]1, 0, $mfBytes, [uint32]$mfBytes.Length)) { throw 'UpdateResource RT_MANIFEST failed' }
            if (-not [ResPatcher]::EndUpdateResource($mh, $false)) { throw ('EndUpdateResource failed (manifest): ' + $exe) }
            Write-Log ('  manifest embedded: ' + $exe.Substring($script:Root.Length + 1))
        }
    } else {
        Write-Log ('  WARN app.manifest not found: ' + $mfPath)
    }

    Write-Log 'BUILD OK'
    Save-Log
    exit 0
}
catch {
    Write-Log ('BUILD FAILED: ' + $_.Exception.Message)
    Write-Log ($_ | Out-String)
    Save-Log
    exit 1
}
