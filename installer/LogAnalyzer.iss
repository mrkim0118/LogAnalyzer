#define AppVersion "1.0.18"

[Setup]
AppId={{AA141AD6-CBEE-46DE-9238-E104093D2FAC}
AppName=LogAnalyzer
AppVersion={#AppVersion}
VersionInfoProductName=LogAnalyzer
VersionInfoDescription=LogAnalyzer Setup
VersionInfoVersion=1.0.18.0
DefaultDirName={localappdata}\Programs\LogAnalyzer
DefaultGroupName=LogAnalyzer
PrivilegesRequired=lowest
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=LogAnalyzer-Setup
SetupIconFile=..\assets\LogAnalyzer.ico
UninstallDisplayIcon={app}\LogAnalyzer.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
DisableDirPage=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면 바로가기 만들기"; GroupDescription: "바로가기:"

[InstallDelete]
Type: files; Name: "{userprograms}\TAURUS 로그 분석기.lnk"; Check: FileExists(ExpandConstant('{app}\TaurusLogAnalyzer.exe'))
Type: files; Name: "{userdesktop}\TAURUS 로그 분석기.lnk"; Check: FileExists(ExpandConstant('{app}\TaurusLogAnalyzer.exe'))
Type: files; Name: "{app}\TaurusLogAnalyzer.exe.config"
Type: files; Name: "{app}\TaurusLogAnalyzer.exe"

[Files]
Source: "..\bin\Release\LogAnalyzer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\LogAnalyzer.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\timing-keywords.ini"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{userprograms}\LogAnalyzer"; Filename: "{app}\LogAnalyzer.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\LogAnalyzer"; Filename: "{app}\LogAnalyzer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LogAnalyzer.exe"; Description: "LogAnalyzer 실행"; Flags: nowait postinstall skipifsilent

[Code]
// 설치 전에 .NET Framework 4.8 이상이 있는지 확인한다.
function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox('.NET Framework 4.8 이상을 설치한 후 다시 실행해 주세요.', mbError, MB_OK, IDOK);
end;
