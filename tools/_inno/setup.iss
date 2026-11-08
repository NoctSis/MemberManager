; ============================================================================
;  MemberManager / hui yuan guan jia  -  Inno Setup 6 installer script
;
;  Sources (never stage the runtime inside beta\ - compile-inprocess.ps1 wipes it):
;    beta\      application binaries (explicit file list, build fails loudly)
;    web\dist   front-end assets (always freshest vite output)
;    runtime\   WebView2 Runtime 109.0.1518.140 x86
;               = last fixed-version runtime that still boots on Windows 7.
;               ShellForm.ResolveRuntimeFolder() looks at <exedir>\runtime FIRST,
;               so a shop PC needs nothing pre-installed (no registry, no store).
;
;  Install mode: per-user into %LOCALAPPDATA%\MemberManager -> no UAC prompt,
;                works on Windows 7 / 10 / 11, uninstall needs no admin either.
;  Data policy : uninstall NEVER deletes shop data unless the user explicitly
;                confirms it (default button = keep).
; ============================================================================

#define MyAppName      "会员管家"
#define MyAppExeName   "MemberManager.exe"
#define MyAppVersion   "1.0.1"
#define MyAppPublisher "会员管家"

; Stable AppId: keeps upgrade / uninstall / start-menu entry tied together.
#define MyAppId "{{7B3E9C21-5A48-4D6F-9C1B-2E8F4A7D3C65}"

; ---- build variants -------------------------------------------------------
;   default        : bundles WebView2 runtime 109 + .NET 4.8 offline  (~284 MB)
;                    -> brand-new / offline / Windows 7 machine
;   ISCC /DLIGHT=1 : bundles NEITHER  (~2.5 MB)
;                    -> Windows 10/11 machine that already has Edge WebView2
;                       and .NET Framework 4.8 (i.e. every updated Win10/11)
#ifdef LIGHT
  #define OutBase "收银系统-安装程序-轻量版"
#else
  #define OutBase "会员管家-安装程序-1.0.1"
#endif

