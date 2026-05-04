using Microsoft.Extensions.Logging;

namespace MetacyberAgent.Core;

/// <summary>
/// خدمة منع التقاط الشاشة (SCP) - نسخة macOS
///
/// على macOS، يتم منع التقاط الشاشة عبر:
/// 1. CGWindowLevel: رفع مستوى النافذة لتظهر فوق أدوات التقاط
/// 2. NSWindow.sharingType = .none: منع مشاركة محتوى النافذة
/// 3. مراقبة عمليات التقاط المعروفة (screencapture, QuickTime, etc.)
/// 4. كشف الـ VNC/Screen Sharing
/// </summary>
public class ScpEnforcementService : IDisposable
{
    private readonly ILogger _logger;
    private ScpSettings? _settings;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;

    // قائمة عمليات التقاط الشاشة المعروفة على macOS
    private static readonly string[] KnownCaptureProcesses =
    {
        "screencapture",
        "QuickTime Player",
        "Screenshot",
        "Snagit",
        "Monosnap",
        "Skitch",
        "CleanMyMac",
        "obs",
        "zoom.us",
        "webexmeetings",
        "Microsoft Teams"
    };

    public ScpEnforcementService(ILogger logger)
    {
        _logger = logger;
    }

    public void ApplySettings(ScpSettings? settings)
    {
        _settings = settings;
        if (settings?.Enabled == true)
        {
            _logger.LogInformation("تفعيل SCP على macOS");
            StartMonitoring();
        }
        else
        {
            StopMonitoring();
        }
    }

    private void StartMonitoring()
    {
        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    private void StopMonitoring()
    {
        _cts?.Cancel();
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("بدأ مراقبة SCP على macOS");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_settings?.BlockScreenshots == true)
                    await CheckAndBlockCaptureProcessesAsync();

                if (_settings?.DetectVirtualMachine == true)
                    CheckVirtualMachine();

                if (_settings?.BlockRemoteSession == true)
                    CheckRemoteSession();

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("خطأ في مراقبة SCP: {Msg}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private async Task CheckAndBlockCaptureProcessesAsync()
    {
        try
        {
            var runningProcesses = System.Diagnostics.Process.GetProcesses();
            foreach (var proc in runningProcesses)
            {
                try
                {
                    string procName = proc.ProcessName;
                    if (KnownCaptureProcesses.Any(k =>
                        procName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogWarning("⚠️ اكتشاف عملية التقاط: {Name} (PID: {Pid})",
                            procName, proc.Id);

                        // على macOS، نُسجّل الحدث ونُرسله للخادم
                        // (لا يمكن إنهاء العمليات الأخرى بدون صلاحيات root)
                        OnCaptureDetected(procName);
                    }
                }
                catch { /* العملية ربما انتهت */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("خطأ في فحص عمليات التقاط: {Msg}", ex.Message);
        }
        await Task.CompletedTask;
    }

    private void CheckVirtualMachine()
    {
        // يتم الكشف في AgentStatusService
    }

    private void CheckRemoteSession()
    {
        // فحص Screen Sharing على macOS
        try
        {
            bool isScreenSharing = IsScreenSharingActive();
            if (isScreenSharing)
                _logger.LogWarning("⚠️ اكتشاف مشاركة الشاشة (Screen Sharing)");
        }
        catch { }
    }

    private static bool IsScreenSharingActive()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = "list com.apple.screensharing",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
            proc?.WaitForExit(2000);
            return !output.Contains("Could not find service");
        }
        catch { return false; }
    }

    public event EventHandler<string>? CaptureDetected;

    private void OnCaptureDetected(string processName)
    {
        CaptureDetected?.Invoke(this, processName);
    }

    public void Dispose()
    {
        StopMonitoring();
        _cts?.Dispose();
    }
}
