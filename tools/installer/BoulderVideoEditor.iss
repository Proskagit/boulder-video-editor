; Boulder Video Editor — the Windows installer (DECISIONS.md D070 §2). Built by tools/distribution/New-Release.ps1, which passes:
;   /DAppVersion=1.3.0            the product version (also the published assemblies' version)
;   /DAppDir=<folder>             the published application (Publish-Portable.ps1: self-contained win-x64 with ffmpeg\)
;   /DModelsDir=<folder>          the models package (Pack-Models.ps1: models.json, models\<folder>\<file>, notices, licences)
;   /DModelsInclude=<file>        the [Files] lines and the folder list generated from tools/models/models.json
;   /DOutputDir=<folder>          where BoulderVideoEditor-<version>-setup.exe is written
;   /DRepoRoot=<folder>           the repository (the icon)
; Per user (no administrator rights): the program into %LOCALAPPDATA%\Programs\Boulder Video Editor by default (the folder can
; be chosen on the first installation), the models into %LOCALAPPDATA%\BoulderVideoEditor\models — the folder the application
; reads them from. An update is the next version's installer run over the installed one: the same AppId, the same folder, the
; settings, projects and models kept. The uninstaller removes the program's files and shortcuts only; the models only when the
; user ticks their box; never the projects, videos, exports, settings or caches.

#ifndef AppVersion
  #error AppVersion is not defined (run tools/distribution/New-Release.ps1)
#endif
#ifndef AppDir
  #error AppDir is not defined
#endif
#ifndef ModelsDir
  #error ModelsDir is not defined
#endif
#ifndef ModelsInclude
  #error ModelsInclude is not defined
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef RepoRoot
  #define RepoRoot "..\.."
#endif

#define AppName "Boulder Video Editor"
#define AppExe "BoulderVideoEditor.exe"
; The per-user folder of the application (src/Infrastructure/Configuration/AppPaths.cs: AppFolderName).
#define DataFolder "BoulderVideoEditor"
; Held by the running application (src/App/Program.cs: RunningMutexName).
#define RunningMutex "BoulderVideoEditor.Running"

[Setup]
; Never change the AppId: it is what makes a new version's installer update the installed one instead of adding a second copy.
AppId={{564FF1BA-2649-4C46-9BF9-C48DCCEA4DA9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}
; The folder is asked on the first installation only; an update goes into the installed one.
DisableDirPage=auto
UsePreviousAppDir=yes
DirExistsWarning=auto
AppMutex={#RunningMutex}
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile={#RepoRoot}\src\UI\Assets\app.ico
WizardStyle=modern dark
WizardSmallImageFile={#RepoRoot}\src\UI\Assets\app-256.png
DisableWelcomePage=yes
OutputDir={#OutputDir}
OutputBaseFilename=BoulderVideoEditor-{#AppVersion}-setup
; The models (ONNX weights) do not compress: their files are stored (nocompression); the program is compressed.
Compression=lzma2/max
SolidCompression=no
LZMAUseSeparateProcess=yes
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
russian.ConfirmUninstall=Удалить %1?%n%nПроекты, видео, экспорты и настройки не удаляются. ИИ-модели удаляются, только если это выбрано на предыдущем шаге.
english.ConfirmUninstall=Remove %1?%n%nProjects, videos, exports and settings are not removed. The AI models are removed only if that was chosen in the previous step.

[CustomMessages]
russian.ModelsComponent=ИИ-модели (около 1,2 ГБ) устанавливаются в %LOCALAPPDATA%\{#DataFolder}\models.
english.ModelsComponent=The AI models (about 1.2 GB) are installed into %LOCALAPPDATA%\{#DataFolder}\models.
russian.UninstallTitle=Удаление {#AppName}
english.UninstallTitle=Uninstall {#AppName}
russian.UninstallText=Будут удалены файлы программы и её ярлыки.%n%nВаши проекты, видео, экспорты, настройки и список недавних проектов не удаляются.
english.UninstallText=The program's files and shortcuts will be removed.%n%nYour projects, videos, exports, settings and the recent projects list are not removed.
russian.UninstallModels=Также удалить ИИ-модели (около 1,2 ГБ) из %LOCALAPPDATA%\{#DataFolder}\models
english.UninstallModels=Also remove the AI models (about 1.2 GB) from %LOCALAPPDATA%\{#DataFolder}\models
russian.UninstallModelsNote=Модели нужны и портативной версии редактора; без них она предложит установить их снова.
english.UninstallModelsNote=The portable version of the editor uses the same models; without them it offers to install them again.
russian.UninstallContinue=Удалить
english.UninstallContinue=Remove
russian.Downgrade=Установлена более новая версия {#AppName} (%1). Установить версию {#AppVersion} поверх неё?
english.Downgrade=A newer version of {#AppName} (%1) is installed. Install version {#AppVersion} over it?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The models' notices and licences stay with the program (removed with it); the models themselves go to the models folder.
Source: "{#ModelsDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}\models-licenses"; Flags: ignoreversion
Source: "{#ModelsDir}\README.txt"; DestDir: "{app}\models-licenses"; Flags: ignoreversion
Source: "{#ModelsDir}\models.json"; DestDir: "{app}\models-licenses"; Flags: ignoreversion
Source: "{#ModelsDir}\licenses\*"; DestDir: "{app}\models-licenses\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
#include ModelsInclude

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
var
  ModelChecks: TStringList;
  DeleteModels: Boolean;

function ModelsRoot(): String;
begin
  Result := ExpandConstant('{localappdata}\{#DataFolder}\models');
end;

{ A model file is copied unless the one in place has the pinned SHA-256 (a correct file is never rewritten; a damaged or
  missing one is). Inno may call a Check several times: each answer is kept. }
function ModelNeeded(const Relative, Sha256: String): Boolean;
var
  Path, Key: String;
  Index: Integer;
begin
  Key := Relative;
  Index := ModelChecks.IndexOfName(Key);
  if Index >= 0 then
  begin
    Result := ModelChecks.ValueFromIndex[Index] = '1';
    Exit;
  end;
  Path := ModelsRoot() + '\' + Relative;
  Result := True;
  if FileExists(Path) then
  try
    Result := CompareText(GetSHA256OfFile(Path), Sha256) <> 0;
  except
    Result := True;
  end;
  if Result then
    ModelChecks.Add(Key + '=1')
  else
    ModelChecks.Add(Key + '=0');
  if Result then
    Log('Model file to install: ' + Relative)
  else
    Log('Model file already in place: ' + Relative);
end;

function InstalledVersion(): String;
var
  Key: String;
begin
  Result := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1';
  if not RegQueryStringValue(HKCU, Key, 'DisplayVersion', Result) then
    Result := '';
end;

function InitializeSetup(): Boolean;
var
  Installed: String;
  InstalledPacked, ThisPacked: Int64;
begin
  ModelChecks := TStringList.Create;
  Result := True;
  Installed := InstalledVersion();
  if (Installed <> '') and StrToVersion(Installed, InstalledPacked) and StrToVersion('{#AppVersion}', ThisPacked) then
    if ComparePackedVersion(InstalledPacked, ThisPacked) > 0 then
      Result := SuppressibleMsgBox(FmtMessage(CustomMessage('Downgrade'), [Installed]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
end;

procedure DeinitializeSetup();
begin
  ModelChecks.Free;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + CustomMessage('ModelsComponent');
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

{ --- Uninstall ------------------------------------------------------------------------------------------------------- }

{ The uninstall options: the models are kept unless the box is ticked. A silent uninstall keeps them unless /DELETEMODELS
  is given. }
function InitializeUninstall(): Boolean;
var
  Form: TSetupForm;
  Text, Note: TNewStaticText;
  Box: TNewCheckBox;
  Ok, Cancel: TNewButton;
begin
  DeleteModels := False;
  if UninstallSilent() then
  begin
    DeleteModels := ExpandConstant('{param:DELETEMODELS|0}') <> '0';
    Result := True;
    Exit;
  end;

  Form := CreateCustomForm(ScaleX(460), ScaleY(210), False, True);
  try
    Form.Caption := CustomMessage('UninstallTitle');

    Text := TNewStaticText.Create(Form);
    Text.Parent := Form;
    Text.Left := ScaleX(16);
    Text.Top := ScaleY(16);
    Text.Width := Form.ClientWidth - ScaleX(32);
    Text.AutoSize := False;
    Text.WordWrap := True;
    Text.Height := ScaleY(64);
    Text.Caption := CustomMessage('UninstallText');

    Box := TNewCheckBox.Create(Form);
    Box.Parent := Form;
    Box.Left := ScaleX(16);
    Box.Top := Text.Top + Text.Height + ScaleY(8);
    Box.Width := Form.ClientWidth - ScaleX(32);
    Box.Height := ScaleY(20);
    Box.Caption := CustomMessage('UninstallModels');
    Box.Checked := False;

    Note := TNewStaticText.Create(Form);
    Note.Parent := Form;
    Note.Left := ScaleX(34);
    Note.Top := Box.Top + Box.Height + ScaleY(2);
    Note.Width := Form.ClientWidth - ScaleX(50);
    Note.AutoSize := False;
    Note.WordWrap := True;
    Note.Height := ScaleY(32);
    Note.Caption := CustomMessage('UninstallModelsNote');

    Cancel := TNewButton.Create(Form);
    Cancel.Parent := Form;
    Cancel.Width := ScaleX(90);
    Cancel.Height := ScaleY(26);
    Cancel.Left := Form.ClientWidth - ScaleX(16) - Cancel.Width;
    Cancel.Top := Form.ClientHeight - ScaleY(14) - Cancel.Height;
    Cancel.Caption := SetupMessage(msgButtonCancel);
    Cancel.ModalResult := mrCancel;
    Cancel.Cancel := True;

    Ok := TNewButton.Create(Form);
    Ok.Parent := Form;
    Ok.Width := ScaleX(90);
    Ok.Height := ScaleY(26);
    Ok.Left := Cancel.Left - ScaleX(8) - Ok.Width;
    Ok.Top := Cancel.Top;
    Ok.Caption := CustomMessage('UninstallContinue');
    Ok.ModalResult := mrOk;
    Ok.Default := True;

    Form.ActiveControl := Ok;
    Result := Form.ShowModal() = mrOk;
    DeleteModels := Result and Box.Checked;
  finally
    Form.Free;
  end;
end;

{ Only the model folders the application has ever pinned are removed (MODEL_FOLDERS of the generated include, plus the
  older people-v1); the models folder itself only when nothing else is left in it. Nothing else of the per-user folder. }
procedure RemoveModels();
var
  Folders: TArrayOfString;
  I: Integer;
  Path: String;
begin
  Folders := ModelFolders();
  for I := 0 to GetArrayLength(Folders) - 1 do
  begin
    Path := ModelsRoot() + '\' + Folders[I];
    if DirExists(Path) then
    begin
      if DelTree(Path, True, True, True) then
        Log('Removed the model folder ' + Path)
      else
        Log('Could not remove the model folder ' + Path);
    end;
  end;
  if RemoveDir(ModelsRoot()) then
    Log('Removed the empty models folder ' + ModelsRoot());
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteModels then
    RemoveModels();
end;
