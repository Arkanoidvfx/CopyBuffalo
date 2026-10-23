#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId=ArkCopyBuffer
AppName=ArkCopyBuffer
#ifdef FullPack
OutputBaseFilename=ArkCopyBuffer-Full-Setup
#else
OutputBaseFilename=ArkCopyBuffer-Setup
#endif
DefaultDirName={localappdata}\Programs\ArkCopyBuffer
AppVersion={#AppVersion}
AppPublisher=Arkanoidvfx
AppPublisherURL=https://github.com/Arkanoidvfx/CopyBuffalo
AppMutex=Local\ArkCopyBuffer
SetupMutex=ArkCopyBuffer-Setup
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
DefaultGroupName=ArkCopyBuffer
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
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef FullPack
Source: "{#AiDir}\*"; DestDir: "{app}\ai"; Excludes: "models\*"; Components: ai; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#AiDir}\models\*.gguf"; DestDir: "{app}\ai\models"; Components: ai; Flags: ignoreversion nocompression

[Types]
Name: "full"; Description: "Полная установка"
Name: "custom"; Description: "Выбор компонентов"; Flags: iscustom

[Components]
Name: "core"; Description: "История буфера и извлечение звука"; Types: full custom; Flags: fixed
Name: "ai"; Description: "Сжатие промптов: модель и CPU/NVIDIA-сервер"; Types: full custom
#endif

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"
Name: "autostart"; Description: "Запускать вместе с Windows"

[Icons]
Name: "{userprograms}\ArkCopyBuffer"; Filename: "{app}\ArkCopyBuffer.exe"; WorkingDir: "{app}"; Check: not IsUpdate
Name: "{userdesktop}\ArkCopyBuffer"; Filename: "{app}\ArkCopyBuffer.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not IsUpdate

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ArkCopyBuffer"; ValueData: """{app}\ArkCopyBuffer.exe"""; Tasks: autostart; Check: not IsUpdate

[Run]
Filename: "{app}\ArkCopyBuffer.exe"; Description: "Запустить ArkCopyBuffer"; Flags: nowait postinstall skipifsilent

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
    MsgBox('Поддерживаются только Windows 10 и Windows 11 (Intel/AMD x64).', mbError, MB_OK);
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
