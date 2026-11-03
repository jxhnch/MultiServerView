; Inno Setup script. Build with:
;   iscc /DAppVersion=1.0.0 /DSourceDir=..\publish installer\MultiServerView.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6E0B5C5E-6C0B-4B2E-9B63-5F1C2D7A9E41}
AppName=MultiServerView
AppVersion={#AppVersion}
AppPublisher=jxhnch
AppPublisherURL=https://github.com/jxhnch/MultiServerView
DefaultDirName={autopf}\MultiServerView
DefaultGroupName=MultiServerView
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=MultiServerView-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
SetupIconFile=..\src\MultiServerView.App\Assetspp.ico
UninstallDisplayIcon={app}\MultiServerView.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MultiServerView"; Filename: "{app}\MultiServerView.exe"
Name: "{autodesktop}\MultiServerView"; Filename: "{app}\MultiServerView.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MultiServerView.exe"; Description: "Start MultiServerView"; Flags: nowait postinstall skipifsilent
