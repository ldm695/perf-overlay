using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PerfOverlay.Capture;

public sealed class PresentMonCaptureService : IDisposable
{
    private static readonly string DiagnosticPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PerfOverlay", "capture.log");
    private readonly object _lifecycleGate = new();
    private Process? _process;
    private string? _sessionName;
    private int _frameTimeColumn = -1;
    private int _untilDisplayedColumn = -1;
    private int _pcLatencyColumn = -1;
    private int _processIdColumn = -1;
    private int _simulationIntervalColumn = -1;
    private int _timeInMsColumn = -1;
    private int _targetProcessId;
    private string? _executablePath;
    private long _captureStartedAtUtcTicks;
    private long _targetStartedAtUtcTicks;
    private long _lastTargetFrameAtUtcTicks;
    private string? _lastFailure;

    public event Action<PresentMonFrameSample>? FrameDataReceived;
    public event Action<string>? CaptureFailed;
    public int TargetProcessId => Volatile.Read(ref _targetProcessId);
    public DateTimeOffset? CaptureStartedAt => ReadTimestamp(ref _captureStartedAtUtcTicks);
    public DateTimeOffset? TargetStartedAt => ReadTimestamp(ref _targetStartedAtUtcTicks);
    public DateTimeOffset? LastTargetFrameAt => ReadTimestamp(ref _lastTargetFrameAtUtcTicks);
    public string? LastFailure => Volatile.Read(ref _lastFailure);
    public bool IsRunning
    {
        get
        {
            var process = Volatile.Read(ref _process);
            try { return process is not null && !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public void SetTargetProcessId(int? processId)
    {
        var newPid = processId ?? 0;
        var previousPid = Interlocked.Exchange(ref _targetProcessId, newPid);
        if (previousPid == newPid)
            return;

        Interlocked.Exchange(ref _targetStartedAtUtcTicks, newPid > 0 ? DateTimeOffset.UtcNow.UtcTicks : 0);
        Interlocked.Exchange(ref _lastTargetFrameAtUtcTicks, 0);
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            StopCore();
            StartCore();
        }
    }

    public void Restart() => Start();

    private void StartCore()
    {

        var executable = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "找不到 PresentMon。请将官方 PresentMon x64 控制台程序放到 tools\\PresentMon\\PresentMon.exe，或使用包含该文件的发布包。",
                executable);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        _sessionName = $"PerfOverlay-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _executablePath = executable;
        Interlocked.Exchange(ref _captureStartedAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Volatile.Write(ref _lastFailure, null);
        startInfo.ArgumentList.Add("--session_name");
        startInfo.ArgumentList.Add(_sessionName);
        startInfo.ArgumentList.Add("--output_stdout");
        startInfo.ArgumentList.Add("--no_console_stats");
        startInfo.ArgumentList.Add("--track_pc_latency");
        startInfo.ArgumentList.Add("--track_app_timing");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += Process_OutputDataReceived;
        process.ErrorDataReceived += Process_ErrorDataReceived;
        process.Exited += Process_Exited;

        _process = process;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("PresentMon 启动失败。");

            _frameTimeColumn = -1;
            _untilDisplayedColumn = -1;
            _pcLatencyColumn = -1;
            _processIdColumn = -1;
            _simulationIntervalColumn = -1;
            _timeInMsColumn = -1;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            Volatile.Write(ref _lastFailure, "PresentMon failed to start.");
            if (ReferenceEquals(_process, process))
                _process = null;
            _sessionName = null;
            _executablePath = null;
            process.Exited -= Process_Exited;
            process.OutputDataReceived -= Process_OutputDataReceived;
            process.ErrorDataReceived -= Process_ErrorDataReceived;
            process.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        lock (_lifecycleGate)
            StopCore();
    }

    private void StopCore()
    {
        var process = _process;
        _process = null;
        var sessionName = _sessionName;
        _sessionName = null;
        var executablePath = _executablePath;
        _executablePath = null;
        _frameTimeColumn = -1;
        _untilDisplayedColumn = -1;
        _pcLatencyColumn = -1;
        _processIdColumn = -1;
        _simulationIntervalColumn = -1;
        _timeInMsColumn = -1;

        if (!string.IsNullOrWhiteSpace(sessionName) && executablePath is not null)
            StopTraceSession(sessionName);

        if (process is null)
            return;

        process.Exited -= Process_Exited;
        process.OutputDataReceived -= Process_OutputDataReceived;
        process.ErrorDataReceived -= Process_ErrorDataReceived;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    internal static bool StopTraceSession(string sessionName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "logman.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("stop");
            startInfo.ArgumentList.Add(sessionName);
            startInfo.ArgumentList.Add("-ets");
            using var stopper = Process.Start(startInfo);
            if (stopper is null)
                return false;

            var stdout = stopper.StandardOutput.ReadToEndAsync();
            var stderr = stopper.StandardError.ReadToEndAsync();
            if (!stopper.WaitForExit(5000))
            {
                try { stopper.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                WriteDiagnostic($"Timed out while stopping ETW session {sessionName}.");
                return false;
            }

            Task.WaitAll(stdout, stderr);
            if (stopper.ExitCode == 0)
                return true;

            var errorMessage = (stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult()).Trim();
            WriteDiagnostic($"Could not stop ETW session {sessionName}: {errorMessage}");
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            WriteDiagnostic($"Could not stop ETW session {sessionName}: {ex.Message}");
            return false;
        }

    }

    private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not { Length: > 0 } line)
            return;

        var fields = ParseCsvLine(line);
        if (_frameTimeColumn < 0 || _processIdColumn < 0)
        {
            var headerIndex = fields.FindIndex(x => x.Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase));
            if (headerIndex < 0)
                headerIndex = fields.FindIndex(x => x.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
            if (headerIndex < 0)
                headerIndex = fields.FindIndex(x => x.Equals("FrameTime", StringComparison.OrdinalIgnoreCase));
            if (headerIndex >= 0 && fields.Any(x => x.Equals("Application", StringComparison.OrdinalIgnoreCase)))
            {
                _frameTimeColumn = headerIndex;
                _untilDisplayedColumn = fields.FindIndex(x => x.Equals("MsUntilDisplayed", StringComparison.OrdinalIgnoreCase));
                _pcLatencyColumn = fields.FindIndex(x => x.Equals("MsPCLatency", StringComparison.OrdinalIgnoreCase));
                _processIdColumn = fields.FindIndex(x => x.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
                _simulationIntervalColumn = fields.FindIndex(x => x.Equals("MsBetweenSimulationStart", StringComparison.OrdinalIgnoreCase));
                _timeInMsColumn = fields.FindIndex(x => x.Equals("TimeInMs", StringComparison.OrdinalIgnoreCase));
            }
            return;
        }

        var targetPid = Volatile.Read(ref _targetProcessId);
        if (targetPid <= 0 || _processIdColumn >= fields.Count ||
            !int.TryParse(fields[_processIdColumn], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowPid) || rowPid != targetPid)
            return;

        double? frameTimeMs = TryReadMetric(fields, _frameTimeColumn);
        double? pcLatencyMs = _pcLatencyColumn >= 0 ? TryReadMetric(fields, _pcLatencyColumn) : null;
        double? simulationInterval = TryReadMetric(fields, _simulationIntervalColumn);
        double? untilDisplayed = TryReadMetric(fields, _untilDisplayedColumn);
        double? presentTimeMs = TryReadMetric(fields, _timeInMsColumn);
        double? displayTimeMs = presentTimeMs is double presentTime && untilDisplayed is double untilDisplay
            ? presentTime + untilDisplay
            : presentTimeMs;
        var sample = new PresentMonFrameSample(
            presentTimeMs,
            displayTimeMs,
            frameTimeMs,
            simulationInterval,
            pcLatencyMs);
        if (frameTimeMs is > 0)
            Interlocked.Exchange(ref _lastTargetFrameAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        FrameDataReceived?.Invoke(sample);
    }

    private static double? TryReadMetric(IReadOnlyList<string> fields, int column)
    {
        if (column < 0 || column >= fields.Count ||
            !double.TryParse(fields[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
            return null;

        return value;
    }

    private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is { Length: > 0 } text)
        {
            if (text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("failed", StringComparison.OrdinalIgnoreCase))
                Volatile.Write(ref _lastFailure, text.Trim());
            WriteDiagnostic($"PresentMon stderr: {text}");
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        var process = sender as Process;
        var code = -1;
        try { code = process?.ExitCode ?? -1; }
        catch (InvalidOperationException) { }

        if (!ReferenceEquals(_process, process))
            return;

        _process = null;
        var sessionName = _sessionName;
        _sessionName = null;
        var executablePath = _executablePath;
        _executablePath = null;
        if (sessionName is not null && executablePath is not null)
            StopTraceSession(sessionName);

        var message = code == 0
            ? "PresentMon stopped unexpectedly."
            : $"PresentMon exited with code {code}. Check Performance Logs Users permissions and the target process.";
        Volatile.Write(ref _lastFailure, message);
        WriteDiagnostic(message);
        CaptureFailed?.Invoke(message);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        fields.Add(value.ToString());
        return fields;
    }

    internal static void WriteDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticPath)!);
            File.AppendAllText(DiagnosticPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static DateTimeOffset? ReadTimestamp(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }

    public void Dispose() => Stop();
}
