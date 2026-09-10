#define MyAppName "SuperCV"
#define MyAppPublisher "ShuaiKeAng"
#define MyAppExeName "SuperCV.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.9.5"
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
ShowLanguageDialog=yes
CloseApplications=yes
RestartApplications=no
DirExistsWarning=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl,Languages\LanguageSelection.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,Languages\ChineseLanguageName.isl,Languages\LanguageSelection.isl"

[CustomMessages]
DesktopIconDescription=Create a desktop shortcut
AdditionalOptions=Additional options:
LaunchApplication=Launch SuperCV
DataRootTitle=Choose data location
DataRootDescription=Set the default SuperCV data folder
DataRootInstructions=SuperCV stores settings, workspaces, history, and image cache in this folder. We recommend a folder writable by the current user.
DataRootLabel=Data location:
DataRootRequired=Please choose a data location.
DataRootOverlap=The data location cannot be the installation folder, its parent folder, or a subfolder. Choose another folder writable by the current user.
RuntimeChoiceTitle=Choose runtime installation method
RuntimeChoiceDescription=SuperCV requires Microsoft .NET 8 Desktop Runtime (x64)
RuntimeChoiceInstructions=Choose how to obtain it. Automatic download shows the current speed; you can also open Microsoft's official download page to install it manually.
RuntimeDownloadRecommended=Download and install automatically (recommended)
RuntimeDownloadManual=Open Microsoft's official download page to install manually
RuntimeDownloadingTitle=Downloading runtime
RuntimeDownloadingDescription=Downloading components required by SuperCV from Microsoft. Setup will continue automatically when finished.
RuntimeInstallingTitle=Installing runtime
RuntimeInstallingDescription=Microsoft's installer displays its own progress. Complete any Windows permission prompt to continue.
RuntimeDownloadProgress=Downloading Microsoft .NET 8 Desktop Runtime (x64)...
RuntimeDownloadSpeed=Current speed:
RuntimePreparingBundled=Preparing the bundled Microsoft .NET 8 Desktop Runtime (x64)...
RuntimeBundledDescription=The complete installer does not need an internet connection to obtain the runtime.
RuntimeExtractFailed=Unable to extract the bundled Microsoft .NET 8 Desktop Runtime.%n%nDetails: %1
RuntimeConnecting=Connecting...
RuntimeDownloadCancelled=The Microsoft .NET 8 Desktop Runtime download was cancelled.%n%nClick Install again to retry.
RuntimeDownloadFailed=Unable to download the Microsoft .NET 8 Desktop Runtime (x64) required by SuperCV.%n%nCheck your network or proxy settings and retry.%nDetails: %1
RuntimeInstallingProgress=Installing Microsoft .NET 8 Desktop Runtime (x64)...
RuntimeInstallingProgressDescription=Approve the Windows User Account Control prompt and keep this window open until installation is complete.
RuntimeLaunchFailed=Unable to start the Microsoft .NET 8 Desktop Runtime installer.%n%nAllow the Windows User Account Control prompt and retry.%nSystem error code: %1%nSystem message: %2
RuntimeInstallFailed=Microsoft .NET 8 Desktop Runtime installation failed.%n%nInstaller exit code: %1
RuntimeNotDetected=Microsoft .NET 8 Desktop Runtime was installed but could not be detected.%n%nRestart Windows, then run the SuperCV installer again.
RuntimeManualOpenFailed=Unable to open the Microsoft .NET 8 download page.%n%n%1%n%nSystem message: %2
RuntimeManualOpened=Download and install the Windows x64 version from the “.NET Desktop Runtime” section of the Microsoft page.%n%nAfter installation finishes, return to this wizard and click Next again.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconDescription}"; GroupDescription: "{cm:AdditionalOptions}"; Flags: unchecked

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
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApplication}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetDesktopRuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  DotNetDesktopRuntimePageUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';
  DotNetDesktopRuntimeFileName = 'windowsdesktop-runtime-8-win-x64.exe';
  DotNetDesktopRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  DotNetInstallRootKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64';
  DataRootRegistryKey = 'Software\SuperCV';
  DataRootRegistryValue = 'DataRoot';
  SettingsFileName = 'settings.json';

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
    CustomMessage('RuntimeDownloadProgress'),
    ProgressText + '    ' + CustomMessage('RuntimeDownloadSpeed') + ' ' + DownloadSpeedText);
  Result := True;
end;

