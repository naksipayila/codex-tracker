#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{A5D8D3B2-4E2A-4C64-B6A1-4CE9B1A3AF4D}
AppName=Codex Tracker
AppVersion={#AppVersion}
AppVerName=Codex Tracker {#AppVersion}
AppPublisher=Naksi Payila
AppPublisherURL=https://github.com/naksipayila/codex-tracker
AppSupportURL=https://github.com/naksipayila/codex-tracker/issues
DefaultDirName={localappdata}\Programs\Codex Tracker
DefaultGroupName=Codex Tracker
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Codex Tracker Setup
SetupIconFile={#SourcePath}\..\src\launcher\icon.ico
UninstallDisplayIcon={app}\Codex Tracker.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=no
VersionInfoCompany=Naksi Payila
VersionInfoDescription=Codex Tracker installer
VersionInfoProductName=Codex Tracker
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (c) Naksi Payila

[Tasks]
Name: "startup"; Description: "Launch Codex Tracker when I sign in to Windows"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\Codex Tracker.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Codex Tracker"; Filename: "{app}\Codex Tracker.exe"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexUsageTray"; ValueData: "{app}\Codex Tracker.exe"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Codex Tracker.exe"; Description: "Launch Codex Tracker"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
