; FirstOption Revit MCP setup. Build it with scripts\build-installer.ps1, which fills installer\stage first.
;
; The setup does what scripts\install.ps1 does, without the .NET SDK and without a build:
;   1. Closes Revit (after the user saves) and stops the MCP server, because they lock the files.
;   2. Copies the server, the add-in for each Revit version, the pyRevit bridge and the skills.
;   3. Writes FirstOption.RevitMcp.addin for each Revit version.
;   4. Installs pyRevit with winget when it is missing, adds the bridge to pyRevit and turns on Routes.
;   5. Registers the MCP server in Claude Code and Codex when they are installed.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef RevitVersions
  #define RevitVersions "2020,2021,2022,2023,2024,2025,2026"
#endif
#define McpName "firstoption-revit"

[Setup]
AppId={{0F6B3C1E-7B8C-4E43-9E2E-5C1D9A7F4B21}
AppName=FirstOption Revit MCP
AppVersion={#AppVersion}
AppPublisher=First Option
DefaultDirName={localappdata}\First Option\RevitMCP
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no
OutputDir=Output
OutputBaseFilename=FirstOption-RevitMCP-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName=FirstOption Revit MCP

[Files]
Source: "stage\Server\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\Revit Add-in\*"; DestDir: "{app}\Revit Add-in"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\pyRevit Bridge\*"; DestDir: "{app}\pyRevit Bridge"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\FirstOption.RevitMcp.addin"; DestDir: "{tmp}"; Flags: dontcopy
Source: "codex-config.ps1"; DestDir: "{app}\Setup"; Flags: ignoreversion
; Claude Code and Codex read skills only from their own folders.
Source: "stage\skills\*"; DestDir: "{%USERPROFILE}\.claude\skills"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\skills\*"; DestDir: "{%USERPROFILE}\.codex\skills"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Old files from an earlier version must not stay next to the new ones.
Type: filesandordirs; Name: "{app}\Server"
Type: filesandordirs; Name: "{app}\Revit Add-in"
Type: filesandordirs; Name: "{app}\pyRevit Bridge"

[Dirs]
; The saved commands, the activity log and the settings belong to the user; the uninstaller keeps them.
Name: "{app}\Command Library\FirstOptionLibrary.extension\FO Library.tab\Commands.panel"; Flags: uninsneveruninstall

[Run]
Filename: "{code:NewestRevitExe}"; Description: "Start Revit"; Flags: postinstall nowait skipifsilent; Check: HasRevit

[Code]
var
  Report: String;

function Quote(const S: String): String;
begin
  Result := '"' + S + '"';
end;

procedure AddReport(const Line: String);
begin
  Report := Report + #13#10 + '• ' + Line;
  Log(Line);
end;

// Runs a command line through cmd, so that .cmd tools (npm installs of claude and codex) and the PATH work.
function RunCmd(const CommandLine: String): Integer;
var
  Code: Integer;
  OutFile: String;
  Output: AnsiString;
begin
  // Keep the output of the command in the setup log, so that a failed step shows its error.
  OutFile := ExpandConstant('{tmp}\cmd-output.txt');
  DeleteFile(OutFile);
  if Pos('>nul', CommandLine) > 0 then
  begin
    if not Exec(ExpandConstant('{cmd}'), '/S /C "' + CommandLine + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Code := -1;
  end
  else if not Exec(ExpandConstant('{cmd}'), '/S /C "' + CommandLine + ' > "' + OutFile + '" 2>&1"', '', SW_HIDE,
    ewWaitUntilTerminated, Code) then
    Code := -1;
  Log('cmd: ' + CommandLine + ' -> ' + IntToStr(Code));
  if LoadStringFromFile(OutFile, Output) and (Output <> '') then
    Log('  output: ' + String(Output));
  Result := Code;
end;

function ExpandVars(const S: String): String;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := S;
  Names := ['USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramData', 'SystemRoot', 'windir'];
  for I := 0 to GetArrayLength(Names) - 1 do
    StringChangeEx(Result, '%' + Names[I] + '%', GetEnv(Names[I]), True);
end;

// Setup keeps the PATH it started with, so "where" misses a tool installed after Explorer started (or by winget
// during this setup). Look in each folder of the PATH in the registry too.
function FindTool(const Name: String): String;
var
  Paths, Dir, UserPath, MachinePath: String;
  P: Integer;
begin
  Result := '';
  if RunCmd('where ' + Name + ' >nul 2>nul') = 0 then
  begin
    Result := Name;
    Exit;
  end;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', UserPath) then UserPath := '';
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', MachinePath) then
    MachinePath := '';
  Paths := UserPath + ';' + MachinePath + ';';
  while Paths <> '' do
  begin
    P := Pos(';', Paths);
    Dir := Trim(ExpandVars(Copy(Paths, 1, P - 1)));
    Delete(Paths, 1, P);
    if Dir = '' then Continue;
    if FileExists(AddBackslash(Dir) + Name + '.exe') then Result := Quote(AddBackslash(Dir) + Name + '.exe')
    else if FileExists(AddBackslash(Dir) + Name + '.cmd') then Result := Quote(AddBackslash(Dir) + Name + '.cmd');
    if Result <> '' then Exit;
  end;
end;

function ProcessRuns(const ImageName: String): Boolean;
var
  Locator, Service, Found: Variant;
begin
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + ImageName + '''');
    Result := Found.Count > 0;
  except
    Result := False;
  end;
  Log('process ' + ImageName + ' runs: ' + IntToStr(Ord(Result)));
end;

function NewestRevitExe(Param: String): String;
var
  Year: Integer;
  Exe: String;
begin
  Result := '';
  for Year := 2030 downto 2020 do
  begin
    Exe := ExpandConstant('{commonpf64}\Autodesk\Revit ') + IntToStr(Year) + '\Revit.exe';
    if FileExists(Exe) then
    begin
      Result := Exe;
      Exit;
    end;
  end;
end;

function HasRevit: Boolean;
begin
  Result := NewestRevitExe('') <> '';
end;

function FindFirst(const Tool, Exe1, Exe2: String): String;
begin
  Result := FindTool(Tool);
  if (Result = '') and FileExists(Exe1) then Result := Quote(Exe1);
  if (Result = '') and (Exe2 <> '') and FileExists(Exe2) then Result := Quote(Exe2);
end;

function FindPyRevit: String;
begin
  Result := FindFirst('pyrevit', ExpandConstant('{commonpf64}\pyRevit-Master\bin\pyrevit.exe'),
    ExpandConstant('{userappdata}\pyRevit-Master\bin\pyrevit.exe'));
end;

function FindClaude: String;
begin
  Result := FindFirst('claude', ExpandConstant('{%USERPROFILE}\.local\bin\claude.exe'), '');
end;

function FindCodex: String;
begin
  Result := FindFirst('codex', ExpandConstant('{localappdata}\Programs\OpenAI\Codex\bin\codex.exe'), '');
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not HasRevit then
    Result := MsgBox('No Revit was found on this computer. The MCP works only with Revit 2020-2026.' + #13#10#13#10 +
      'Install anyway?', mbConfirmation, MB_YESNO) = IDYES;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Waited: Integer;
begin
  Result := '';
  if ProcessRuns('Revit.exe') then
  begin
    if MsgBox('Revit is open. Setup must close it.' + #13#10#13#10 +
      'Click OK to close Revit. Revit asks you to save your changes.', mbInformation, MB_OKCANCEL) <> IDOK then
    begin
      Result := 'Setup stopped because Revit is open. Run setup again when you are ready to close Revit.';
      Exit;
    end;
    RunCmd('taskkill /IM Revit.exe >nul 2>nul');
    repeat
      Waited := 0;
      while ProcessRuns('Revit.exe') and (Waited < 60) do
      begin
        Sleep(1000);
        Waited := Waited + 1;
      end;
      if not ProcessRuns('Revit.exe') then Break;
      if MsgBox('Revit is still open. It can show a Save dialog.' + #13#10#13#10 +
        'Answer the dialog in Revit, then click Retry.', mbError, MB_RETRYCANCEL) <> IDRETRY then
      begin
        Result := 'Setup stopped because Revit is still open.';
        Exit;
      end;
      RunCmd('taskkill /IM Revit.exe >nul 2>nul');
    until False;
  end;
  // Claude Code and Codex start the server again on the next session; stopping it loses nothing.
  RunCmd('taskkill /IM FirstOption.RevitMcp.exe /F >nul 2>nul');
end;

function XmlEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '&', '&amp;', True);
  StringChangeEx(Result, '<', '&lt;', True);
  StringChangeEx(Result, '>', '&gt;', True);
end;

procedure WriteManifests;
var
  Template: AnsiString;
  Text, Version, Versions, Dir, Dll: String;
  Lines: TArrayOfString;
  P: Integer;
begin
  ExtractTemporaryFile('FirstOption.RevitMcp.addin');
  LoadStringFromFile(ExpandConstant('{tmp}\FirstOption.RevitMcp.addin'), Template);
  Versions := '{#RevitVersions},';
  while Versions <> '' do
  begin
    P := Pos(',', Versions);
    Version := Copy(Versions, 1, P - 1);
    Delete(Versions, 1, P);
    Dll := ExpandConstant('{app}\Revit Add-in\') + Version + '\FirstOption.RevitMcp.Addin.dll';
    Dir := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\') + Version;
    Text := String(Template);
    StringChangeEx(Text, 'FirstOption.RevitMcp\FirstOption.RevitMcp.Addin.dll', XmlEscape(Dll), True);
    ForceDirectories(Dir);
    SetArrayLength(Lines, 1);
    Lines[0] := Text;
    // UTF-8, so that a user name with non-English letters stays correct in the path.
    if not SaveStringsToUTF8File(Dir + '\FirstOption.RevitMcp.addin', Lines, False) then
      AddReport('Revit ' + Version + ': the add-in manifest was not written.');
  end;
  AddReport('Revit add-in installed for Revit {#RevitVersions}.');
end;

procedure SetUpPyRevit;
var
  PyRevit: String;
begin
  PyRevit := FindPyRevit;
  if PyRevit = '' then
  begin
    WizardForm.StatusLabel.Caption := 'Installing pyRevit (this can take a few minutes)...';
    if FindTool('winget') <> '' then
      RunCmd(FindTool('winget') + ' install --id pyRevit.pyRevit -e --silent --accept-package-agreements --accept-source-agreements');
    PyRevit := FindPyRevit;
    if PyRevit = '' then
    begin
      AddReport('pyRevit is not installed, so Revit cannot talk to the MCP. Install pyRevit from ' +
        'https://github.com/pyrevitlabs/pyRevit/releases, then run this setup again.');
      Exit;
    end;
    AddReport('pyRevit installed.');
  end;
  WizardForm.StatusLabel.Caption := 'Setting up pyRevit...';
  if (RunCmd(PyRevit + ' extensions paths add ' + Quote(ExpandConstant('{app}\pyRevit Bridge'))) = 0) and
     (RunCmd(PyRevit + ' configs routes enable') = 0) then
    AddReport('pyRevit loads the bridge, and pyRevit Routes is on.')
  else
    AddReport('pyRevit setup failed. See the setup log in %TEMP%.');
end;

function HasCodex: Boolean;
begin
  Result := (FindCodex <> '') or DirExists(ExpandConstant('{%USERPROFILE}\.codex'));
end;

// Setup cannot run codex.exe (a Windows app link), so codex-config.ps1 edits ~\.codex\config.toml.
function CodexConfig(const Action: String): Integer;
begin
  Result := RunCmd('powershell -NoProfile -ExecutionPolicy Bypass -File ' + Quote(ExpandConstant('{app}\Setup\codex-config.ps1')) +
    ' -Action ' + Action + ' -Server ' + Quote(ExpandConstant('{app}\Server\FirstOption.RevitMcp.exe')));
end;

procedure RegisterAgents;
var
  Server, Claude: String;
  Found: Boolean;
begin
  WizardForm.StatusLabel.Caption := 'Connecting Claude Code and Codex...';
  Server := Quote(ExpandConstant('{app}\Server\FirstOption.RevitMcp.exe'));
  Found := False;
  Claude := FindClaude;
  if Claude <> '' then
  begin
    Found := True;
    RunCmd(Claude + ' mcp remove --scope user {#McpName} >nul 2>nul');
    if RunCmd(Claude + ' mcp add --scope user {#McpName} -- ' + Server) = 0 then
      AddReport('Claude Code: the Revit tools are ready in a new Claude Code session.')
    else
      AddReport('Claude Code: the MCP server was not registered. See the setup log in %TEMP%.');
  end;
  if HasCodex then
  begin
    Found := True;
    if CodexConfig('add') = 0 then
      AddReport('Codex: the Revit tools are ready in a new Codex session.')
    else
      AddReport('Codex: the MCP server was not registered. See the setup log in %TEMP%.');
  end;
  if not Found then
    AddReport('Claude Code and Codex were not found. Install one of them, then run this setup again to connect it.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteManifests;
    SetUpPyRevit;
    RegisterAgents;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedLabel.Caption := 'FirstOption Revit MCP is installed.' + #13#10 + Report + #13#10#13#10 +
      'In Revit, open First Option > AI Bridge > MCP Panel. It shows "pyRevit Routes online" when all is ready.';
    WizardForm.FinishedLabel.AutoSize := True;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Year: Integer;
  PyRevit, Claude: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RunCmd('taskkill /IM FirstOption.RevitMcp.exe /F >nul 2>nul');
    for Year := 2020 to 2030 do
      DeleteFile(ExpandConstant('{userappdata}\Autodesk\Revit\Addins\') + IntToStr(Year) + '\FirstOption.RevitMcp.addin');
    PyRevit := FindPyRevit;
    if PyRevit <> '' then
      RunCmd(PyRevit + ' extensions paths forget ' + Quote(ExpandConstant('{app}\pyRevit Bridge')));
    Claude := FindClaude;
    if Claude <> '' then
      RunCmd(Claude + ' mcp remove --scope user {#McpName} >nul 2>nul');
    CodexConfig('remove');
  end;
end;
