#ifndef AppVersion
  #define AppVersion "0.2.3"
#endif

[Setup]
AppId=ArkCopyBuffer
AppName=CopyBuffalo
#ifdef FullPack
OutputBaseFilename=ArkCopyBuffer-Full-Setup
#else
OutputBaseFilename=ArkCopyBuffer-Setup
#endif
DefaultDirName={localappdata}\Programs\ArkCopyBuffer
AppVersion={#AppVersion}
AppPublisher=Arkanoidvfx
AppPublisherURL=https://github.com/Arkanoidvfx/CopyBuffalo
LicenseFile=..\LICENSE
AppMutex=Local\ArkCopyBuffer
SetupMutex=ArkCopyBuffer-Setup
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
DefaultGroupName=CopyBuffalo
DisableProgramGroupPage=yes
SetupIconFile=..\Assets\App.ico
UninstallDisplayIcon={app}\ArkCopyBuffer.exe
Compression=lzma2/fast
SolidCompression=no
DiskSpanning=no
OutputDir={#OutputDir}
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.FullInstall=Full installation
russian.FullInstall=Полная установка
english.CustomInstall=Choose components
russian.CustomInstall=Выбор компонентов
english.CoreComponent=Clipboard history, OCR, and media tools
russian.CoreComponent=История буфера, OCR и работа с медиа
english.AiComponent=Local AI: model and CPU/NVIDIA runtime
russian.AiComponent=Сжатие промптов: модель и CPU/NVIDIA-сервер
english.DesktopShortcut=Create a desktop shortcut
russian.DesktopShortcut=Создать ярлык на рабочем столе
english.Autostart=Start with Windows
russian.Autostart=Запускать вместе с Windows
english.Launch=Launch CopyBuffalo
russian.Launch=Запустить CopyBuffalo
english.UnsupportedSystem=Only Windows 10 and Windows 11 (Intel/AMD x64) are supported.
russian.UnsupportedSystem=Поддерживаются только Windows 10 и Windows 11 (Intel/AMD x64).

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef FullPack
Source: "{#AiDir}\*"; DestDir: "{app}\ai"; Excludes: "models\*"; Components: ai; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#AiDir}\models\*.gguf"; DestDir: "{app}\ai\models"; Components: ai; Flags: ignoreversion nocompression

[Types]
Name: "full"; Description: "{cm:FullInstall}"
Name: "custom"; Description: "{cm:CustomInstall}"; Flags: iscustom

[Components]
Name: "core"; Description: "{cm:CoreComponent}"; Types: full custom; Flags: fixed
Name: "ai"; Description: "{cm:AiComponent}"; Types: full custom
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"
Name: "autostart"; Description: "{cm:Autostart}"

[Icons]
Name: "{userprograms}\CopyBuffalo"; Filename: "{app}\ArkCopyBuffer.exe"; WorkingDir: "{app}"; Check: not IsUpdate
Name: "{userdesktop}\CopyBuffalo"; Filename: "{app}\ArkCopyBuffer.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not IsUpdate

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ArkCopyBuffer"; ValueData: """{app}\ArkCopyBuffer.exe"""; Tasks: autostart; Check: not IsUpdate

[Run]
Filename: "{app}\ArkCopyBuffer.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[Code]
function IsUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') <> '0';
end;

function InitializeSetup(): Boolean;
var
  V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Result := V.ProductType = 1;
  if not Result then
    MsgBox(CustomMessage('UnsupportedSystem'), mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ArkCopyBuffer', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\ArkCopyBuffer.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ArkCopyBuffer');
end;
