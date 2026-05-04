using MetacyberAgent.Core;

namespace MetacyberAgent.Service;

/// <summary>
/// الـ Worker الرئيسي للخدمة - نسخة macOS
///
/// يعمل كـ LaunchDaemon (root) ويتولى:
/// 1. مراقبة جلسات المستخدمين عبر SCDynamicStore / Console User API
/// 2. إطلاق AgentUiWorker في جلسة المستخدم الحالي
/// 3. مراقبة استمرارية عمل الـ Agent UI (Watchdog)
/// 4. إعادة تشغيل الـ Agent UI عند الحاجة
/// </summary>
public class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _logger;
    private readonly IConfiguration _config;

    private MacSessionMonitor? _sessionMonitor;
    private MacProcessGuard? _processGuard;
    private Timer? _watchdogTimer;

    // إعدادات الـ Watchdog
    private const int WATCHDOG_INTERVAL_MS   = 30_000;  // 30 ثانية
    private const int SESSION_POLL_TIMEOUT_MS = 120_000; // دقيقتان
    private const int SESSION_POLL_INTERVAL_MS = 2_000;  // ثانيتان

    // ✅ FIX v3.5.4: تأخير قبل قراءة PID الفعلي لـ Agent UI
    private const int UI_LAUNCH_PID_WAIT_MS = 2_500;

    public AgentWorker(ILogger<AgentWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("═══════════════════════════════════════════");
        _logger.LogInformation("MetaCyber Agent Service بدأ التشغيل (macOS)");
        _logger.LogInformation("═══════════════════════════════════════════");

        // إعادة تعيين علم الإيقاف الطبيعي
        GracefulShutdownHelper.ResetFlag();

        string serverUrl  = _config["AgentSettings:ServerUrl"]  ?? string.Empty;
        string productId  = _config["AgentSettings:ProductId"]  ?? string.Empty;

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(productId))
        {
            _logger.LogError("إعدادات ServerUrl أو ProductId مفقودة في appsettings.json");
            return;
        }

        // تهيئة مراقب الجلسات
        _sessionMonitor = new MacSessionMonitor(_logger, serverUrl, productId);
        _sessionMonitor.UserLoggedIn  += OnUserLoggedIn;
        _sessionMonitor.UserLoggedOut += OnUserLoggedOut;
        _sessionMonitor.Start();

        // تهيئة حارس العمليات
        _processGuard = new MacProcessGuard(_logger, serverUrl);

        // تشغيل Watchdog
        _watchdogTimer = new Timer(
            WatchdogCallback,
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(WATCHDOG_INTERVAL_MS));

        // محاولة إطلاق Agent UI في الجلسة الحالية
        await LaunchInCurrentSessionWithRetryAsync(stoppingToken);

        // انتظار إشارة الإيقاف
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("تم استقبال إشارة الإيقاف");
            GracefulShutdownHelper.SignalGracefulShutdown();
        }
    }

    private void OnUserLoggedIn(object? sender, MacSessionEventArgs e)
    {
        _logger.LogInformation("🔑 مستخدم سجّل دخول: {User} (Console)", e.Username);
        // تأخير 5 ثوانٍ لضمان اكتمال جلسة المستخدم (v3.5.3)
        Task.Delay(5000).ContinueWith(_ => LaunchAgentUiForUser(e.Username));
    }

    private void OnUserLoggedOut(object? sender, MacSessionEventArgs e)
    {
        _logger.LogInformation("🚪 مستخدم سجّل خروج: {User}", e.Username);
        _processGuard?.TerminateAgentUi(e.Username);
    }

    private void WatchdogCallback(object? state)
    {
        try
        {
            string? consoleUser = GetConsoleUser();
            if (string.IsNullOrEmpty(consoleUser)) return;

            bool isRunning = _processGuard?.IsAgentUiRunning(consoleUser) ?? false;
            if (!isRunning)
            {
                _logger.LogWarning("🐕 Watchdog: Agent UI غير موجود - إعادة التشغيل...");

                // فحص إذا كان الإيقاف طبيعياً
                if (!GracefulShutdownHelper.IsGracefulShutdown())
                {
                    _logger.LogWarning("⚠️ Agent UI توقف بشكل غير طبيعي!");
                    _processGuard?.ReportTamper(consoleUser);
                }

                LaunchAgentUiForUser(consoleUser);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("خطأ في Watchdog: {Msg}", ex.Message);
        }
    }

    private async Task LaunchInCurrentSessionWithRetryAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int attempt = 0;

        while (!ct.IsCancellationRequested && sw.ElapsedMilliseconds < SESSION_POLL_TIMEOUT_MS)
        {
            attempt++;
            string? consoleUser = GetConsoleUser();

            if (!string.IsNullOrEmpty(consoleUser))
            {
                _logger.LogInformation(
                    "✅ وُجد مستخدم Console: {User} (محاولة {A})", consoleUser, attempt);
                LaunchAgentUiForUser(consoleUser);
                return;
            }

            _logger.LogInformation(
                "محاولة {A}: لا يوجد مستخدم Console بعد ({Ms}ms)...",
                attempt, sw.ElapsedMilliseconds);

            try { await Task.Delay(SESSION_POLL_INTERVAL_MS, ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogWarning("انتهت مهلة البحث عن جلسة Console - MacSessionMonitor سيتولى الاكتشاف");
    }

    /// <summary>
    /// إطلاق Agent UI في سياق المستخدم المحدد
    /// على macOS: استخدام launchctl asuser
    /// ✅ FIX v3.5.4: تسجيل PID الفعلي لـ Agent UI بدلاً من PID لـ launchctl
    /// </summary>
    private void LaunchAgentUiForUser(string username)
    {
        try
        {
            // الحصول على UID للمستخدم
            int uid = GetUserUid(username);
            if (uid < 0)
            {
                _logger.LogWarning("تعذر الحصول على UID للمستخدم: {User}", username);
                return;
            }

            string agentPath = GetAgentExecutablePath();
            if (!File.Exists(agentPath))
            {
                _logger.LogError("ملف Agent غير موجود: {Path}", agentPath);
                return;
            }

            // إطلاق Agent UI في سياق المستخدم عبر launchctl asuser
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "launchctl",
                Arguments       = $"asuser {uid} \"{agentPath}\" --ui",
                UseShellExecute = false,
                CreateNoWindow  = true
            };

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                // ✅ FIX v3.5.4: انتظار بدء Agent UI الفعلي ثم تسجيل PID الصحيح
                // launchctl ينتهي فوراً بعد إطلاق العملية الهدف، لذا نبحث عن PID الفعلي
                Task.Delay(UI_LAUNCH_PID_WAIT_MS).ContinueWith(_ =>
                {
                    int realPid = GetAgentUiPid(username);
                    int pidToRegister = realPid > 0 ? realPid : proc.Id;

                    _processGuard?.RegisterProcess(username, pidToRegister);
                    _logger.LogInformation(
                        "✅ تم إطلاق Agent UI للمستخدم {User} (UID: {Uid}, PID: {Pid}){Source}",
                        username, uid, pidToRegister,
                        realPid > 0 ? " [PID فعلي]" : " [PID launchctl - احتياطي]");
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("فشل إطلاق Agent UI للمستخدم {User}: {Msg}", username, ex.Message);
        }
    }

    /// <summary>
    /// ✅ FIX v3.5.4: الحصول على PID الفعلي لعملية Agent UI النشطة
    /// يستخدم pgrep للبحث عن العملية بعد الإطلاق
    /// </summary>
    private static int GetAgentUiPid(string username)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "pgrep",
                Arguments              = $"-u {username} -f \"MetacyberAgentService --ui\"",
                RedirectStandardOutput = true,
                UseShellExecute        = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string? output = proc?.StandardOutput.ReadLine()?.Trim();
            proc?.WaitForExit(2000);

            if (int.TryParse(output, out int pid) && pid > 0)
                return pid;
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// الحصول على مستخدم Console الحالي على macOS
    /// </summary>
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

            if (string.IsNullOrEmpty(user) || user == "root") return null;
            return user;
        }
        catch { return null; }
    }

    private static int GetUserUid(string username)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "id",
                Arguments = $"-u {username}",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string? output = proc?.StandardOutput.ReadLine()?.Trim();
            proc?.WaitForExit(2000);

            if (int.TryParse(output, out int uid)) return uid;
        }
        catch { }
        return -1;
    }

    private static string GetAgentExecutablePath()
    {
        // المسار الافتراضي للتثبيت على macOS
        string defaultPath = "/Library/MetacyberAgent/MetacyberAgentService";

        // البحث في نفس مجلد الملف الحالي (للتطوير)
        string? currentDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);
        if (currentDir != null)
        {
            string localPath = Path.Combine(currentDir, "MetacyberAgentService");
            if (File.Exists(localPath)) return localPath;
        }

        return defaultPath;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("جاري إيقاف الخدمة...");
        GracefulShutdownHelper.SignalGracefulShutdown();

        _watchdogTimer?.Dispose();
        _sessionMonitor?.Stop();
        _processGuard?.Dispose();

        _logger.LogInformation("تم إيقاف الخدمة بنجاح");
        await base.StopAsync(cancellationToken);
    }
}
