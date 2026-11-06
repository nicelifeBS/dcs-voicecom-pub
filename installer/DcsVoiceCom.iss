; Inno Setup script for DCS VoiceCom. Build it with installer\build.ps1, which publishes the app first.
; Compile by hand: iscc /DAppVersion=0.1.0-beta.5 /DPublishDir=..\dist\DcsVoiceCom installer\DcsVoiceCom.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist\DcsVoiceCom"
#endif

#define AppName "DCS VoiceCom"
#define AppExe "DcsVoiceCom.exe"

[Setup]
AppId={{6F1B6C2E-4C1D-4E8A-9B4B-2D6C0E5A7F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DCS VoiceCom
DefaultDirName={autopf}\DcsVoiceCom
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the user can pick "all users" instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=DcsVoiceCom-Setup-{#AppVersion}
SetupIconFile=..\src\DcsVoiceCom.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Same mutex the app holds, so setup asks to close a running app.
AppMutex=DcsVoiceCom.SingleInstance
CloseApplications=yes
; Sample profiles go to the installing user's %LOCALAPPDATA%, also in an all-users install.
UsedUserAreasWarning=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "profiles"; Description: "Install sample profiles (F-14B Upgraded, common)"; GroupDescription: "Profiles:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Sample profiles go where the app looks by default. Existing profiles are never overwritten.
Source: "..\profiles\*.json"; DestDir: "{localappdata}\DcsVoiceCom\profiles"; Tasks: profiles; Flags: onlyifdoesntexist uninsneveruninstall
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Settings, profiles, logs and a downloaded model in %LOCALAPPDATA%\DcsVoiceCom stay after uninstall.
