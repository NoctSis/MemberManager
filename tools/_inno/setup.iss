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
#define MyAppVersion   "1.0.4"
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
  #define OutBase "会员管家-安装程序-1.0.4"
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
; Program.MarkRunning() holds these while the app is open. Checking them lets
; Setup say "please close 会员管家" (with a Retry button) instead of failing
; halfway through copying files, which is what happens if the exe is locked --
; and on this app the user cannot tell which of the many msedgewebview2.exe
; processes is the one to kill. Both names are registered: Global\ covers
; "another Windows user has it open", Local\ is the fallback path.
AppMutex=Global\MemberManager.Running,Local\MemberManager.Running
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

[Messages]
; The stock wording ("please close the running program") leaves the owner stuck:
; the app keeps running with no visible window (tray / auto-start), so "close it"
; sounds impossible and he reboots the whole PC. Say where to look.
SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请在屏幕右下角任务栏找到它的图标，右键退出；%n或按 Ctrl+Alt+Del 打开任务管理器，结束 MemberManager 进程。%n%n然后点「确定」继续安装，或点「取消」退出。

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

// Is there an msedgewebview2.exe one level down (Application\<version>\)?
function HasWebView2Under(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(Root) then exit;
  if FindFirst(AddBackslash(Root) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          if FileExists(AddBackslash(AddBackslash(Root) + FindRec.Name) + 'msedgewebview2.exe') then
            Result := True;
      until Result or (not FindNext(FindRec));
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Does this machine already have a WebView2 runtime the app can use?
// Mirrors ShellForm.ResolveRuntimeFolder(): <app>\runtime first, then the
// system-wide Edge install. The light build bundles none of this.
function HasWebView2(): Boolean;
begin
  Result := True;
  if FileExists(ExpandConstant('{app}\runtime\msedgewebview2.exe')) then exit;
  if HasWebView2Under(ExpandConstant('{pf}\Microsoft\EdgeWebView\Application')) then exit;
  if HasWebView2Under(ExpandConstant('{pf32}\Microsoft\EdgeWebView\Application')) then exit;
  if HasWebView2Under(ExpandConstant('{localappdata}\Microsoft\EdgeWebView\Application')) then exit;
  Result := False;
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

// Kill the app by image name. Used by the installer (before overwriting files)
// and by the uninstaller. /F because a background instance with no window would
// otherwise ignore the polite request.
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM ' + '{#MyAppExeName}', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

/// <summary>
/// 按进程名判断程序是否在跑。
/// <para>
/// 为什么不只靠 [Setup] 的 AppMutex：那个互斥体是 1.0.3 才加的，
/// 1.0.0–1.0.2 运行的实例根本不建它，升级时查不到 → 放行 →
/// 覆盖 MemberManager.exe.config 时被 Windows 锁住，报
/// 「MoveFile 失败: 错误代码 5。拒绝访问」（店主 11-15 客户现场实拍）。
/// </para>
/// <para>
/// tasklist 的输出在中文系统上是 GBK，但我们只匹配 ASCII 的 exe 名，
/// LoadStringsFromFile 按 ANSI 读不会影响判断。
/// </para>
/// </summary>
function IsAppRunning(): Boolean;
var
  ResultCode: Integer;
  TmpFile: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\mm-procs.txt');
  if not Exec('cmd.exe',
              '/c tasklist /FI "IMAGENAME eq {#MyAppExeName}" /NH > "' + TmpFile + '"',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    exit;
  if not FileExists(TmpFile) then exit;
  try
    if LoadStringsFromFile(TmpFile, Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        if Pos(UpperCase('{#MyAppExeName}'), UpperCase(Lines[I])) > 0 then
        begin
          Result := True;
          break;
        end;
  finally
    DeleteFile(TmpFile);
  end;
end;

/// <summary>
/// Runs before any file is copied. Installs the bundled .NET 4.8 offline package
/// when the machine lacks it. Returns a non-empty string to abort the install.
/// </summary>
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  DotNetPath: String;
  HasNet: Boolean;
begin
  Result := '';

  // ---- 先处理「程序还在跑」 ----
  // 覆盖安装要改写 exe / exe.config / dll，只要有一个进程持有这些文件，
  // Windows 就拒绝改名，向导会在半路弹「MoveFile 失败: 错误代码 5。拒绝访问」。
  // AppMutex 只认识 1.0.3 以后启动的实例，这里按进程名再兜一层。
  if IsAppRunning() then
  begin
    if WizardSilent() then
    begin
      // 无人值守安装（/SILENT、/VERYSILENT，或 IT 批量部署）：没人能点按钮，
      // 弹框只会让安装一直挂着。直接关掉继续，日志留痕。
      Log('Saw a running MemberManager instance; closing it (silent mode)');
      CloseRunningApp();
      Sleep(1500);
    end
    else if MsgBox('安装前需要先关闭「会员管家」。' + NL + NL +
                   '它可能正在后台运行（屏幕右下角任务栏里能找到）。' + NL + NL +
                   '点「是」= 现在自动关闭并继续安装' + NL +
                   '点「否」= 退出安装，您自己关掉后再运行安装包',
                   mbConfirmation, MB_YESNO) = IDYES then
    begin
      Log('Saw a running MemberManager instance, closing it before overwriting files');
      CloseRunningApp();
      Sleep(1500);   // 给 Windows 一点时间释放文件句柄
    end
    else
    begin
      Result := '已取消安装。' + NL + NL +
                '请先关闭「会员管家」（屏幕右下角任务栏图标右键退出，' + NL +
                '或按 Ctrl+Alt+Del 打开任务管理器结束 MemberManager 进程），' + NL +
                '然后重新运行安装包。';
      exit;
    end;
  end;

  HasNet := HasDotNet48();

#ifdef LIGHT
  // Light build bundles neither runtime, so both have to come from the machine.
  // Pointing a Windows 7 user at Microsoft's download page is a dead end: the
  // Evergreen bootstrapper refuses to run there and only the fixed 109 build
  // works, which is exactly what the offline package ships. So check up front
  // and name the piece that is actually missing.
  if not HasWebView2() then
  begin
    Result := '这台电脑没有找到 Edge 浏览器内核（WebView2 Runtime），轻量版安装包没有自带。' + NL + NL +
              '· Windows 7：请改用「Win7 离线版」安装包，它自带内核 109，装完就能用。' + NL +
              '· Windows 10 / 11：请先安装 Microsoft Edge（系统一般自带）。' + NL + NL +
              '提示：如果之前装过「Win7 离线版」，升级时请不要先卸载，直接覆盖安装会保留内核。';
    exit;
  end;
  if not HasNet then
  begin
    Result := '这台电脑缺少 .NET Framework 4.8，轻量版安装包没有自带运行环境。' + NL + NL +
              '· Windows 7：请改用「Win7 离线版」安装包（自带 4.8，断网也能装）。' + NL +
              '· Windows 10 / 11：请先安装 .NET Framework 4.8（微软官网免费下载）。';
    exit;
  end;
  Log('Light build: system WebView2 and .NET 4.8 both present, nothing to install');
  exit;
#endif

  if HasNet then
  begin
    Log('.NET 4.8 already present, skipping bundled installer');
    exit;
  end;

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

// (CloseRunningApp now lives above PrepareToInstall, because the installer calls
//  it too — a running instance locks exe / exe.config and the copy dies with
//  "MoveFile failed: code 5, access denied".)

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
