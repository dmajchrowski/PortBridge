; PortBridge setup (Inno Setup 6). Built by build.ps1 -Installer, which passes /DAppVersion=x.y.z.
;
; Like TCP-Com's setup: one signed-able setup program that installs the application, its virtual COM port driver
; (com0com instead of TCP-Com's ELTIMA driver), Start-menu entries and an uninstaller. Unlike TCP-Com's, it asks for
; administrator rights itself, can register PortBridge as a real Windows service and can open Windows Firewall for it.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppExe "PortBridge.exe"

[Setup]
AppId={{6F3B2C1E-7A0D-4B8E-9C5F-2D41A7E3B9C4}
AppName=PortBridge
AppVersion={#AppVersion}
AppVerName=PortBridge {#AppVersion}
AppPublisher=PortBridge
AppCopyright=Copyright (c) 2026 David Majchrowski
AppComments=Connects serial (COM) ports and virtual COM ports to TCP/IP and UDP
; Open source under the MIT License: shown on the License Agreement page, which must be accepted to continue.
LicenseFile=..\LICENSE
; The Windows service only runs from a folder non-administrators cannot change, so stay in Program Files.
DefaultDirName={autopf}\PortBridge
DefaultGroupName=PortBridge
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=PortBridge-Setup-{#AppVersion}
SetupIconFile=..\src\PortBridge\Assets\portbridge.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=PortBridge
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; [Code] stops the service and closes desktop windows itself (PrepareToInstall); this is a fallback for anything else.
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "service"; Description: "Run PortBridge as a Windows service (bridges start at boot, even when nobody is signed in)"; Check: not ServiceWasInstalled
Name: "firewall"; Description: "Allow PortBridge through Windows Firewall, so other PCs can connect to its bridges"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "com0com"; Description: "Install the com0com virtual COM port driver (needed for virtual COM ports)"; Check: not Com0ComReady

[Files]
Source: "..\artifacts\PortBridge\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
; Written by build.ps1: the licenses of the .NET runtime and packages bundled into the exe.
Source: "..\artifacts\PortBridge\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "redist\com0com\NOTICE.txt"; DestDir: "{app}\licenses"; DestName: "com0com-NOTICE.txt"; Flags: ignoreversion
Source: "redist\com0com\*"; DestDir: "{tmp}\com0com"; Flags: deleteafterinstall; Tasks: com0com

[Icons]
Name: "{group}\PortBridge"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall PortBridge"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PortBridge"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; PortBridge always runs as administrator (its manifest asks for it), so setup, already elevated, opens it directly
; and Windows does not ask a second time. The shortcuts above get the same UAC shield and prompt.
; runascurrentuser is needed: postinstall entries otherwise run as the original (unelevated) user, and
; CreateProcess then fails with error 740 ("The requested operation requires elevation").
Filename: "{app}\{#AppExe}"; Description: "Open PortBridge"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
; "Start the app when I sign in" is a scheduled task per user under \PortBridge\.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Get-ScheduledTask -TaskPath '\PortBridge\' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false; try {{ $s = New-Object -ComObject Schedule.Service; $s.Connect(); $s.GetFolder('\').DeleteFolder('PortBridge', 0) } catch {{ }"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveSignInTasks"
Filename: "{app}\{#AppExe}"; Parameters: "service uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PortBridge"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallRule"
; Close a desktop window still running from this folder, so its file can be removed.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Get-Process PortBridge -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -eq '{app}\{#AppExe}' } | Stop-Process -Force"""; Flags: runhidden waituntilterminated; RunOnceId: "CloseDesktopApp"

[UninstallDelete]
Type: dirifempty; Name: "{app}\licenses"
Type: dirifempty; Name: "{app}"

[Code]
var
  ServiceExisted: Boolean;
  Com0ComDriverAdded: Boolean;

function Run(const FileName, Params: String; Show: Integer): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', Show, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

{ True if Windows' driver store holds com0com's driver. Its files can be present without it, when the
  "Always trust Steven William Hatchett" question was declined. }
function Com0ComDriverInStore: Boolean;
var
  Code, I: Integer;
  Output: TExecOutput;
begin
  Result := False;
  if not ExecAndCaptureOutput(ExpandConstant('{sys}\pnputil.exe'), '/enum-drivers', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then
    Exit;
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
    if Pos('com0com.inf', Lowercase(Output.StdOut[I])) > 0 then
    begin
      Result := True;
      Exit;
    end;
end;

{ Remembered once: the service is stopped during the upgrade, and "sc query" answers 1060 when it does not exist. }
function InitializeSetup: Boolean;
begin
  ServiceExisted := Run(ExpandConstant('{sys}\sc.exe'), 'query PortBridge', SW_HIDE) <> 1060;
  Com0ComDriverAdded := Com0ComDriverInStore;
  Result := True;
end;

function ServiceWasInstalled: Boolean;
begin
  Result := ServiceExisted;
end;

{ com0com's files and its driver are both in place; otherwise the com0com task is offered (again). }
function Com0ComReady: Boolean;
begin
  Result := Com0ComDriverAdded and
    (RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\com0com')
     or FileExists(ExpandConstant('{commonpf32}\com0com\setupc.exe')));
end;

{ Desktop windows keep the program file open. Restart Manager cannot close windows in other users' sessions, and closes
  none while the stopped service's process (session 0) is still exiting, so close them here like the uninstaller does,
  then wait until every process started from the install folder, the service's included, has exited. }
procedure CloseRunningCopies;
var
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  StringChangeEx(Exe, '''', '''''', True);
  Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -Command "' +
    '$exe = ''' + Exe + '''; ' +
    'function Running { Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $exe } }; ' +
    'Running | Where-Object { $_.SessionId -ne 0 } | Invoke-CimMethod -MethodName Terminate | Out-Null; ' +
    'for ($i = 0; $i -lt 50 -and (Running); $i++) { Start-Sleep -Milliseconds 200 }"', SW_HIDE);
end;

{ Stops the running service so its program file can be replaced; it is started again after the files are copied. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ServiceExisted and FileExists(ExpandConstant('{app}\{#AppExe}')) then
    Run(ExpandConstant('{app}\{#AppExe}'), 'service stop', SW_HIDE)
  else if ServiceExisted then
    Run(ExpandConstant('{sys}\sc.exe'), 'stop PortBridge', SW_HIDE);
  if FileExists(ExpandConstant('{app}\{#AppExe}')) then
    CloseRunningCopies;
end;

procedure InstallCom0Com;
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing the com0com virtual COM port driver...';
  Code := Run(ExpandConstant('{tmp}\com0com\setup.exe'), '/S', SW_SHOW);
  if Code <> 0 then
  begin
    SuppressibleMsgBox('The com0com driver setup failed (code ' + IntToStr(Code) + '). PortBridge is installed, but virtual COM ports ' +
      'will not work until com0com is installed.', mbError, MB_OK, IDOK);
    Exit;
  end;
  { Add the driver to Windows now, while someone is at the screen: Windows asks whether to trust its publisher
    ("Always trust software from Steven William Hatchett"). The PortBridge service cannot show that question later. }
  WizardForm.StatusLabel.Caption := 'Adding the com0com driver to Windows (Windows may ask you to confirm)...';
  Code := Run(ExpandConstant('{sys}\pnputil.exe'), '/add-driver "' + ExpandConstant('{commonpf32}\com0com\com0com.inf') + '"', SW_SHOW);
  if (Code <> 0) and (Code <> 3010) then
    SuppressibleMsgBox('Windows did not add the com0com driver (pnputil error 0x' + Format('%.8x', [Code]) + '), for example because ' +
      'its publisher was not confirmed. Virtual COM ports will not work until it is added. To add it later, run this setup ' +
      'again and keep the com0com task ticked, or run this command as administrator and choose Install when Windows asks:' + #13#10#13#10 +
      'pnputil /add-driver "' + ExpandConstant('{commonpf32}\com0com\com0com.inf') + '"', mbError, MB_OK, IDOK);
end;

function ServiceProblem(Code: Integer): String;
begin
  case Code of
    5: Result := 'it needs administrator rights';
    10: Result := 'the folder PortBridge is installed in can be changed by non-administrators; install it under Program Files';
    11: Result := 'the data folder ' + ExpandConstant('{commonappdata}\PortBridge') + ' contains links or permissions that could not be secured';
  else
    Result := 'error code ' + IntToStr(Code);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  App, Netsh: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  App := ExpandConstant('{app}\{#AppExe}');
  Netsh := ExpandConstant('{sys}\netsh.exe');

  if WizardIsTaskSelected('firewall') then
  begin
    WizardForm.StatusLabel.Caption := 'Allowing PortBridge through Windows Firewall...';
    Run(Netsh, 'advfirewall firewall delete rule name="PortBridge"', SW_HIDE);
    { All profiles: device and plant networks usually have no domain and are left as "Public" by Windows. }
    Run(Netsh, 'advfirewall firewall add rule name="PortBridge" dir=in action=allow program="' + App + '" enable=yes profile=any', SW_HIDE);
  end;

  { The service starts before com0com is installed, so bridges are back while Windows' driver question waits for an
    answer. Bridges with virtual COM ports keep retrying and come up once the driver is added. }
  Code := 0;
  if ServiceExisted then
  begin
    WizardForm.StatusLabel.Caption := 'Starting the PortBridge service...';
    Code := Run(App, 'service start', SW_HIDE);
  end
  else if WizardIsTaskSelected('service') then
  begin
    WizardForm.StatusLabel.Caption := 'Installing the PortBridge service...';
    Code := Run(App, 'service install --start', SW_HIDE);
  end;
  if Code <> 0 then
    SuppressibleMsgBox('PortBridge is installed, but its Windows service could not be set up: ' + ServiceProblem(Code) + '.' + #13#10 +
      'You can still use PortBridge from its window, or try again under Settings > Install as service.', mbError, MB_OK, IDOK);

  if WizardIsTaskSelected('com0com') then
    InstallCom0Com;
end;
