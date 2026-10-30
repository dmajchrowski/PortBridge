using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace PortBridge.Desktop;

/// <summary>
/// The per-user scheduled task behind "Start the app when I sign in": runs PortBridge --minimized at the user's sign-in,
/// elevated without a UAC prompt. Registered through the Task Scheduler COM API with the definition passed in memory —
/// never through a file another process of this user could change before Task Scheduler reads it.
/// </summary>
internal static class SignInTask
{
    private const string FolderName = "PortBridge";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int FileNotFound = unchecked((int)0x80070002);

    private static string UserSid => WindowsIdentity.GetCurrent().User!.Value;

    private static string TaskName => $"Start PortBridge at sign-in ({UserSid})";

    private static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return service;
    }

    // Late-bound COM calls report failures as the matching .NET exception (0x80070002 arrives as FileNotFoundException),
    // so errors are recognised by HResult rather than by type.
    private static dynamic? Folder(dynamic service, bool create)
    {
        try { return service.GetFolder(@"\" + FolderName); }
        catch (Exception ex) when (ex.HResult == FileNotFound)
        {
            return create ? service.GetFolder(@"\").CreateFolder(FolderName, null) : null;
        }
    }

    public static bool Exists()
    {
        try
        {
            var folder = Folder(Connect(), create: false);
            if (folder is null) return false;
            folder.GetTask(TaskName);
            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Create(string exePath)
    {
        var folder = Folder(Connect(), create: true)!;
        folder.RegisterTask(TaskName, Definition(exePath, UserSid), TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
    }

    public static void Delete()
    {
        try { Folder(Connect(), create: false)?.DeleteTask(TaskName, 0); }
        catch (Exception ex) when (ex.HResult == FileNotFound) { }
    }

    internal static string Definition(string exePath, string userSid) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Opens PortBridge in the notification area when this user signs in ("Start the app when I sign in" in its Settings).</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
              <Arguments>--minimized</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
}
