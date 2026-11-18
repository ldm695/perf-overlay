using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PerfOverlay.Capture;

public enum CaptureIssueKind
{
    IntelDcaPmon,
    NvidiaFrameView
}

public sealed record CaptureExternalIssue(CaptureIssueKind Kind, string SessionName, string ServiceName, string Reason);

public sealed class CaptureRecoveryModule : IDisposable
{
    private static readonly Regex OrphanSessionPattern = new(
        "--session_name\\s+\\\"?PerfOverlay-(?<parent>\\d+)-(?<guid>[0-9a-f]{32})\\\"?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OrphanSessionNamePattern = new(
        "^\\s*PerfOverlay-(?<parent>\\d+)-(?<guid>[0-9a-f]{32})\\s+Trace\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly PresentMonCaptureService _capture;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Queue<DateTimeOffset> _restarts = new();
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private Task? _monitorTask;
    private CaptureExternalIssue? _pendingExternalIssue;
    private string? _ignoredIssueKey;
    private bool _exhaustionReported;
    private DateTimeOffset _nextRestartAllowedAt;

    public event Action<string>? AutomaticRecoveryOccurred;
    public event Action<string>? RecoveryExhausted;
    public event Action<string>? StartupCleanupOccurred;
    public event Action<CaptureExternalIssue>? ExternalIssueDetected;

    public CaptureRecoveryModule(PresentMonCaptureService capture) => _capture = capture;

    public void Start()
    {
        var cleaned = CleanupOrphanedCollectors();
        if (cleaned > 0)
        {
            var message = $"Removed {cleaned} orphaned PerfOverlay collector/session resource(s).";
            PresentMonCaptureService.WriteDiagnostic(message);
            StartupCleanupOccurred?.Invoke(message);
        }

        _monitorTask ??= Task.Run(MonitorAsync);
    }

    public void ResolveExternalIssue(CaptureExternalIssue issue, bool externalRepairSucceeded, string resultMessage)
    {
        lock (_restarts)
        {
            if (_pendingExternalIssue != issue)
                return;

            _pendingExternalIssue = null;
            if (!externalRepairSucceeded)
                _ignoredIssueKey = GetIssueKey(issue);
            else
            {
                _ignoredIssueKey = null;
                _restarts.Clear();
                _exhaustionReported = false;
            }
        }

        PresentMonCaptureService.WriteDiagnostic($"External capture repair: {resultMessage}");
    }

    public Task<int> RestartExternalServiceAsync(CaptureExternalIssue issue) =>
        ServiceRepairHelper.RequestRestartAsync(issue.ServiceName);

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
                await CheckCaptureHealthAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    private async Task CheckCaptureHealthAsync(CancellationToken cancellationToken)
    {
        if (!await _monitorGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var targetPid = _capture.TargetProcessId;
            if (targetPid <= 0)
                return;

            lock (_restarts)
                if (_pendingExternalIssue is not null)
                    return;

            var now = DateTimeOffset.UtcNow;
            var lastFrameAt = _capture.LastTargetFrameAt;
            var lastFailure = _capture.LastFailure;
            var collectorRunning = _capture.IsRunning;
            if (collectorRunning && lastFrameAt is null && string.IsNullOrWhiteSpace(lastFailure))
                return;

            if (lastFrameAt is DateTimeOffset recentFrame && now - recentFrame < TimeSpan.FromSeconds(8))
            {
                lock (_restarts)
                {
                    _restarts.Clear();
                    _ignoredIssueKey = null;
                    _exhaustionReported = false;
                    _nextRestartAllowedAt = DateTimeOffset.MinValue;
                }
                return;
            }

            var baseline = lastFrameAt ?? Max(_capture.TargetStartedAt, _capture.CaptureStartedAt) ?? now;
            var stallLimit = !collectorRunning ? TimeSpan.FromSeconds(2) :
                !string.IsNullOrWhiteSpace(lastFailure) ? TimeSpan.FromSeconds(3) :
                TimeSpan.FromSeconds(12);
            if (now - baseline < stallLimit)
                return;

            lock (_restarts)
                if (now < _nextRestartAllowedAt)
                    return;

            var issue = await DetectKnownExternalIssueAsync(cancellationToken).ConfigureAwait(false);
            if (issue is not null)
            {
                var issueKey = GetIssueKey(issue);
                lock (_restarts)
                {
                    if (_ignoredIssueKey != issueKey)
                    {
                        _pendingExternalIssue = issue;
                        PresentMonCaptureService.WriteDiagnostic($"Known external ETW issue detected: {issueKey}; {issue.Reason}");
                    }
                }
                if (_pendingExternalIssue is not null)
                {
                    ExternalIssueDetected?.Invoke(issue);
                    return;
                }
            }

            if (!CanRestart(now))
                return;

            try
            {
                await Task.Run(_capture.Restart, cancellationToken).ConfigureAwait(false);
                lock (_restarts)
                    _nextRestartAllowedAt = DateTimeOffset.UtcNow.AddSeconds(30);
                AutomaticRecoveryOccurred?.Invoke($"PresentMon capture restarted for foreground PID {targetPid} after frame data stalled.");
                PresentMonCaptureService.WriteDiagnostic($"Automatic capture recovery restarted PresentMon for target PID {targetPid}.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                PresentMonCaptureService.WriteDiagnostic($"Automatic PresentMon recovery failed: {ex.Message}");
            }
        }
        finally
        {
            _monitorGate.Release();
        }
    }

    private bool CanRestart(DateTimeOffset now)
    {
        lock (_restarts)
        {
            while (_restarts.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(5))
                _restarts.Dequeue();
            if (_restarts.Count >= 3)
            {
                if (!_exhaustionReported)
                {
                    _exhaustionReported = true;
                    RecoveryExhausted?.Invoke("Frame capture is still stalled after three automatic restarts. Check the capture.log for the detected ETW issue.");
                    PresentMonCaptureService.WriteDiagnostic("Automatic capture recovery limit reached (3 restarts in 5 minutes).");
                }
                return false;
            }
            _restarts.Enqueue(now);
            return true;
        }
    }

    private static async Task<CaptureExternalIssue?> DetectKnownExternalIssueAsync(CancellationToken cancellationToken)
    {
        var sessionOutput = await QueryEtwSessionsAsync(cancellationToken).ConfigureAwait(false);
        if (sessionOutput is null)
            return null;

        if (ContainsSession(sessionOutput, "DCA_PMON"))
        {
            var state = GetServiceState("ESRV_SVC_QUEENCREEK");
            return new CaptureExternalIssue(
                CaptureIssueKind.IntelDcaPmon,
                "DCA_PMON",
                "ESRV_SVC_QUEENCREEK",
                state is null
                    ? "Intel DCA_PMON is active, but its previously associated Intel service is not installed; the session may be stale."
                    : "Intel DCA_PMON is active while frame collection is stalled. Previous diagnostics linked this session to lost DXGI ETW events.");
        }

        if (ContainsSession(sessionOutput, "FrameViewService"))
        {
            var serviceState = GetServiceState("FvSvc");
            var producerRunning = IsNvidiaFrameViewPresentMonRunning();
            if (!producerRunning || !string.Equals(serviceState, "Running", StringComparison.OrdinalIgnoreCase))
                return new CaptureExternalIssue(
                    CaptureIssueKind.NvidiaFrameView,
                    "FrameViewService",
                    "FvSvc",
                    "A FrameView ETW session remains active, but its PresentMon collector or service is stopped. Previously, restarting FvSvc cleared the stale session and restored frame monitoring.");
        }

        return null;
    }

    private static async Task<string?> QueryEtwSessionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "logman.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("query");
            info.ArgumentList.Add("-ets");
            using var process = Process.Start(info);
            if (process is null)
                return null;

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            PresentMonCaptureService.WriteDiagnostic($"ETW session inventory unavailable: {ex.Message}");
            return null;
        }
    }

