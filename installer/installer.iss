; SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
; Copyright (c) 2026 hmodymac8-droid
; Inno Setup script for PC Butler. build.ps1 compiles it and passes AppVersion and
; DistDir (the published program folder) on the command line.
;
; A standard per-user setup wizard: no administrator rights, proper uninstaller,
; English and Arabic. The program updates itself by running this setup with
; /VERYSILENT (see Updater.Apply in src/PCButler.Core/Updater.cs).

#define AppName "PC Butler"
#define AppExe "PCButler.exe"
#define DataFolder "PCButler"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValue "PCButler"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef DistDir
  #define DistDir "..\artifacts\dist"
#endif
; TestBuild makes a copy that is safe to install into a scratch folder: it uses
; its own identity, never closes or starts the real program, and leaves the
; shortcuts and the startup entry alone.
#ifdef TestBuild
  #define AppId "{{C4BFA050-D93B-4180-B2D4-7E5700000000}"
#else
  #define AppId "{{C4BFA050-D93B-4180-B2D4-ACCDBE95D00A}"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=hmodymac8-droid
AppCopyright=Copyright (c) 2026 hmodymac8-droid
AppPublisherURL=https://github.com/hmodymac8-droid/PC-Butler
AppSupportURL=https://github.com/hmodymac8-droid/PC-Butler/issues
AppUpdatesURL=https://github.com/hmodymac8-droid/PC-Butler/releases
; "lowest" installs for the current user only, under AppData\Local\Programs.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputBaseFilename=PCButlerSetup
SetupIconFile=..\src\PCButler\icon.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The program is closed by PrepareToInstall below, so skip the Restart Manager prompt.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[CustomMessages]
english.Autostart=Start {#AppName} when Windows starts
arabic.Autostart=تشغيل {#AppName} مع بدء تشغيل ويندوز
english.AutoSort=Sort automatically in the background
arabic.AutoSort=الفرز التلقائي في الخلفية
english.OpenApp=Open {#AppName}
arabic.OpenApp=فتح {#AppName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"
Name: "autostart"; Description: "{cm:Autostart}"; Flags: unchecked
Name: "autosort"; Description: "{cm:AutoSort}"; Flags: unchecked

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
#ifndef TestBuild
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
#endif

[Registry]
; File Explorer's right-click entry is written by the program itself (see ExplorerMenu); uninstalling takes it away.
Root: HKCU; Subkey: "Software\Classes\*\shell\PCButler"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\PCButler"; Flags: dontcreatekey uninsdeletekey
#ifndef TestBuild
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunValue}"; \
    ValueData: """{app}\{#AppExe}"" --background"; Check: WantAutostart
#endif

[Run]
#ifndef TestBuild
; One process holds the tray icon and the window. After a silent update it always comes
; back as it was; in the wizard it starts with automatic sorting on if that was ticked.
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait; Check: WizardSilent
Filename: "{app}\{#AppExe}"; Parameters: "--background --auto-on"; Flags: nowait; Check: TickedAutoSort
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait; Check: TickedAutostartOnly
; Updating from the window: a second launch brings the window up in the running program.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: ReopenRequested
#endif
Filename: "{app}\{#AppExe}"; Description: "{cm:OpenApp}"; Flags: nowait postinstall skipifsilent

[Code]
var
  AutostartWasOn: Boolean;

procedure CloseProgram;
var
  ResultCode: Integer;
begin
#ifndef TestBuild
  // The running program locks its files.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
#endif
end;

function InitializeSetup: Boolean;
begin
  // Remember the state before anything is changed.
  AutostartWasOn := RegValueExists(HKEY_CURRENT_USER, '{#RunKey}', '{#RunValue}');
  Result := True;
end;

function WantAutostart: Boolean;
begin
  // A silent run is the program updating itself: keep the start with Windows as the
  // user has it now. (They may have switched it in the program since installing.)
  if WizardSilent then
    Result := AutostartWasOn
  else
    Result := WizardIsTaskSelected('autostart');
end;

function TickedAutoSort: Boolean;
begin
  Result := (not WizardSilent) and WizardIsTaskSelected('autosort');
end;

function TickedAutostartOnly: Boolean;
begin
  // Starting with Windows but not sorting: the program still starts now, in the tray.
  Result := (not WizardSilent) and WizardIsTaskSelected('autostart') and not WizardIsTaskSelected('autosort');
end;

function ReopenRequested: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:reopen|0}') = '1');
end;

procedure InitializeWizard;
begin
  // Updating over an existing install: offer the automatic option as it is now.
  if AutostartWasOn then
    WizardSelectTasks('autostart');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseProgram;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: String;
begin
  // Lets the program announce "Updated to version X" when it starts again.
  // (Not for a TestBuild, which must leave the real data folder alone.)
#ifndef TestBuild
  if (CurStep = ssPostInstall) and WizardSilent then
  begin
    DataDir := ExpandConstant('{userappdata}\{#DataFolder}');
    ForceDirectories(DataDir);
    SaveStringToFile(DataDir + '\updated.txt', '{#AppVersion}', False);
  end;
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseProgram;
#ifndef TestBuild
    // The program may have added this itself ("Turn on automatic sorting").
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#RunValue}');
#endif
  end;
end;