procedure InitializeWizard;
begin
  DataRootPage := CreateInputDirPage(
    wpSelectDir,
    CustomMessage('DataRootTitle'),
    CustomMessage('DataRootDescription'),
    CustomMessage('DataRootInstructions'),
    False,
    '');
  DataRootPage.Add(CustomMessage('DataRootLabel'));
  DataRootPage.Values[0] := GetInitialDataRoot;

  DotNetRuntimeChoicePage := CreateInputOptionPage(
    wpSelectTasks,
    CustomMessage('RuntimeChoiceTitle'),
    CustomMessage('RuntimeChoiceDescription'),
    CustomMessage('RuntimeChoiceInstructions'),
    True,
    False);
  DotNetRuntimeChoicePage.Add(CustomMessage('RuntimeDownloadRecommended'));
  DotNetRuntimeChoicePage.Add(CustomMessage('RuntimeDownloadManual'));
  DotNetRuntimeChoicePage.SelectedValueIndex := 0;

  DotNetRuntimeDownloadPage := CreateDownloadPage(
    CustomMessage('RuntimeDownloadingTitle'),
    CustomMessage('RuntimeDownloadingDescription'),
    @OnDotNetRuntimeDownloadProgress);
  DotNetRuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;

  DotNetRuntimeInstallPage := CreateOutputMarqueeProgressPage(
    CustomMessage('RuntimeInstallingTitle'),
    CustomMessage('RuntimeInstallingDescription'));
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
      'Unable to run while checking .NET Desktop Runtime: ' +
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

function FormatCustomMessage1(const MessageName, Value: String): String;
begin
  Result := CustomMessage(MessageName);
  StringChangeEx(Result, '%1', Value, True);
  StringChangeEx(Result, '%n', #13#10, True);
end;

function FormatCustomMessage2(
  const MessageName, FirstValue, SecondValue: String): String;
begin
  Result := CustomMessage(MessageName);
  StringChangeEx(Result, '%1', FirstValue, True);
  StringChangeEx(Result, '%2', SecondValue, True);
  StringChangeEx(Result, '%n', #13#10, True);
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
      MsgBox(CustomMessage('DataRootRequired'), mbError, MB_OK);
      Result := False;
      Exit;
    end;

    DataRoot := ExpandFileName(DataRoot);
    if PathsOverlap(DataRoot, ExpandConstant('{app}')) then
    begin
      MsgBox(CustomMessage('DataRootOverlap'), mbError, MB_OK);
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
    MsgBox(FormatCustomMessage2(
      'RuntimeManualOpenFailed',
      DotNetDesktopRuntimePageUrl,
      SysErrorMessage(ErrorCode)), mbError, MB_OK);
  end
  else
  begin
    MsgBox(CustomMessage('RuntimeManualOpened'), mbInformation, MB_OK);
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
    CustomMessage('RuntimePreparingBundled'),
    CustomMessage('RuntimeBundledDescription'));
  DotNetRuntimeInstallPage.Show;
  DotNetRuntimeInstallPage.Animate;
  try
    try
      ExtractTemporaryFile(DotNetDesktopRuntimeFileName);
    except
      Result := FormatCustomMessage1(
        'RuntimeExtractFailed', GetExceptionMessage);
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
      DownloadSpeedText := CustomMessage('RuntimeConnecting');
      DotNetRuntimeDownloadPage.Download;
    except
      if DotNetRuntimeDownloadPage.AbortedByUser then
        Result := CustomMessage('RuntimeDownloadCancelled')
      else
        Result := FormatCustomMessage1(
          'RuntimeDownloadFailed', GetExceptionMessage);
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
    CustomMessage('RuntimeInstallingProgress'),
    CustomMessage('RuntimeInstallingProgressDescription'));
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
      Result := FormatCustomMessage2(
        'RuntimeLaunchFailed',
        IntToStr(ResultCode),
        SysErrorMessage(ResultCode));
      Exit;
    end;
  finally
    DotNetRuntimeInstallPage.Hide;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    Result := FormatCustomMessage1(
      'RuntimeInstallFailed', IntToStr(ResultCode));
    Exit;
  end;

  if ResultCode = 3010 then
    NeedsRestart := True;

  if not IsDotNet8DesktopRuntimeInstalled then
  begin
    Result := CustomMessage('RuntimeNotDetected');
  end;
end;

function GetSelectedApplicationLanguage: String;
begin
  if CompareText(ActiveLanguage, 'english') = 0 then
    Result := 'en-US'
  else
    Result := 'zh-CN';
end;

procedure SaveInitialApplicationLanguage;
var
  SettingsFilePath: String;
  Language: String;
begin
  SettingsFilePath := AddBackslash(GetSelectedDataRoot('')) + SettingsFileName;
  if FileExists(SettingsFilePath) or FileExists(SettingsFilePath + '.bak') then
    Exit;

  Language := GetSelectedApplicationLanguage;
  if not SaveStringToFile(
    SettingsFilePath,
    '{"schemaVersion":2,"payload":{"language":"' + Language + '"}}',
    False) then
    Log('Unable to save initial SuperCV language settings: ' + SettingsFilePath)
  else
    Log('Saved initial SuperCV language: ' + Language);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveInitialApplicationLanguage;
end;
