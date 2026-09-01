#define MyAppName "SuperCV"
#define MyAppPublisher "ShuaiKeAng"
#define MyAppExeName "SuperCV.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.9.1"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

#ifndef MyAppPackageSuffix
  #define MyAppPackageSuffix ""
#endif

[Setup]
AppId={{AAFE21AC-9196-43AD-9C8B-505285F65796}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/ShuaiKeAng/SuperCV
AppSupportURL=https://github.com/ShuaiKeAng/SuperCV/issues
AppUpdatesURL=https://github.com/ShuaiKeAng/SuperCV/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; DataRoot is intentionally stored for the installing user because SuperCV data is per user.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=SuperCV-Setup-{#MyAppVersion}{#MyAppPackageSuffix}
SetupIconFile=..\src\SuperCV.Presentation.Wpf\Assets\SuperCV.Remastered.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
CloseApplications=yes
RestartApplications=no
DirExistsWarning=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,runtimes\browser-wasm\*,runtimes\linux-*\*,runtimes\maccatalyst-*\*,runtimes\osx-*\*,runtimes\win-arm\*,runtimes\win-arm64\*,runtimes\win-x86\*"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef BundledDotNetRuntimePath
Source: "{#BundledDotNetRuntimePath}"; DestName: "windowsdesktop-runtime-8-win-x64.exe"; Flags: dontcopy noencryption
#endif

[Dirs]
Name: "{code:GetSelectedDataRoot}"; Flags: uninsneveruninstall

[Registry]
Root: HKCU; Subkey: "Software\SuperCV"; ValueType: string; ValueName: "DataRoot"; ValueData: "{code:GetSelectedDataRoot}"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetDesktopRuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  DotNetDesktopRuntimePageUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';
  DotNetDesktopRuntimeFileName = 'windowsdesktop-runtime-8-win-x64.exe';
  DotNetDesktopRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  DotNetInstallRootKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64';
  DataRootRegistryKey = 'Software\SuperCV';
  DataRootRegistryValue = 'DataRoot';

var
  DataRootPage: TInputDirWizardPage;
  DotNet8DesktopRuntimeFound: Boolean;
  DotNetRuntimeChoicePage: TInputOptionWizardPage;
  DotNetRuntimeDownloadPage: TDownloadWizardPage;
  DotNetRuntimeInstallPage: TOutputMarqueeProgressWizardPage;
  DownloadLastTick: Int64;
  DownloadLastBytes: Int64;
  DownloadSpeedText: String;

function GetInitialDataRoot: String;
var
  ConfiguredDataRoot: String;
begin
  Result := ExpandConstant('{localappdata}\SuperCV');
  if RegQueryStringValue(
       HKCU,
       DataRootRegistryKey,
       DataRootRegistryValue,
       ConfiguredDataRoot) and
     (Trim(ConfiguredDataRoot) <> '') then
    Result := ConfiguredDataRoot;
end;

function GetSelectedDataRoot(Param: String): String;
begin
  Result := ExpandFileName(Trim(DataRootPage.Values[0]));
end;

function PathsOverlap(const FirstPath, SecondPath: String): Boolean;
var
  FirstWithSeparator: String;
  SecondWithSeparator: String;
begin
  FirstWithSeparator := AddBackslash(RemoveBackslashUnlessRoot(FirstPath));
  SecondWithSeparator := AddBackslash(RemoveBackslashUnlessRoot(SecondPath));
  Result :=
    (CompareText(FirstWithSeparator, SecondWithSeparator) = 0) or
    (CompareText(
       Copy(FirstWithSeparator, 1, Length(SecondWithSeparator)),
       SecondWithSeparator) = 0) or
    (CompareText(
       Copy(SecondWithSeparator, 1, Length(FirstWithSeparator)),
       FirstWithSeparator) = 0);
end;

function GetTickCount64: Int64;
  external 'GetTickCount64@kernel32.dll stdcall';

procedure InspectDotNetRuntimeOutput(
  const S: String;
  const Error, FirstLine: Boolean);
begin
  if (not Error) and
     (Pos('Microsoft.WindowsDesktop.App 8.', Trim(S)) = 1) then
    DotNet8DesktopRuntimeFound := True;
end;

function FormatByteCount(const ByteCount: Int64): String;
begin
  if ByteCount >= 1073741824 then
    Result := Format(
      '%d.%d GiB', [ByteCount div 1073741824, ((ByteCount mod 1073741824) * 10) div 1073741824])
  else if ByteCount >= 1048576 then
    Result := Format(
      '%d.%d MiB', [ByteCount div 1048576, ((ByteCount mod 1048576) * 10) div 1048576])
  else
    Result := Format('%d KiB', [ByteCount div 1024]);
end;

function OnDotNetRuntimeDownloadProgress(
  const Url, FileName: String;
  const Progress, ProgressMax: Int64): Boolean;
var
  CurrentTick: Int64;
  ElapsedMilliseconds: Int64;
  BytesPerSecond: Int64;
  ProgressText: String;
begin
  CurrentTick := GetTickCount64;
  ElapsedMilliseconds := CurrentTick - DownloadLastTick;

  if ElapsedMilliseconds >= 500 then
  begin
    BytesPerSecond :=
      ((Progress - DownloadLastBytes) * 1000) div ElapsedMilliseconds;
    DownloadSpeedText := FormatByteCount(BytesPerSecond) + '/s';
    DownloadLastTick := CurrentTick;
    DownloadLastBytes := Progress;
  end;

  if ProgressMax > 0 then
    ProgressText :=
      FormatByteCount(Progress) + ' / ' + FormatByteCount(ProgressMax) +
      Format('（%d%%）', [(Progress * 100) div ProgressMax])
  else
    ProgressText := FormatByteCount(Progress);

  DotNetRuntimeDownloadPage.SetText(
    '正在下载 Microsoft .NET 8 Desktop Runtime (x64)...',
    ProgressText + '    当前速度：' + DownloadSpeedText);
  Result := True;
end;

procedure InitializeWizard;
begin
  DataRootPage := CreateInputDirPage(
    wpSelectDir,
    '选择数据存储位置',
    '设置 SuperCV 默认数据文件夹',
    'SuperCV 会在该文件夹中保存设置、工作区、历史记录和图片缓存。建议使用当前用户可写的文件夹。',
    False,
    '');
  DataRootPage.Add('数据存储位置：');
  DataRootPage.Values[0] := GetInitialDataRoot;

  DotNetRuntimeChoicePage := CreateInputOptionPage(
    wpSelectTasks,
    '选择运行环境安装方式',
    'SuperCV 需要 Microsoft .NET 8 Desktop Runtime (x64)',
    '请选择获取方式。自动下载会显示实时速度；也可以打开 Microsoft 官方网页手动安装。',
    True,
    False);
  DotNetRuntimeChoicePage.Add('自动下载并安装（推荐）');
  DotNetRuntimeChoicePage.Add('打开 Microsoft 官方下载网页手动安装');
  DotNetRuntimeChoicePage.SelectedValueIndex := 0;

  DotNetRuntimeDownloadPage := CreateDownloadPage(
    '正在下载运行环境',
    '正在从 Microsoft 下载 SuperCV 所需的组件。下载完成后将自动继续。',
    @OnDotNetRuntimeDownloadProgress);
  DotNetRuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;

  DotNetRuntimeInstallPage := CreateOutputMarqueeProgressPage(
    '正在安装运行环境',
    'Microsoft 安装程序会显示独立的安装进度，请完成 Windows 权限确认。');
end;

function DotNetExecutableHasDesktopRuntime(
  const DotNetExecutable: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not FileExists(DotNetExecutable) then
    Exit;

  DotNet8DesktopRuntimeFound := False;
  try
    ExecAndLogOutput(
      DotNetExecutable,
      '--list-runtimes',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode,
      @InspectDotNetRuntimeOutput);
  except
    Log(
      '检测 .NET Desktop Runtime 时无法运行 ' +
      DotNetExecutable + ': ' + GetExceptionMessage);
  end;

  Result := DotNet8DesktopRuntimeFound;
end;

function RegistryViewHasDotNet8DesktopRuntime(RootKey: Integer): Boolean;
var
  ValueNames: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if not RegGetValueNames(RootKey, DotNetDesktopRuntimeKey, ValueNames) then
    Exit;

  for Index := 0 to GetArrayLength(ValueNames) - 1 do
  begin
    if Pos('8.', ValueNames[Index]) = 1 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InstallRootHasDotNet8DesktopRuntime(RootKey: Integer): Boolean;
var
  InstallRoot: String;
begin
  Result :=
    RegQueryStringValue(
      RootKey,
      DotNetInstallRootKey,
      'InstallLocation',
      InstallRoot) and
    DotNetExecutableHasDesktopRuntime(
      AddBackslash(InstallRoot) + 'dotnet.exe');
end;

function IsDotNet8DesktopRuntimeInstalled: Boolean;
begin
  Result :=
    RegistryViewHasDotNet8DesktopRuntime(HKLM64) or
    RegistryViewHasDotNet8DesktopRuntime(HKLM32) or
    InstallRootHasDotNet8DesktopRuntime(HKLM64) or
    InstallRootHasDotNet8DesktopRuntime(HKLM32) or
    DotNetExecutableHasDesktopRuntime(
      ExpandConstant('{pf64}\dotnet\dotnet.exe'));
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
#ifdef BundledDotNetRuntimePath
  Result := PageID = DotNetRuntimeChoicePage.ID;
#else
  Result :=
    (PageID = DotNetRuntimeChoicePage.ID) and
    IsDotNet8DesktopRuntimeInstalled;
#endif
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ErrorCode: Integer;
  DataRoot: String;
begin
  Result := True;

  if CurPageID = DataRootPage.ID then
  begin
    DataRoot := Trim(DataRootPage.Values[0]);
    if DataRoot = '' then
    begin
      MsgBox('请选择数据存储位置。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    DataRoot := ExpandFileName(DataRoot);
    if PathsOverlap(DataRoot, ExpandConstant('{app}')) then
    begin
      MsgBox(
        '数据存储位置不能是安装目录或其父、子目录。请选择其他当前用户可写的文件夹。',
        mbError,
        MB_OK);
      Result := False;
      Exit;
    end;

    DataRootPage.Values[0] := DataRoot;
  end;

  if (CurPageID <> DotNetRuntimeChoicePage.ID) or
     (DotNetRuntimeChoicePage.SelectedValueIndex <> 1) then
    Exit;

  if IsDotNet8DesktopRuntimeInstalled then
    Exit;

  if not ShellExec(
    'open',
    DotNetDesktopRuntimePageUrl,
    '',
    '',
    SW_SHOWNORMAL,
    ewNoWait,
    ErrorCode) then
  begin
    MsgBox(
      '无法打开 Microsoft .NET 8 下载网页。' + #13#10 + #13#10 +
      DotNetDesktopRuntimePageUrl + #13#10 +
      '系统说明：' + SysErrorMessage(ErrorCode),
      mbError,
      MB_OK);
  end
  else
  begin
    MsgBox(
      '请在 Microsoft 网页的“.NET Desktop Runtime”区域下载并安装 Windows x64 版本。' + #13#10 + #13#10 +
      '安装完成后返回此向导，再次点击“下一步”。',
      mbInformation,
      MB_OK);
  end;

  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RuntimeInstallerPath: String;
  RuntimeInstallerParameters: String;
  ResultCode: Integer;
begin
  Result := '';
  if IsDotNet8DesktopRuntimeInstalled then
    Exit;

  RuntimeInstallerPath :=
    ExpandConstant('{tmp}\') + DotNetDesktopRuntimeFileName;

#ifdef BundledDotNetRuntimePath
  DotNetRuntimeInstallPage.SetText(
    '正在准备内置的 Microsoft .NET 8 Desktop Runtime (x64)...',
    '完整安装包无需联网下载运行环境。');
  DotNetRuntimeInstallPage.Show;
  DotNetRuntimeInstallPage.Animate;
  try
    try
      ExtractTemporaryFile(DotNetDesktopRuntimeFileName);
    except
      Result :=
        '无法从完整安装包中提取 Microsoft .NET 8 Desktop Runtime。' + #13#10 + #13#10 +
        '详细信息：' + GetExceptionMessage;
      Exit;
    end;
  finally
    DotNetRuntimeInstallPage.Hide;
  end;
#else
  DotNetRuntimeDownloadPage.Clear;
  DotNetRuntimeDownloadPage.Add(
    DotNetDesktopRuntimeUrl,
    DotNetDesktopRuntimeFileName,
    '');
  DotNetRuntimeDownloadPage.Show;
  try
    try
      if FileExists(RuntimeInstallerPath) then
        DeleteFile(RuntimeInstallerPath);

      DownloadLastTick := GetTickCount64;
      DownloadLastBytes := 0;
      DownloadSpeedText := '正在连接...';
      DotNetRuntimeDownloadPage.Download;
    except
      if DotNetRuntimeDownloadPage.AbortedByUser then
        Result :=
          '已取消下载 Microsoft .NET 8 Desktop Runtime。' + #13#10 + #13#10 +
          '可以重新点击“安装”后再次尝试。'
      else
        Result :=
          '无法下载 SuperCV 所需的 Microsoft .NET 8 Desktop Runtime (x64)。' + #13#10 + #13#10 +
          '请检查网络或代理设置后重试。' + #13#10 +
          '详细信息：' + GetExceptionMessage;
      Exit;
    end;
  finally
    DotNetRuntimeDownloadPage.Hide;
  end;
#endif

  if WizardSilent then
    RuntimeInstallerParameters := '/install /quiet /norestart'
  else
    RuntimeInstallerParameters := '/install /passive /norestart';

  DotNetRuntimeInstallPage.SetText(
    '正在安装 Microsoft .NET 8 Desktop Runtime (x64)...',
    '请确认 Windows 用户账户控制提示；安装完成前请勿关闭进度窗口。');
  DotNetRuntimeInstallPage.Show;
  DotNetRuntimeInstallPage.Animate;
  try
    if not ShellExec(
      'runas',
      RuntimeInstallerPath,
      RuntimeInstallerParameters,
      '',
      SW_SHOWNORMAL,
      ewWaitUntilTerminated,
      ResultCode) then
    begin
      Result :=
        '无法启动 Microsoft .NET 8 Desktop Runtime 安装程序。' + #13#10 + #13#10 +
        '请允许 Windows 用户账户控制提示后重试。' + #13#10 +
        '系统错误代码：' + IntToStr(ResultCode) + #13#10 +
        '系统说明：' + SysErrorMessage(ResultCode);
      Exit;
    end;
  finally
    DotNetRuntimeInstallPage.Hide;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    Result :=
      'Microsoft .NET 8 Desktop Runtime 安装失败。' + #13#10 + #13#10 +
      '安装程序退出代码：' + IntToStr(ResultCode);
    Exit;
  end;

  if ResultCode = 3010 then
    NeedsRestart := True;

  if not IsDotNet8DesktopRuntimeInstalled then
  begin
    Result :=
      'Microsoft .NET 8 Desktop Runtime 安装完成后仍未能检测到运行环境。' + #13#10 + #13#10 +
      '请重新启动 Windows 后再次运行 SuperCV 安装程序。';
  end;
end;
