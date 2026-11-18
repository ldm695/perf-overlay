using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace PerfOverlay;

internal static class DpiBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
    }
}

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\PerfOverlay.SingleInstance";
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--repair-service")
        {
            Environment.ExitCode = ServiceRepairHelper.Restart(e.Args[1]);
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, MutexName, out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

internal static class ServiceRepairHelper
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 1;
    private const uint ServiceRunning = 4;

    public static async Task<int> RequestRestartAsync(string serviceName)
    {
        if (!IsSupportedService(serviceName))
            return 87;

        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                return 2;

            var info = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            info.ArgumentList.Add("--repair-service");
            info.ArgumentList.Add(serviceName);
            using var process = Process.Start(info);
            if (process is null)
                return 2;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223;
        }
        catch (TimeoutException)
        {
            return 1460;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.NativeErrorCode;
        }
    }

    public static int Restart(string serviceName)
    {
        if (!IsSupportedService(serviceName))
            return 87;

        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            return Marshal.GetLastWin32Error();

        try
        {
            var service = OpenServiceW(manager, serviceName, ServiceQueryStatus | ServiceStart | ServiceStop);
            if (service == IntPtr.Zero)
                return Marshal.GetLastWin32Error();

            try
            {
                if (!QueryServiceStatus(service, out var status))
                    return Marshal.GetLastWin32Error();

                if (status.CurrentState != ServiceStopped)
                {
                    if (!ControlService(service, ServiceControlStop, out status))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != 1062) // already stopped
                            return error;
                    }

                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (!QueryServiceStatus(service, out status))
                            return Marshal.GetLastWin32Error();
                        if (status.CurrentState == ServiceStopped)
                            break;
                        Thread.Sleep(250);
                    }
                    if (status.CurrentState != ServiceStopped)
                        return 1460;
                }

                if (!StartServiceW(service, 0, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1056) // already running
                        return error;
                }

                var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (DateTime.UtcNow < startDeadline)
                {
                    if (!QueryServiceStatus(service, out status))
                        return Marshal.GetLastWin32Error();
                    if (status.CurrentState == ServiceRunning)
                        return 0;
                    Thread.Sleep(250);
                }
                return 1460;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static bool IsSupportedService(string name) =>
        name is "ESRV_SVC_QUEENCREEK" or "FvSvc";

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
