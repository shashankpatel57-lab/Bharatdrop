#define MyAppName "MicBridge Studio"
#define MyAppVersion "2.2.0"
#define MyAppPublisher "MicBridge"
#define MyAppExeName "MicBridge Studio.exe"

[Setup]
AppId={{C3C7DCA9-96E8-4A59-8A4C-5B41A6D2F2E2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\MicBridge Studio
DefaultGroupName=MicBridge Studio
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=MicBridge_Studio_Setup_v2.2
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\\..\\publish\\MicBridge_Studio.exe"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MicBridge Studio"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\MicBridge Studio"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch MicBridge Studio"; Flags: nowait postinstall skipifsilent
