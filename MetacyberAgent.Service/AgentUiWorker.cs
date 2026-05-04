using MetacyberAgent.Core;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MetacyberAgent.Service;

/// <summary>
/// Worker عرض العلامة المائية في جلسة المستخدم - نسخة macOS
///
/// يعمل في سياق المستخدم الحقيقي ويتولى:
/// 1. جلب إعدادات العلامة المائية من API
/// 2. عرض العلامة المائية عبر WatermarkOverlayApp (NSWindow)
/// 3. إرسال حالة الاتصال للخادم
/// 4. مراقبة تغييرات الإعدادات
/// 5. رصد أحداث التقاط الشاشة والطباعة
/// </summary>
public class AgentUiWorker : BackgroundService
{
    private readonly ILogger<AgentUiWorker> _logger;
    private readonly IConfiguration _config;

    private ApiService? _apiService;
    private AgentStatusService? _statusService;
    private CaptureLogService? _captureLogService;
    private PrintLogService? _printLogService;
    private ScpEnforcementService? _scpService;
    private WatermarkOverlayApp? _overlayApp;

    private ApiResponse? _currentSettings;
    private Timer? _settingsRefreshTimer;

    // أوقات الانتظار المحسّنة (v3.5.3)
    private const int SETTINGS_REFRESH_INTERVAL_MS = 60_000; // دقيقة

    public AgentUiWorker(ILogger<AgentUiWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string serverUrl = _config["AgentSettings:ServerUrl"] ?? string.Empty;
        string productId = _config["AgentSettings:ProductId"] ?? string.Empty;

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(productId))
        {
            _logger.LogError("إعدادات ServerUrl أو ProductId مفقودة");
            return;
        }

        // ✅ v3.5.3 FIX: التحقق من أن اسم المستخدم ليس حساب نظام
        string username = GetRealUsername();
        _logger.LogInformation("Agent UI يعمل للمستخدم: {User}", username);

        // تهيئة الخدمات
        _apiService = new ApiService(serverUrl, productId, _logger);

        // انتظار الشبكة
        await _apiService.WaitForNetworkAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        // جلب الإعدادات
        _currentSettings = await _apiService.FetchSettingsAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        // ✅ FIX v3.5.5: عدم الخروج عند فشل جلب الإعدادات
        // السلوك السابق: return عند null → يخرج Agent UI → Watchdog يعيد التشغيل → حلقة لا نهاية لها
        // السلوك الجديد: عرض علامة مائية افتراضية + إعادة المحاولة دورياً
        if (_currentSettings == null)
        {
            _logger.LogWarning("⚠️ فشل جلب الإعدادات - سيتم عرض علامة مائية افتراضية وإعادة المحاولة كل دقيقة");

            // عرض علامة مائية افتراضية بدلاً من الخروج
            _overlayApp = new WatermarkOverlayApp(_logger);
            _overlayApp.ApplyFallback(username);

            // إعادة المحاولة دورياً حتى تنجح
            _settingsRefreshTimer = new Timer(
                async _ => await RetryFetchAndApplyAsync(username, serverUrl, productId, stoppingToken),
                null,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60));

            // تهيئة خدمة الحالة بإصدار افتراضي
            _statusService = new AgentStatusService(serverUrl, productId, _logger);
            _statusService.StartReporting(username, "offline");

            stoppingToken.Register(() =>
            {
                _logger.LogInformation("تم استقبال إشارة إيقاف Agent UI (fallback mode)");
                GracefulShutdownHelper.SignalGracefulShutdown();
            });

            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        // التحقق من استثناء المستخدم
        if (WatermarkSettings.IsUserInList(username, _currentSettings.WatermarkExcludedUsers))
        {
            _logger.LogInformation("المستخدم {User} مستثنى من العلامة المائية", username);
        }
        else if (_currentSettings.IsActive && _currentSettings.Config != null)
        {
            // عرض العلامة المائية
            _overlayApp = new WatermarkOverlayApp(_logger);
            _overlayApp.ApplySettings(_currentSettings.Config, username);
            _logger.LogInformation("✅ تم تطبيق العلامة المائية للمستخدم: {User}", username);
        }
        else
        {
            _logger.LogInformation("العلامة المائية غير نشطة (IsActive={Active})", _currentSettings.IsActive);
        }

        // تهيئة خدمة الحالة
        _statusService = new AgentStatusService(serverUrl, productId, _logger);
        _statusService.StartReporting(username, _currentSettings.Version);

        // تهيئة خدمة رصد التقاط الشاشة
        _captureLogService = new CaptureLogService(serverUrl, username, _logger);
        _captureLogService.StartMonitoring();

