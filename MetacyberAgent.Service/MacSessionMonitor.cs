namespace MetacyberAgent.Service;

/// <summary>
/// بيانات حدث جلسة macOS
/// </summary>
public class MacSessionEventArgs : EventArgs
{
    public string Username { get; init; } = string.Empty;
}

/// <summary>
/// مراقب جلسات المستخدمين على macOS
/// يستبدل WtsSessionMonitor (Windows) بمراقبة Console User عبر:
/// 1. فحص دوري لـ /dev/console owner
/// 2. مراقبة NSDistributedNotificationCenter (عبر script خارجي)
///
/// على macOS لا يوجد WTS API، لذا نستخدم:
/// - stat -f %Su /dev/console : لمعرفة المستخدم الحالي
/// - scutil --get ComputerName : للتحقق من اسم الجهاز
/// </summary>
public class MacSessionMonitor : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _serverUrl;
    private readonly string _productId;

    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private string _lastKnownUser = string.Empty;

    private const int POLL_INTERVAL_MS = 5000; // فحص كل 5 ثوانٍ

    public event EventHandler<MacSessionEventArgs>? UserLoggedIn;
    public event EventHandler<MacSessionEventArgs>? UserLoggedOut;

    public MacSessionMonitor(ILogger logger, string serverUrl, string productId)
    {
        _logger    = logger;
        _serverUrl = serverUrl;
        _productId = productId;
    }

    public void Start()
    {
        // تهيئة المستخدم الحالي
        _lastKnownUser = GetConsoleUser() ?? string.Empty;

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
        _logger.LogInformation("بدأ مراقبة جلسات macOS (Console User Polling)");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? currentUser = GetConsoleUser();

                if (currentUser != _lastKnownUser)
                {
                    // تغيّر المستخدم
                    if (!string.IsNullOrEmpty(_lastKnownUser) && string.IsNullOrEmpty(currentUser))
                    {
                        // تسجيل خروج
                        _logger.LogInformation("🚪 اكتشاف تسجيل خروج: {User}", _lastKnownUser);
                        UserLoggedOut?.Invoke(this, new MacSessionEventArgs { Username = _lastKnownUser });
                    }
                    else if (string.IsNullOrEmpty(_lastKnownUser) && !string.IsNullOrEmpty(currentUser))
                    {
                        // تسجيل دخول جديد
                        _logger.LogInformation("🔑 اكتشاف تسجيل دخول: {User}", currentUser);
                        // تأخير 5 ثوانٍ لضمان اكتمال جلسة المستخدم (v3.5.3)
                        await Task.Delay(5000, ct);
                        UserLoggedIn?.Invoke(this, new MacSessionEventArgs { Username = currentUser });
                    }
                    else if (!string.IsNullOrEmpty(currentUser) && currentUser != _lastKnownUser)
                    {
                        // تبديل مستخدم (Fast User Switching)
                        _logger.LogInformation("🔄 تبديل مستخدم: {Old} → {New}", _lastKnownUser, currentUser);
                        UserLoggedOut?.Invoke(this, new MacSessionEventArgs { Username = _lastKnownUser });
                        await Task.Delay(5000, ct);
                        UserLoggedIn?.Invoke(this, new MacSessionEventArgs { Username = currentUser });
                    }

                    _lastKnownUser = currentUser ?? string.Empty;
                }

                await Task.Delay(POLL_INTERVAL_MS, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("خطأ في مراقبة الجلسة: {Msg}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private static string? GetConsoleUser()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "stat",
                Arguments = "-f %Su /dev/console",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string? user = proc?.StandardOutput.ReadLine()?.Trim();
            proc?.WaitForExit(2000);

            // root لا يُعدّ مستخدماً حقيقياً (شاشة القفل أو قبل تسجيل الدخول)
            if (string.IsNullOrEmpty(user) || user == "root") return null;
            return user;
        }
        catch { return null; }
    }

    public void Dispose() => Stop();
}