    private static string? GetServiceState(string serviceName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2", $"SELECT State FROM Win32_Service WHERE Name='{serviceName}'");
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            using (row)
                return row["State"] as string;
        }
        catch (ManagementException ex)
        {
            PresentMonCaptureService.WriteDiagnostic($"Service status query failed for {serviceName}: {ex.Message}");
        }
        return null;
    }

    private static bool IsNvidiaFrameViewPresentMonRunning()
    {
        try
        {
            using var serviceSearcher = new ManagementObjectSearcher(
                "root\\CIMV2", "SELECT ProcessId FROM Win32_Service WHERE Name='FvSvc' AND State='Running'");
            using var services = serviceSearcher.Get();
            uint serviceProcessId = 0;
            foreach (ManagementObject service in services)
            using (service)
            {
                serviceProcessId = Convert.ToUInt32(service["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                break;
            }

            if (serviceProcessId == 0)
                return false;

            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2", "SELECT ParentProcessId FROM Win32_Process WHERE Name='PresentMon_x64.exe'");
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            using (row)
                if (Convert.ToUInt32(row["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture) == serviceProcessId)
                    return true;
        }
        catch (ManagementException ex)
        {
            PresentMonCaptureService.WriteDiagnostic($"FrameView process query failed: {ex.Message}");
        }
        return false;
    }

    private static bool ContainsSession(string output, string name) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static string GetIssueKey(CaptureExternalIssue issue) => $"{issue.Kind}:{issue.SessionName}";

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left.Value > right.Value ? left : right;

    private static int CleanupOrphanedCollectors()
    {
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe"));
        var applicationPath = Environment.ProcessPath;
        var killed = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2", "SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process");
            using var rows = searcher.Get();
            var processes = rows.Cast<ManagementObject>().ToArray();
            var activeApplicationPids = new HashSet<uint> { (uint)Environment.ProcessId };
            if (!string.IsNullOrWhiteSpace(applicationPath))
            {
                foreach (var row in processes)
                    if (string.Equals(row["ExecutablePath"] as string, applicationPath, StringComparison.OrdinalIgnoreCase))
                        activeApplicationPids.Add(Convert.ToUInt32(row["ProcessId"], System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (var row in processes)
            {
                var path = row["ExecutablePath"] as string;
                var commandLine = row["CommandLine"] as string;
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(commandLine) ||
                    !string.Equals(Path.GetFullPath(path), executable, StringComparison.OrdinalIgnoreCase))
                    continue;

                var parentPid = Convert.ToUInt32(row["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                var match = OrphanSessionPattern.Match(commandLine);
                if (!match.Success || !uint.TryParse(match.Groups["parent"].Value, out var sessionParentPid) || sessionParentPid != parentPid ||
                    activeApplicationPids.Contains(parentPid))
                    continue;

                var sessionName = $"PerfOverlay-{sessionParentPid}-{match.Groups["guid"].Value}";
                var processId = Convert.ToInt32(row["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                PresentMonCaptureService.StopTraceSession(sessionName);
                try
                {
                    using var orphan = Process.GetProcessById(processId);
                    if (!orphan.HasExited)
                        orphan.Kill(entireProcessTree: true);
                    killed++;
                    PresentMonCaptureService.WriteDiagnostic($"Cleaned orphaned PresentMon PID {processId}, parent PID {parentPid}, session {sessionName}.");
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    PresentMonCaptureService.WriteDiagnostic($"Could not clean orphaned PresentMon PID {processId}: {ex.Message}");
                }
            }

            var sessionOutput = QueryEtwSessionsAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (sessionOutput is not null)
            {
                foreach (var line in sessionOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = OrphanSessionNamePattern.Match(line);
                    if (!match.Success || !uint.TryParse(match.Groups["parent"].Value, out var parentPid) ||
                        activeApplicationPids.Contains(parentPid))
                        continue;

                    var sessionName = $"PerfOverlay-{parentPid}-{match.Groups["guid"].Value}";
                    if (!PresentMonCaptureService.StopTraceSession(sessionName))
                        continue;

                    killed++;
                    PresentMonCaptureService.WriteDiagnostic($"Stopped orphaned PerfOverlay ETW session {sessionName}; parent PID {parentPid} is not running this application.");
                }
            }

            foreach (var row in processes)
                row.Dispose();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or InvalidOperationException or ArgumentException)
        {
            PresentMonCaptureService.WriteDiagnostic($"Orphaned collector scan failed: {ex.Message}");
        }
        return killed;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _monitorTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _monitorGate.Dispose();
        _cancellation.Dispose();
    }
}
