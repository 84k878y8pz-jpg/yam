using MetacyberAgent.Core;

namespace MetacyberAgent.Service;

/// <summary>
/// حارس العمليات على macOS - بديل ProcessGuardService (Windows)
///
/// يتولى:
/// 1. تتبع عمليات Agent UI النشطة
/// 2. الكشف عن إنهاء العملية بشكل غير طبيعي
/// 3. إرسال tamper notification عند الاكتشاف
/// 4. deduplication لمنع إرسال tamper مزدوج (v3.5.3)
/// </summary>
public class MacProcessGuard : IDisposable
{
    private readonly ILogger _logger;
    private readonly TamperLogService _tamperService;

    // قاموس: username → PID
    private readonly Dictionary<string, int> _trackedProcesses = new();
    private readonly object _lock = new();

    // deduplication (v3.5.3)
    private readonly Dictionary<string, DateTime> _lastTamperSent = new();
    private const int TAMPER_DEDUP_SECONDS = 10;

    private CancellationTokenSource? _cts;
    private Task? _watchTask;
    private const int WATCH_INTERVAL_MS = 5000;

    public MacProcessGuard(ILogger logger, string serverUrl)
    {
        _logger       = logger;
        _tamperService = new TamperLogService(serverUrl, logger);

        _cts = new CancellationTokenSource();
        _watchTask = Task.Run(() => WatchLoopAsync(_cts.Token));
    }

    public void RegisterProcess(string username, int pid)
    {
        lock (_lock)
        {
            _trackedProcesses[username] = pid;
            _logger.LogInformation("تسجيل Agent UI: {User} → PID {Pid}", username, pid);
        }
    }

    public bool IsAgentUiRunning(string username)
    {
        lock (_lock)
        {
            if (!_trackedProcesses.TryGetValue(username, out int pid)) return false;
            return IsProcessAlive(pid);
        }
    }

    public void TerminateAgentUi(string username)
    {
        lock (_lock)
        {
            if (!_trackedProcesses.TryGetValue(username, out int pid)) return;

            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                proc.Kill();
                _logger.LogInformation("تم إنهاء Agent UI للمستخدم {User} (PID: {Pid})", username, pid);
            }
            catch { /* العملية ربما انتهت بالفعل */ }

            _trackedProcesses.Remove(username);
        }
    }

    public void ReportTamper(string username)
    {
        if (!ShouldSendTamper(username)) return;

        _ = _tamperService.SendTamperAsync(username, "processKilled", username);
        RecordTamperSent(username);
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                CheckTrackedProcesses();
                await Task.Delay(WATCH_INTERVAL_MS, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("خطأ في WatchLoop: {Msg}", ex.Message);
            }
        }
    }

    private void CheckTrackedProcesses()
    {
        List<string> deadUsers = new();

        lock (_lock)
        {
            foreach (var (username, pid) in _trackedProcesses)
            {
                if (!IsProcessAlive(pid))
                    deadUsers.Add(username);
            }
        }

        foreach (var username in deadUsers)
        {
            _logger.LogWarning("⚠️ Agent UI توقف للمستخدم: {User}", username);

            // ✅ v3.5.3: لا tamper عند الإيقاف الطبيعي
            if (!GracefulShutdownHelper.IsGracefulShutdown())
            {
                if (ShouldSendTamper(username))
                {
                    _logger.LogWarning("🚨 إرسال tamper: Agent UI قُتل قسراً للمستخدم {User}", username);
                    _ = _tamperService.SendTamperAsync(username, "processKilled", username);
                    RecordTamperSent(username);
                }
            }

            lock (_lock)
            {
                _trackedProcesses.Remove(username);
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch { return false; }
    }

    private bool ShouldSendTamper(string key)
    {
        lock (_lastTamperSent)
        {
            if (_lastTamperSent.TryGetValue(key, out DateTime last))
                return (DateTime.UtcNow - last).TotalSeconds >= TAMPER_DEDUP_SECONDS;
            return true;
        }
    }

    private void RecordTamperSent(string key)
    {
        lock (_lastTamperSent)
        {
            _lastTamperSent[key] = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _watchTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts?.Dispose();
        _tamperService.Dispose();
    }
}