        // تهيئة خدمة رصد الطباعة
        _printLogService = new PrintLogService(serverUrl, username, _logger);
        _printLogService.StartMonitoring();

        // تهيئة SCP
        _scpService = new ScpEnforcementService(_logger);
        if (!WatermarkSettings.IsUserInList(username, _currentSettings.ScpExcludedUsers))
        {
            _scpService.ApplySettings(_currentSettings.Scp);
            _scpService.CaptureDetected += (_, processName) =>
            {
                _ = _apiService.SendCaptureLogAsync(new CaptureLogEntry
                {
                    Username    = username,
                    DeviceName  = Environment.MachineName,
                    IpAddress   = GetLocalIpAddress(),
                    CaptureType = $"blocked:{processName}",
                    Status      = "blocked"
                });
            };
        }

        // تحديث دوري للإعدادات
        _settingsRefreshTimer = new Timer(
            async _ => await RefreshSettingsAsync(username, stoppingToken),
            null,
            TimeSpan.FromMilliseconds(SETTINGS_REFRESH_INTERVAL_MS),
            TimeSpan.FromMilliseconds(SETTINGS_REFRESH_INTERVAL_MS));

        // معالجة إشارة الإيقاف
        stoppingToken.Register(() =>
        {
            _logger.LogInformation("تم استقبال إشارة إيقاف Agent UI");
            GracefulShutdownHelper.SignalGracefulShutdown();
        });

        // انتظار الإيقاف
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// ✅ FIX v3.5.5: إعادة المحاولة بعد فشل الاتصال الأول
    /// يستمر المحاولة حتى تنجح ثم يطبق الإعدادات ويوقف التايمر
    /// </summary>
    private async Task RetryFetchAndApplyAsync(string username, string serverUrl, string productId, CancellationToken ct) // serverUrl/productId reserved for future use
    {
        try
        {
            if (_apiService == null) return;
            var settings = await _apiService.FetchSettingsAsync(ct);
            if (settings == null) return;

            _logger.LogInformation("✅ نجح جلب الإعدادات بعد الاسترداد - تطبيق الإعدادات الحقيقية");
            _currentSettings = settings;

            // إيقاف التايمر الاحتياطي
            _settingsRefreshTimer?.Dispose();

            // تطبيق الإعدادات الحقيقية
            if (!WatermarkSettings.IsUserInList(username, settings.WatermarkExcludedUsers) &&
                settings.IsActive && settings.Config != null)
            {
                _overlayApp?.ApplySettings(settings.Config, username);
            }
            else
            {
                _overlayApp?.Hide();
            }

            // تحديث خدمة الحالة
            _statusService?.UpdateUsername(username);

            // تفعيل التحديث الدوري العادي
            _settingsRefreshTimer = new Timer(
                async _ => await RefreshSettingsAsync(username, ct),
                null,
                TimeSpan.FromMilliseconds(SETTINGS_REFRESH_INTERVAL_MS),
                TimeSpan.FromMilliseconds(SETTINGS_REFRESH_INTERVAL_MS));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("فشل RetryFetch: {Msg}", ex.Message);
        }
    }

