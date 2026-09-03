; Script generated for Inno Setup - RhythmHub Distribution Installer
#define MyAppName "RhythmHub"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "RhythmHub Team"
#define MyAppExeName "RhythmHub.exe"
#define MyAppIcon "Assets\AppLogo.ico"

[Setup]
AppId={{8B036D89-7050-4E87-8A7B-E56525164B62}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#MyAppIcon}
OutputDir=dist\installer
OutputBaseFilename=RhythmHubSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "vigembus"; Description: "Virtual Controller Driver (ViGEmBus) - Required ONLY for Xbox One guitar dongles to create a virtual Xbox 360 controller (Not needed for Wii/PS3 dongles)"; GroupDescription: "Prerequisites & Drivers:"; Check: NotIsViGEmBusInstalled

[Files]
Source: "dist\staged\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "prerequisites\*"; DestDir: "{app}\prerequisites"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIcon}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIcon}"; Tasks: desktopicon

[Run]
Filename: "{app}\prerequisites\ViGEmBus_1.22.0_x64_x86_arm64.exe"; Parameters: "/passive /norestart"; StatusMsg: "Installing Virtual Controller Driver (ViGEmBus)..."; Tasks: vigembus; Flags: runascurrentuser
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Helper function to check if the ViGEmBus service registry entry is already installed
function IsViGEmBusInstalled: Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Services\ViGEmBus');
end;

// Inverted helper function for Inno Setup task checking (returns True if driver is MISSING)
function NotIsViGEmBusInstalled: Boolean;
begin
  Result := not IsViGEmBusInstalled;
end;

procedure InitializeWizard;
begin
  if IsViGEmBusInstalled then
    Log('ViGEmBus driver service detected on this machine.')
  else
    Log('ViGEmBus driver service not detected. Pre-selecting driver installation task.');
end;
