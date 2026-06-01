; Inno Setup script for Wallhaven Fetcher Windows installer.
; Usage:
;   iscc /DVersion=0.1.0 installer\WallhavenFetcher.iss
; Output:
;   installer\Output\WallhavenFetcherSetup-0.1.0.exe

#define MyAppName      "Wallhaven Fetcher"
#define MyAppPublisher "xyanwert"
#define MyAppURL       "https://github.com/xyanwert/wallhaven-fetcher-windows"
#define MyAppExeName   "WallhavenFetcher.exe"
#ifndef Version
  #define Version "0.1.0"
#endif

[Setup]
AppId={{2C7E6F1B-9C04-4D7B-8F8A-3E2C4B1A9D7E}
AppName={#MyAppName}
AppVersion={#Version}
AppVerName={#MyAppName} {#Version}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\WallhavenFetcher
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=WallhavenFetcherSetup-{#Version}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start Wallhaven Fetcher when I log in"; \
  GroupDescription: "Auto-launch"; Flags: checkedonce

[Files]
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; If we ever ship loose assets alongside the single-file exe, add them here.

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
  Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Leave %APPDATA%\WallhavenFetcher in place — user data (state, config, key)
; belongs to the user, not the install. To purge, they delete manually.
