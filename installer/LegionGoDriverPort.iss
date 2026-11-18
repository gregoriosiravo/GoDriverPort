; Inno Setup 6 script for Legion Go Driver Port.
;
; The installer ships no Microsoft Windows SDK file: the app uses signtool.exe and makecat.exe from the user's own Windows SDK.
; The licence page shows the MIT LICENSE; BEFORE-YOU-INSTALL.txt (plain language) is shown before it.
;
; Build:  ISCC.exe /DAppVersion=0.1.0 installer\LegionGoDriverPort.iss      (build.ps1 -Installer does this)
; Input:  app\publish\ (produced by app\publish.ps1, which fails if the folder contains any Microsoft SDK file)
; Output: dist\LegionGoDriverPort-<version>-Setup.exe

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "Legion Go Driver Port"
#define AppExe "LegionGoDriverPort.exe"
#define PublishDir SourcePath + "..\app\publish"

#if !FileExists(PublishDir + "\" + AppExe)
  #error "app\publish\LegionGoDriverPort.exe not found. Run app\publish.ps1 first."
#endif
; Defence in depth: never build an installer from a folder that contains Microsoft SDK files.
#if FileExists(PublishDir + "\tools\makecat.exe") || FileExists(PublishDir + "\tools\signtool.exe")
  #error "app\publish contains Microsoft SDK files (signtool/makecat). Re-run app\publish.ps1."
#endif

[Setup]
; Never change AppId: it identifies the app for upgrades and uninstall.
AppId={{6F2D3B7A-5C41-4E8B-9A1D-3E7C2B90A4F6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Legion Go Driver Port contributors
AppPublisherURL=https://github.com/gregoriosiravo/LegionGoDriverPort
AppSupportURL=https://github.com/gregoriosiravo/LegionGoDriverPort/issues
AppUpdatesURL=https://github.com/gregoriosiravo/LegionGoDriverPort/releases
AppCopyright=Copyright (c) 2026 Legion Go Driver Port contributors
VersionInfoVersion={#AppVersion}.0
DefaultDirName={autopf}\LegionGoDriverPort
; Upgrades from a development install move to the folder above instead of reusing the old one.
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; The app itself requests administrator rights; installing to Program Files keeps tools\ writable by administrators only.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
LicenseFile={#SourcePath}..\LICENSE
InfoBeforeFile={#SourcePath}BEFORE-YOU-INSTALL.txt
; The install folder must be writable only by administrators (the app runs elevated and starts the programs in tools\), so the folder is fixed.
DisableDirPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#SourcePath}..\dist
OutputBaseFilename=LegionGoDriverPort-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything in the publish folder (tools\, licenses\,THIRD-PARTY-NOTICES.md, BEFORE-YOU-INSTALL.txt, LICENSE.txt ...). Excludes are defence in depth: no Microsoft SDK file may ever be installed.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "makecat*,signtool*"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Upgrade over 0.1.0 development installs, which bundled the Microsoft signing tools and an end-user agreement. Releases ship none of these.
Type: files; Name: "{app}\tools\signtool*"
Type: files; Name: "{app}\tools\makecat*"
Type: files; Name: "{app}\tools\mssign32.dll"
Type: files; Name: "{app}\tools\wintrust.dll"
Type: files; Name: "{app}\tools\wintrust.dll.ini"
Type: files; Name: "{app}\tools\appxpackaging.dll"
Type: files; Name: "{app}\tools\appxsip.dll"
Type: files; Name: "{app}\tools\opcservices.dll"
Type: files; Name: "{app}\tools\msisip.dll"
Type: files; Name: "{app}\tools\Microsoft.Windows.Build.*.manifest"
Type: files; Name: "{app}\EULA.txt"
Type: files; Name: "{app}\PERSONAL-BUILD*"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Not launched automatically: unchecked by default and never in a silent install.
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall shellexec skipifsilent unchecked

[Code]
var
  DeleteDataOnUninstall: Boolean;

function MakecatUnderKitsRoot(const ProgramFilesDir: String): Boolean;
var
  BinDir: String;
  FindRec: TFindRec;
begin
  Result := False;
  BinDir := ProgramFilesDir + '\Windows Kits\10\bin';
  if FindFirst(BinDir + '\*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          if FileExists(BinDir + '\' + FindRec.Name + '\x64\makecat.exe') then
          begin
            Result := True;
            Break;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function MakecatInstalled: Boolean;
begin
  Result := MakecatUnderKitsRoot(ExpandConstant('{commonpf32}')) or MakecatUnderKitsRoot(ExpandConstant('{commonpf64}'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not MakecatInstalled) then
  begin
    MsgBox('One more step before you can convert a driver.' + #13#10 + #13#10 +
      'Legion Go Driver Port needs makecat.exe and signtool.exe from the free Windows SDK "Signing Tools for Desktop Apps". ' +
      'Microsoft does not allow them to be redistributed, so they are not included, and they were not found on this PC.' + #13#10 + #13#10 +
      'To install it, open a command prompt or PowerShell and run:' + #13#10 + #13#10 +
      '    winget install --interactive Microsoft.WindowsSDK.10.0.28000' + #13#10 + #13#10 +
      'then select only "Windows SDK Signing Tools for Desktop Apps" in the installer. ' +
      'Until then the app starts but cannot convert (rolling back an earlier conversion still works). ' +
      'Details: the project page on GitHub, section "Requirements" of the README.', mbInformation, MB_OK);
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  DeleteDataOnUninstall := False;
  if UninstallSilent then
    Exit;  { silent uninstall: never touch the data folder }

  if MsgBox('Uninstalling Legion Go Driver Port does NOT roll back the graphics driver it installed and does NOT remove ' +
      'the self-signed certificate it added to this PC''s trusted root store.' + #13#10 + #13#10 +
      'If you want to go back to Lenovo''s driver and remove the certificate, cancel now, open the app and use Rollback first ' +
      '(see the project page on GitHub, README section "Safety" of app/README.md).' + #13#10 + #13#10 +
      'Continue with uninstalling the app?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDNO then
  begin
    Result := False;
    Exit;
  end;

  DeleteDataOnUninstall := MsgBox('Also delete the app''s data folder?' + #13#10 + #13#10 +
      ExpandConstant('{commonappdata}') + '\LegionGoDriverConverter' + #13#10 + #13#10 +
      'It holds the session logs and the retained copy of Lenovo''s original driver package, which Rollback needs. ' +
      'If you delete it you can no longer roll back with the app.' + #13#10 + #13#10 +
      'Choose No to keep it (recommended).', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteDataOnUninstall then
    DelTree(ExpandConstant('{commonappdata}') + '\LegionGoDriverConverter', True, True, True);
end;
