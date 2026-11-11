#ifndef PackageDir
  #error PackageDir must point to the verified Windows publish directory
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "0.4.6"
#endif

[Setup]
AppId={{E8123E32-E090-4A2A-8D8E-57F249584FD8}
AppName=RollClip
AppVersion={#ReleaseVersion}
AppPublisher=RollClip
AppPublisherURL=https://github.com/wozeky/RollClip
AppSupportURL=https://github.com/wozeky/RollClip/issues
AppUpdatesURL=https://github.com/wozeky/RollClip/releases
DefaultDirName={localappdata}\Programs\RollClip
DefaultGroupName=RollClip
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename=RollClip-Setup-win-x64
UninstallDisplayIcon={app}\RollClip.exe
AppMutex=Local\ClipBridge.Desktop
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
Name: "{autoprograms}\RollClip"; Filename: "{app}\RollClip.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\RollClip"; Filename: "{app}\RollClip.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\RollClip.exe"; Description: "{cm:LaunchProgram,RollClip}"; Flags: nowait postinstall skipifsilent