    private async Task RefreshSettingsAsync(string username, CancellationToken ct)
    {
        try
        {
            if (_apiService == null) return;
            var newSettings = await _apiService.FetchSettingsAsync(ct);
            if (newSettings == null) return;

            // تحديث إذا تغير الإصدار
            if (newSettings.Version != _currentSettings?.Version)
            {
                _logger.LogInformation(
                    "تحديث الإعدادات: v{Old} → v{New}",
                    _currentSettings?.Version, newSettings.Version);

                _currentSettings = newSettings;

                if (newSettings.IsActive && newSettings.Config != null &&
                    !WatermarkSettings.IsUserInList(username, newSettings.WatermarkExcludedUsers))
                {
                    _overlayApp?.ApplySettings(newSettings.Config, username);
                }
                else
                {
                    _overlayApp?.Hide();
                }

                _statusService?.UpdateUsername(username);
                _scpService?.ApplySettings(newSettings.Scp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("فشل تحديث الإعدادات: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// الحصول على اسم المستخدم الحقيقي (v3.5.3 FIX)
    /// </summary>
    private static string GetRealUsername()
    {
        // AgentUiWorker يعمل في جلسة المستخدم الحقيقي
        // Environment.UserName دائماً صحيح في هذا السياق
        string username = Environment.UserName;

        // التحقق من أنه ليس حساب نظام
        if (string.IsNullOrEmpty(username) ||
            username.Equals("root", StringComparison.OrdinalIgnoreCase) ||
            username.EndsWith("$"))
        {
            // محاولة الحصول على الاسم من $USER
            username = Environment.GetEnvironmentVariable("USER") ?? username;
        }

        return username;
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            return ip.Address.ToString();
                    }
                }
            }
        }
        catch { }
        return "unknown";
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("إيقاف Agent UI...");
        _settingsRefreshTimer?.Dispose();
        _overlayApp?.Hide();
        _captureLogService?.Dispose();
        _printLogService?.Dispose();
        _scpService?.Dispose();
        _statusService?.Dispose();
        _apiService?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// تطبيق العلامة المائية على macOS
/// يستخدم NSWindow عبر P/Invoke لـ AppKit/CoreGraphics
/// </summary>
public class WatermarkOverlayApp : IDisposable
{
    private readonly ILogger _logger;
    private System.Diagnostics.Process? _overlayProcess;
    private WatermarkSettings? _currentSettings;
    private string _currentUsername = string.Empty;

    // مسار سكريبت العلامة المائية
    private static readonly string OverlayScriptPath =
        "/Library/MetacyberAgent/watermark_overlay.py";

    public WatermarkOverlayApp(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// ✅ FIX v3.5.5: عرض علامة مائية افتراضية عند فشل الاتصال بالخادم
    /// يعرض اسم المستخدم بشفافية منخفضة حتى يتم الاتصال بالخادم
    /// </summary>
    public void ApplyFallback(string username)
    {
        _currentUsername = username;
        StopOverlay();

        try
        {
            if (File.Exists(OverlayScriptPath))
            {
                string watermarkText = username;
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = "python3",
                    Arguments = $"\"{OverlayScriptPath}\" " +
                                $"--text \"{EscapeArg(watermarkText)}\" " +
                                $"--opacity 0.3 " +
                                $"--font-size 14 " +
                                $"--color \"#FFFFFF\" " +
                                $"--position \"bottomRight\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true
                };
                _overlayProcess = System.Diagnostics.Process.Start(psi);
                _logger.LogInformation("✅ علامة مائية افتراضية (وضع لا اتصال): {Text}", watermarkText);
            }
            else
            {
                _logger.LogWarning("سكريبت العلامة المائية غير موجود: {Path}", OverlayScriptPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("فشل تشغيل العلامة الافتراضية: {Msg}", ex.Message);
        }
    }

    public void ApplySettings(WatermarkSettings settings, string username)
    {
        _currentSettings = settings;
        _currentUsername = username;

        // إنهاء العملية السابقة
        StopOverlay();

        // بناء نص العلامة المائية
        string watermarkText = BuildWatermarkText(settings, username);

        try
        {
            // تشغيل سكريبت Python للعلامة المائية
            if (File.Exists(OverlayScriptPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = "python3",
                    Arguments = $"\"{OverlayScriptPath}\" " +
                                $"--text \"{EscapeArg(watermarkText)}\" " +
                                $"--opacity {settings.Opacity} " +
                                $"--font-size {settings.FontSize} " +
                                $"--color \"{settings.Color}\" " +
                                $"--position \"{settings.Position}\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true
                };
                _overlayProcess = System.Diagnostics.Process.Start(psi);
                _logger.LogInformation("✅ تم تشغيل العلامة المائية: {Text}", watermarkText);
            }
            else
            {
                _logger.LogWarning("سكريبت العلامة المائية غير موجود: {Path}", OverlayScriptPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("فشل تشغيل العلامة المائية: {Msg}", ex.Message);
        }
    }

    private static string BuildWatermarkText(WatermarkSettings settings, string username)
    {
        var parts = new List<string>();

        if (settings.ShowUsername && !string.IsNullOrEmpty(username))
            parts.Add(username);

        if (settings.ShowDeviceName)
            parts.Add(Environment.MachineName);

        if (settings.ShowDateTime)
            parts.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        if (!string.IsNullOrEmpty(settings.Text))
            parts.Add(settings.Text);

        return parts.Count > 0 ? string.Join(" | ", parts) : username;
    }

    private static string EscapeArg(string arg) =>
        arg.Replace("\"", "\\\"").Replace("'", "\\'");

    public void Hide() => StopOverlay();

    private void StopOverlay()
    {
        try
        {
            if (_overlayProcess != null && !_overlayProcess.HasExited)
            {
                _overlayProcess.Kill();
                _overlayProcess.WaitForExit(3000);
            }
        }
        catch { }
        finally
        {
            _overlayProcess?.Dispose();
            _overlayProcess = null;
        }
    }

    public void Dispose() => StopOverlay();
}
