#ifndef PackageDir
  #error PackageDir must point to the verified Windows publish directory
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "0.1.0"
#endif

[Setup]
AppId={{6A6F99ED-09D4-44B1-AE8D-C58EF7673300}
AppName=RollClip Rust
AppVersion={#ReleaseVersion}
AppPublisher=RollClip
AppPublisherURL=https://github.com/wozeky/RollClip
AppSupportURL=https://github.com/wozeky/RollClip/issues
AppUpdatesURL=https://github.com/wozeky/RollClip/releases
DefaultDirName={localappdata}\Programs\RollClip Rust
DefaultGroupName=RollClip Rust
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename=RollClip-Rust-Setup-win-x64
UninstallDisplayIcon={app}\RollClip-Rust.exe
AppMutex=Local\RollClipRust.Desktop
CloseApplications=yes
#ifdef SignedBuild
SignTool=rollclip
SignedUninstaller=yes
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\RollClip Rust"; Filename: "{app}\RollClip-Rust.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\RollClip Rust"; Filename: "{app}\RollClip-Rust.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\RollClip-Rust.exe"; Description: "{cm:LaunchProgram,RollClip Rust}"; Flags: nowait postinstall skipifsilent
