; Compiled by scripts/build-installer.ps1, which supplies pinned runtime metadata.
[Setup]
AppId=NbaLive.Online
AppName=NBA 直播条
AppVersion={#AppVersion}
AppPublisher=james7whm
AppPublisherURL=https://github.com/james7whm/nba-live
AppSupportURL=https://gitee.com/james7whm/nba-live/releases
DefaultDirName={localappdata}\Programs\NbaLive
DefaultGroupName=NBA 直播条
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
WizardStyle=modern
DisableProgramGroupPage=yes
DisableWelcomePage=no
SetupIconFile={#SourceRoot}\src\NbaLive\Assets\Tray.ico
UninstallDisplayIcon={app}\NbaLive.exe
OutputDir={#SourceRoot}\artifacts
OutputBaseFilename=NbaLive-{#AppVersion}-online-setup-win-x64
VersionInfoVersion={#AppVersion}.0
Compression=lzma2/max
SolidCompression=yes
AppMutex=Local\NbaLive.Desktop
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: chinese; MessagesFile: "{#SourceRoot}\installer\ChineseSimplified.isl"

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NBA 直播条"; Filename: "{app}\NbaLive.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\NBA 直播条"; Filename: "{app}\NbaLive.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\NbaLive.exe"; Description: "打开 NBA 直播条"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;

function RuntimeMatches(Version: String): Boolean;
var
  Patch, Index: Integer;
  Suffix: String;
begin
  Result := False;
  if Copy(Version, 1, 5) <> '10.0.' then Exit;
  Suffix := Copy(Version, 6, Length(Version));
  if Length(Suffix) = 0 then Exit;
  for Index := 1 to Length(Suffix) do
    if (Suffix[Index] < '0') or (Suffix[Index] > '9') then Exit;
  Patch := StrToIntDef(Suffix, -1);
  Result := Patch >= {#RuntimePatch};
end;

function HasFramework(Name, DllName: String): Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
  Location: String;
begin
  Result := False;
  { The .NET host reads this x64 registration from the 32-bit registry view. }
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) then Exit;
  if not FileExists(AddBackslash(Location) + 'dotnet.exe') then Exit;
  if not RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + Name, Versions) then Exit;
  for Index := 0 to GetArrayLength(Versions) - 1 do
    if RuntimeMatches(Versions[Index]) and FileExists(AddBackslash(Location) + 'shared\' + Name + '\' + Versions[Index] + '\' + DllName) then begin
      Result := True;
      Exit;
    end;
end;

function HasRuntime: Boolean;
begin
  Result := HasFramework('Microsoft.WindowsDesktop.App', 'PresentationFramework.dll') and HasFramework('Microsoft.NETCore.App', 'System.Private.CoreLib.dll');
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  { Test-only invocation exits before installing anything; no runtime or registry changes. }
  if ExpandConstant('{param:RUNTIMESELFTEST|0}') = '1' then begin
    if not RuntimeMatches('10.0.{#RuntimePatch}') or not RuntimeMatches('10.0.999') or
       RuntimeMatches('9.0.999') or RuntimeMatches('11.0.0') or RuntimeMatches('10.0.0') or
       RuntimeMatches('10.0.999-preview') or RuntimeMatches('10.0.13.1') or RuntimeMatches('x10.0.999') then
      RaiseException('Runtime version self-test failed');
    Log('NBA_RUNTIME_TEST_PASS');
    if HasRuntime then Log('NBA_RUNTIME_PRESENT') else Log('NBA_RUNTIME_MISSING');
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('安装运行组件', '正在从微软官网下载并校验 .NET 10 桌面运行环境（x64）…', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  WizardForm.WelcomeLabel2.Caption := '本安装包不包含运行环境。已安装合适组件时无需联网；缺少时会征求同意后下载，组件安装可能需要管理员授权。' + #13#10#13#10 +
    '应用只安装到当前用户目录，不修改原有配置和收藏。Win7 请使用独立兼容包。';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if HasRuntime then begin Log('Compatible runtime found; no download.'); Exit; end;
  if WizardSilent then begin
    Result := '缺少 .NET 10 桌面运行环境（x64）。请交互运行安装包并确认下载，或先安装微软运行组件。';
    Exit;
  end;
  if MsgBox('首次运行需要 Microsoft .NET {#RuntimeVersion} 桌面运行环境（x64）。' + #13#10#13#10 +
    '是否从微软官网下载并打开安装器？仅需安装一次，可能需要管理员授权。' + #13#10 +
    '不便安装时，可从 GitHub 发行列表下载免安装完整版。', mbConfirmation, MB_YESNO) <> IDYES then begin
    Result := '已取消组件安装。尚未安装应用；可再次点击安装重试，或选择免安装完整版。';
    Exit;
  end;
  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime-{#RuntimeVersion}-win-x64.exe', '{#RuntimeSHA256}');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      Result := '组件下载或校验未完成：' + GetExceptionMessage + #13#10 + '可重试，或访问 https://dotnet.microsoft.com/download/dotnet/10.0 安装 Desktop Runtime x64。';
    end;
  finally
    DownloadPage.Hide;
  end;
  if Result <> '' then Exit;
  if not ShellExec('runas', ExpandConstant('{tmp}\windowsdesktop-runtime-{#RuntimeVersion}-win-x64.exe'), '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then begin
    Result := '无法启动组件安装器，或管理员授权已取消。应用尚未安装，可重试。';
    Exit;
  end;
  if Code = 3010 then begin
    NeedsRestart := True;
    Result := '运行组件需要重启电脑。请保存工作、重启后重新运行本安装包；不会自动重启。';
    Exit;
  end;
  if (Code <> 0) or not HasRuntime then
    Result := Format('运行组件尚未安装完成（返回码 %d）。请重试或使用安装帮助；应用文件尚未写入。', [Code]);
end;
