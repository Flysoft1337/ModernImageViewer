; Build through scripts/build-installer.ps1 with Inno Setup 7.1.0.
#ifndef AppVersion
  #error AppVersion must be supplied by the build script
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be supplied by the build script
#endif
#ifndef InstallerOutputDirectory
  #error InstallerOutputDirectory must be supplied by the build script
#endif

[Setup]
AppId={{A2E8F5A0-6526-4C87-ADE1-04DE4A7F41C0}
AppName=Modern Image Viewer
AppVersion={#AppVersion}
AppPublisher=Flysoft1337
AppPublisherURL=https://github.com/Flysoft1337/ModernImageViewer
AppSupportURL=https://github.com/Flysoft1337/ModernImageViewer/issues
AppUpdatesURL=https://github.com/Flysoft1337/ModernImageViewer
DefaultDirName={localappdata}\Programs\ModernImageViewer
DefaultGroupName=Modern Image Viewer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
WizardStyle=modern dynamic windows11
WizardSizePercent=115
ShowLanguageDialog=auto
OutputDir={#InstallerOutputDirectory}
OutputBaseFilename=ModernImageViewer-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\ModernImageViewer.App.exe
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.Associations=Add to Open with for supported image formats
english.DesktopShortcut=Create a desktop shortcut
english.OpenViewer=Open Modern Image Viewer
english.UninstallViewer=Uninstall Modern Image Viewer
english.AssociationCollision=The Open with registration belongs to another application. Clear the Open with option to continue installing without changing that registration.
chinesesimplified.Associations=加入支持的图片格式的“打开方式”候选
chinesesimplified.DesktopShortcut=创建桌面快捷方式
chinesesimplified.OpenViewer=打开 Modern Image Viewer
chinesesimplified.UninstallViewer=卸载 Modern Image Viewer
chinesesimplified.AssociationCollision=此“打开方式”注册项已被其他应用占用。请取消打开方式选项，再继续安装；现有注册将被保留。

[Tasks]
Name: "fileassoc"; Description: "{cm:Associations}"
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "ModernImageViewer.install.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Modern Image Viewer"; Filename: "{app}\ModernImageViewer.App.exe"
Name: "{group}\{cm:UninstallViewer}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Modern Image Viewer"; Filename: "{app}\ModernImageViewer.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ModernImageViewer.App.exe"; Description: "{cm:OpenViewer}"; Flags: nowait postinstall skipifsilent

[Code]
const
  AppKey = 'Software\ModernImageViewer\Installed';
  CapabilitiesKey = 'Software\ModernImageViewer\Installed\Capabilities';
  ProgId = 'ModernImageViewer.Installed.Image';
  ProgIdKey = 'Software\Classes\ModernImageViewer.Installed.Image';
  ApplicationId = 'ModernImageViewer.Installed';
  ApplicationName = 'Modern Image Viewer';
  Owner = 'ModernImageViewer.Installed.v1';
  ImageDescription = 'Modern Image Viewer image';
  ApplicationDescription = 'Browse supported images with Modern Image Viewer.';
  LegacyApplicationDescription = 'Browse JPEG and PNG images with Modern Image Viewer.';
  RegisteredApplicationsKey = 'Software\RegisteredApplications';
  KeyReadWrite64 = $2011F;

function NativeRegCreateKeyEx(Root: NativeUInt; SubKey: String; Reserved: Cardinal;
  ClassName: NativeUInt; Options, Access: Cardinal; Security: NativeUInt;
  var Key: NativeUInt; var Disposition: Cardinal): Integer;
  external 'RegCreateKeyExW@advapi32.dll stdcall';
function NativeRegOpenKeyEx(Root: NativeUInt; SubKey: String; Options, Access: Cardinal;
  var Key: NativeUInt): Integer;
  external 'RegOpenKeyExW@advapi32.dll stdcall';
function NativeRegSetValueEx(Key: NativeUInt; ValueName: String; Reserved, ValueType: Cardinal;
  Data: NativeUInt; DataSize: Cardinal): Integer;
  external 'RegSetValueExW@advapi32.dll stdcall';
function NativeRegQueryValueEx(Key: NativeUInt; ValueName: String; Reserved: NativeUInt;
  var ValueType: Cardinal; Data: NativeUInt; var DataSize: Cardinal): Integer;
  external 'RegQueryValueExW@advapi32.dll stdcall';
function NativeRegCloseKey(Key: NativeUInt): Integer;
  external 'RegCloseKey@advapi32.dll stdcall';
procedure NotifyAssociations(Event: Cardinal; Flags: Cardinal; Item1, Item2: NativeUInt);
  external 'SHChangeNotify@shell32.dll stdcall';

function ExecutablePath(): String;
begin
  Result := ExpandConstant('{app}\ModernImageViewer.App.exe');
end;

function OpenCommand(): String;
begin
  Result := '"' + ExecutablePath() + '" "%1"';
end;

function IconPath(): String;
begin
  Result := '"' + ExecutablePath() + '",0';
end;

function ReadMatching(KeyPath, Name, Expected: String): Boolean;
var
  Value: String;
begin
  Result := RegQueryStringValue(HKCU, KeyPath, Name, Value) and (Value = Expected);
end;

function PrivateKeyAvailable(KeyPath: String): Boolean;
begin
  Result := not RegKeyExists(HKCU, KeyPath) or ReadMatching(KeyPath, 'Owner', Owner);
end;

function AssociationIdentityAvailable(): Boolean;
var
  Value: String;
begin
  Result := PrivateKeyAvailable(AppKey) and PrivateKeyAvailable(ProgIdKey);
  if RegValueExists(HKCU, RegisteredApplicationsKey, ApplicationId) then
    Result := Result and RegQueryStringValue(HKCU, RegisteredApplicationsKey, ApplicationId, Value)
      and (Value = CapabilitiesKey);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsTaskSelected('fileassoc') and not AssociationIdentityAvailable() then
    Result := CustomMessage('AssociationCollision');
end;

procedure WriteString(KeyPath, Name, Value: String);
begin
  if not RegWriteStringValue(HKCU, KeyPath, Name, Value) then
    RaiseException('Could not register Open with: ' + KeyPath);
end;

procedure WriteEmptyCandidate(KeyPath: String);
var
  Key: NativeUInt;
  Disposition: Cardinal;
begin
  { REG_NONE with zero bytes, exactly as the application association service writes. }
  if NativeRegCreateKeyEx(NativeUInt(NativeInt(-2147483647)), KeyPath, 0, 0, 0,
    KeyReadWrite64, 0, Key, Disposition) <> 0 then
    RaiseException('Could not create Open with candidates.');
  try
    if NativeRegSetValueEx(Key, ProgId, 0, 0, 0, 0) <> 0 then
      RaiseException('Could not register Open with candidate.');
  finally
    NativeRegCloseKey(Key);
  end;
end;

function IsEmptyCandidate(KeyPath: String): Boolean;
var
  Key: NativeUInt;
  ValueType, DataSize: Cardinal;
begin
  Result := False;
  if NativeRegOpenKeyEx(NativeUInt(NativeInt(-2147483647)), KeyPath, 0,
    $20119, Key) <> 0 then exit;
  try
    DataSize := 0;
    Result := (NativeRegQueryValueEx(Key, ProgId, 0, ValueType, 0, DataSize) = 0)
      and (ValueType = 0) and (DataSize = 0);
  finally
    NativeRegCloseKey(Key);
  end;
end;

procedure RegisterExtension(Extension: String);
begin
  WriteString(CapabilitiesKey + '\FileAssociations', Extension, ProgId);
  WriteEmptyCandidate('Software\Classes\' + Extension + '\OpenWithProgids');
end;

procedure RegisterAssociations();
begin
  { Recheck immediately before writing; never adopt an unrelated private identity. }
  if not AssociationIdentityAvailable() then
    RaiseException(CustomMessage('AssociationCollision'));
  WriteString(AppKey, 'Owner', Owner);
  WriteString(AppKey, 'ExecutablePath', ExecutablePath());
  WriteString(ProgIdKey, 'Owner', Owner);
  WriteString(ProgIdKey, '', ImageDescription);
  WriteString(ProgIdKey + '\DefaultIcon', '', IconPath());
  WriteString(ProgIdKey + '\shell\open\command', '', OpenCommand());
  WriteString(CapabilitiesKey, 'ApplicationName', ApplicationName);
  WriteString(CapabilitiesKey, 'ApplicationDescription', ApplicationDescription);
  WriteString(CapabilitiesKey, 'ApplicationIcon', IconPath());
  RegisterExtension('.jpg');
  RegisterExtension('.jpeg');
  RegisterExtension('.png');
  RegisterExtension('.bmp');
  RegisterExtension('.gif');
  RegisterExtension('.tif');
  RegisterExtension('.tiff');
  RegisterExtension('.ico');
  RegisterExtension('.webp');
  WriteString(RegisteredApplicationsKey, ApplicationId, CapabilitiesKey);
  NotifyAssociations($08000000, 0, 0, 0);
end;

procedure DeleteMatching(KeyPath, Name, Expected: String; PruneKey: Boolean);
begin
  if ReadMatching(KeyPath, Name, Expected) then RegDeleteValue(HKCU, KeyPath, Name);
  if PruneKey then RegDeleteKeyIfEmpty(HKCU, KeyPath);
end;

procedure RemovePrivateOwnerIfEmpty(KeyPath: String);
var
  Values, SubKeys: TArrayOfString;
begin
  if RegGetValueNames(HKCU, KeyPath, Values) and RegGetSubkeyNames(HKCU, KeyPath, SubKeys)
    and (GetArrayLength(Values) = 1) and (GetArrayLength(SubKeys) = 0)
    and ReadMatching(KeyPath, 'Owner', Owner) then
    RegDeleteValue(HKCU, KeyPath, 'Owner');
  RegDeleteKeyIfEmpty(HKCU, KeyPath);
end;

procedure UnregisterExtension(Extension: String);
var
  KeyPath: String;
begin
  KeyPath := 'Software\Classes\' + Extension + '\OpenWithProgids';
  if IsEmptyCandidate(KeyPath) then RegDeleteValue(HKCU, KeyPath, ProgId);
  DeleteMatching(CapabilitiesKey + '\FileAssociations', Extension, ProgId, True);
end;

procedure UnregisterAssociations();
var
  Value: String;
begin
  { A newer installation may have moved the stable identity. Do not remove its entries. }
  if not ReadMatching(AppKey, 'Owner', Owner) then exit;
  if not RegQueryStringValue(HKCU, AppKey, 'ExecutablePath', Value)
    or (CompareText(Value, ExecutablePath()) <> 0) then exit;
  { Missing entries can be repaired/removed; contradictory owners or commands cannot. }
  if RegValueExists(HKCU, ProgIdKey, 'Owner') and not ReadMatching(ProgIdKey, 'Owner', Owner) then exit;
  if RegValueExists(HKCU, ProgIdKey + '\shell\open\command', '')
    and not ReadMatching(ProgIdKey + '\shell\open\command', '', OpenCommand()) then exit;

  UnregisterExtension('.jpg');
  UnregisterExtension('.jpeg');
  UnregisterExtension('.png');
  UnregisterExtension('.bmp');
  UnregisterExtension('.gif');
  UnregisterExtension('.tif');
  UnregisterExtension('.tiff');
  UnregisterExtension('.ico');
  UnregisterExtension('.webp');
  DeleteMatching(RegisteredApplicationsKey, ApplicationId, CapabilitiesKey, False);
  DeleteMatching(ProgIdKey + '\shell\open\command', '', OpenCommand(), True);
  DeleteMatching(ProgIdKey + '\DefaultIcon', '', IconPath(), True);
  RegDeleteKeyIfEmpty(HKCU, ProgIdKey + '\shell\open');
  RegDeleteKeyIfEmpty(HKCU, ProgIdKey + '\shell');
  DeleteMatching(ProgIdKey, '', ImageDescription, False);
  RemovePrivateOwnerIfEmpty(ProgIdKey);
  DeleteMatching(CapabilitiesKey, 'ApplicationName', ApplicationName, True);
  DeleteMatching(CapabilitiesKey, 'ApplicationDescription', ApplicationDescription, True);
  DeleteMatching(CapabilitiesKey, 'ApplicationDescription', LegacyApplicationDescription, True);
  DeleteMatching(CapabilitiesKey, 'ApplicationIcon', IconPath(), True);
  DeleteMatching(AppKey, 'ExecutablePath', Value, False);
  RemovePrivateOwnerIfEmpty(AppKey);
  NotifyAssociations($08000000, 0, 0, 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and IsTaskSelected('fileassoc') then RegisterAssociations();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then UnregisterAssociations();
end;
