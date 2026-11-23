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
ChangesEnvironment=yes
SetupIconFile=..\src\MultiServerView.App\Assets\app.ico
UninstallDisplayIcon={app}\MultiServerView.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "addtopath"; Description: "Add the command &msv to the PATH (open your servers from any terminal: msv)"; GroupDescription: "Command line:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MultiServerView"; Filename: "{app}\MultiServerView.exe"
Name: "{autodesktop}\MultiServerView"; Filename: "{app}\MultiServerView.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath(ExpandConstant('{app}')); Tasks: addtopath

[Run]
Filename: "{app}\MultiServerView.exe"; Description: "Start MultiServerView"; Flags: nowait postinstall skipifsilent
Filename: "{app}\MultiServerView.exe"; Flags: nowait skipifnotsilent; Check: ShouldRelaunch

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

{ true = the folder is not in the PATH of the user yet }
function NeedsAddPath(Dir: string): Boolean;
var
  Existing: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Existing) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Existing) + ';') = 0;
end;

{ takes the folder out of the PATH again when the program is removed }
procedure RemoveFromPath(Dir: string);
var
  Existing: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Existing) then exit;
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Existing) + ';');
  if P = 0 then exit;
  Delete(Existing, P, Length(Dir) + 1);
  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Existing);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveFromPath(ExpandConstant('{app}'));
end;