; ---- paths: all relative to this script (tools\_inno\), so a fresh clone on
; ---- any machine compiles without editing. SourcePath is supplied by ISCC.
#define RepoRoot   AddBackslash(SourcePath) + "..\.."
#define BetaDir    RepoRoot + "\beta"
#define WebDir     RepoRoot + "\web\dist"
#define RtDir      RepoRoot + "\deps\wv2-runtime-109"
; .NET Framework 4.8 offline installer (115MB, AllOS x86+x64) — bundled so a shop
; PC with no internet can still be set up in one double-click. Installed silently
; only when HasDotNet48() is false; skipped entirely on machines that already have it.
; Both it and the WebView2 runtime live in <repo>\deps\ so a rebuild does not
; depend on the Downloads folder (which gets cleaned out).
#define DotNet48Dir RepoRoot + "\deps"
; empty-seed marker lives beside this script (NOT in beta\ - the in-process build
; wipes beta\* on every compile). Ships so a fresh install seeds only the
; system-required rows (manager + cashier accounts, roles, settings) and no demo data.
#define SeedDir    RemoveBackslash(SourcePath)

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\MemberManager
; DefaultDirName already ends with the folder we want - stop Inno from appending
; the (Chinese) AppName and creating ...\MemberManager\会员管家.
AppendDefaultDirName=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; per-user install: standard user rights are enough, never trigger UAC
PrivilegesRequired=lowest
; Windows 7 is the floor. 6.1 (not the recommended 6.1sp1) on purpose: a few
; shop PCs still run an unserviced Win7 and we would rather let them try than
; hard-refuse them at the installer door.
MinVersion=6.1
OutputDir={#RepoRoot}\release
OutputBaseFilename={#OutBase}
; The 191MB msedge.dll is the stress case: ISCC.exe is a 32-bit process, so
; lzma2/max (64MB dictionary) OOMs while compressing it. normal + non-solid
; keeps peak memory low and still shrinks the runtime to ~1/3.
Compression=lzma2/fast
SolidCompression=no
; Keep compression inside ISCC.exe: the separate islzma*.exe helper gets
; blocked by the sandbox / is not worth the risk here.
LZMAUseSeparateProcess=no
WizardStyle=modern
; installer's own icon (the little icon in Add/Remove Programs and the setup
; wizard title bar). Same asset the exe gets via compile-inprocess.ps1 [8/8].
; Kept at <repo>\assets\app.ico so the icon ships with the source (not in docs\).
SetupIconFile={#RepoRoot}\assets\app.ico
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
Uninstallable=yes
; the app writes into its own folder (portable.txt) - do not wipe on uninstall
UninstallFilesDir={app}

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "在桌面上创建快捷方式"; GroupDescription: "附加图标:"; Flags: checkedonce
Name: "starticon"; Description: "在开始菜单创建快捷方式"; GroupDescription: "附加图标:"; Flags: checkedonce

[Files]
; ---- application binaries (explicit list: missing file = build error) ----
Source: "{#BetaDir}\MemberManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\MemberManager.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\MemberManager.Models.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\MemberManager.Common.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\MemberManager.DAL.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\MemberManager.BLL.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\Newtonsoft.Json.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\System.Data.SQLite.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\SQLite.Interop.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BetaDir}\portable.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SeedDir}\empty-seed.txt"; DestDir: "{app}"; Flags: ignoreversion
; ---- front-end assets ----
Source: "{#WebDir}\*"; DestDir: "{app}\web"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef LIGHT
; light build: no bundled runtime / no .NET offline bundle. The app falls back to
; the system WebView2 (Edge) through ShellForm.ResolveRuntimeFolder().
#else
; ---- bundled WebView2 runtime (required for Windows 7) ----
Source: "{#RtDir}\*"; DestDir: "{app}\runtime"; Flags: ignoreversion recursesubdirs createallsubdirs
; ---- bundled .NET Framework 4.8 offline installer (only extracted when missing) ----
; dontcopy: stays inside the setup exe, extracted to {tmp} on demand by InstallDotNet48.
Source: "{#DotNet48Dir}\NDP48-x86-x64-AllOS-ENU.exe"; DestDir: "{tmp}"; Flags: dontcopy deleteafterinstall
#endif

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; IconIndex: 0; Tasks: desktopicon
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; IconIndex: 0; Tasks: starticon
; Uninstall entry must NOT depend on the start-menu task: if the user unticks
; "start menu shortcut" during setup, there would otherwise be no visible way
; to uninstall from the Start Menu (a proper app always ships one).
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

; ---------------------------------------------------------------------------
;  Code section
; ---------------------------------------------------------------------------
[Code]
const
  RunKeyPath   = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'MemberManager';
  // Official minimum Release DWORD for .NET Framework 4.8
  DotNet48Release = 528040;
  // Named line break: a literal #13#10 at the start of a line would be eaten by
  // the Inno Setup Preprocessor (it treats a leading '#' as a directive).
  // NOTE: inside [Code] comments use // or { } - a leading ';' Pascal-parses as
  // an empty statement and terminates the const block.
  NL = #13#10;

function HasDotNet48(): Boolean;
var
  Release: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKEY_LOCAL_MACHINE,
       'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
    Result := (Release >= DotNet48Release);
end;

procedure OpenUrl(Url: String);
var
  ErrorCode: Integer;
begin
  ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function InitializeSetup(): Boolean;
begin
  // .NET 4.8 is bundled (dontcopy) and installed silently later in
  // PrepareToInstall — never block the wizard here, so a no-internet shop PC
  // still finishes in one double-click.
  Result := True;
end;

/// <summary>
/// Runs before any file is copied. Installs the bundled .NET 4.8 offline package
/// when the machine lacks it. Returns a non-empty string to abort the install.
/// </summary>
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  DotNetPath: String;
begin
  Result := '';
  if HasDotNet48() then
  begin
    Log('.NET 4.8 already present, skipping bundled installer');
    exit;
  end;

#ifdef LIGHT
  // Light build ships no offline package. Say so plainly instead of failing on a
  // missing ExtractTemporaryFile.
  Result := '这台电脑缺少 .NET Framework 4.8，轻量版安装包没有自带运行环境。' + NL +
            NL +
            '请先安装 .NET Framework 4.8（微软官网免费下载），或改用「Win7 离线版」安装包。';
  exit;
#endif

  WizardForm.StatusLabel.Caption := '正在安装运行环境 .NET Framework 4.8（首次安装需几分钟）…';
  WizardForm.StatusLabel.Update;
  Log('Installing bundled .NET Framework 4.8 offline package');

  ExtractTemporaryFile('NDP48-x86-x64-AllOS-ENU.exe');
  DotNetPath := ExpandConstant('{tmp}\NDP48-x86-x64-AllOS-ENU.exe');

  // /q = quiet, /norestart = let our own wizard decide about rebooting.
  if not Exec(DotNetPath, '/q /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := '运行环境安装失败（无法启动安装程序）。请手动安装安装包目录下的 NDP48 后重试。';
    exit;
  end;
  Log('.NET 4.8 installer exit code: ' + IntToStr(ResultCode));

  // 0 = ok, 1641 = ok-but-reboot-required, 3010 = success-reboot-required.
  if (ResultCode <> 0) and (ResultCode <> 1641) and (ResultCode <> 3010) then
  begin
    Result := '运行环境安装失败（错误码 ' + IntToStr(ResultCode) + '）。' + NL +
              '这台电脑可能缺少系统补丁（Win7 需 SP1 + KB4474419 + KB4490628）。' + NL +
              '请先装好补丁后重试。';
    exit;
  end;

  if (ResultCode = 1641) or (ResultCode = 3010) then
    NeedsRestart := True;
end;

// Closing the running app up front: it keeps a handle on its own dll files and
// on data\member.db (the exact situation compile-inprocess.ps1 guards against).
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM ' + '{#MyAppExeName}', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

// Auto-start lives in HKCU Run only (AutoStart.cs - registry is the single
// source of truth). Leaving it behind would launch a deleted exe every logon.
procedure ClearAutoStart();
begin
  if RegDeleteValue(HKEY_CURRENT_USER, RunKeyPath, RunValueName) then
    Log('Removed auto-start value from HKCU Run');
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ExePath: String;
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Prefer the app's own switch so removal goes through the same code path
    // as the Settings toggle; the registry delete below is the safety net.
    ExePath := ExpandConstant('{app}\{#MyAppExeName}');
    if FileExists(ExePath) then
      Exec('"' + ExePath + '"', '--disable-autostart', '', SW_HIDE,
           ewWaitUntilTerminated, ResultCode);
    ClearAutoStart();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{app}\data');
    // Shop owner 11-05: only the program's own directory and its RUNTIME data
    // may be removed. Exported Excel files are the user's own keepsakes - even
    // when they sit in {app}\exports, they must NEVER be touched by uninstall.
    // Files the user exported elsewhere are of course out of scope entirely.
    if DirExists(DataDir) or DirExists(ExpandConstant('{app}\backups')) then
    begin
      // 11-05: a SILENT uninstall must never destroy shop data. There is nobody to
      // answer the prompt, and SUPPRESSMSGBOXES resolves it to the first button -
      // which deleted the whole database during our own silent-uninstall test.
      // Unattended removal therefore always keeps data; only an interactive
      // uninstall can delete it, and even then the default button is "keep".
      if UninstallSilent then
        Log('Silent uninstall: shop data kept on purpose (' + DataDir + ')')
      else if MsgBox('软件已经卸载，会员数据还留在电脑上。' + NL + NL +
                '要一并删除吗？只删本程序安装目录里的运行数据' + NL +
                '（会员和充值消费流水、备份、操作日志）。' + NL +
                '你单独导出的 Excel 报表和程序目录以外的文件都不会动。' + NL + NL +
                '数据位置：' + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
        DelTree(ExpandConstant('{app}\backups'), True, True, True);
        DelTree(ExpandConstant('{app}\logs'), True, True, True);
        RemoveDir(ExpandConstant('{app}'));
      end;
    end;
  end;
end;
