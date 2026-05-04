/// <summary>
/// MetaCyber Agent Watchdog - نسخة macOS
///
/// يعمل كـ LaunchDaemon مستقل ويتولى:
/// 1. مراقبة استمرارية عمل MetacyberAgentService
/// 2. إعادة تشغيله عبر launchctl إذا توقف
/// 3. إرسال tamper notification إذا توقف بشكل غير طبيعي
///
/// ملاحظة: على macOS، launchd يتولى إعادة التشغيل تلقائياً
/// عبر KeepAlive=true في plist، لذا هذا الـ Watchdog
/// يُضيف طبقة حماية إضافية فقط.
/// </summary>

using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// ─── إعدادات ─────────────────────────────────────────────────────────
const string ServiceName      = "com.metacyber.agent";
const string ServiceBinaryPath = "/Library/MetacyberAgent/MetacyberAgentService";
const string LogPath          = "/Library/Logs/MetacyberAgent/watchdog.log";
const string FlagFilePath     = "/Library/Application Support/MetacyberAgent/graceful_shutdown.flag";
const string ConfigPath       = "/Library/MetacyberAgent/appsettings.json";
const int    CHECK_INTERVAL_MS = 30_000;  // 30 ثانية
const int    FLAG_VALIDITY_MIN = 5;

// ─── قراءة الإعدادات ─────────────────────────────────────────────────
string serverUrl = string.Empty;
string productId = string.Empty;

try
{
    if (File.Exists(ConfigPath))
    {
        var configJson = File.ReadAllText(ConfigPath);
        using var doc  = JsonDocument.Parse(configJson);
        var root       = doc.RootElement;
        if (root.TryGetProperty("AgentSettings", out var settings))
        {
            serverUrl = settings.GetProperty("ServerUrl").GetString() ?? string.Empty;
            productId = settings.GetProperty("ProductId").GetString() ?? string.Empty;
        }
    }
}
catch { }

Log($"Watchdog بدأ - Service: {ServiceName}");

// ─── حلقة المراقبة ───────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// معالجة إشارات POSIX
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

while (!cts.Token.IsCancellationRequested)
{
    try
    {
        bool isRunning = IsServiceRunning();

        if (!isRunning)
        {
            Log("⚠️ MetacyberAgentService توقف عن العمل");

            bool isGraceful = IsGracefulShutdown();
            if (!isGraceful)
            {
                Log("🚨 توقف غير طبيعي - إرسال tamper notification");
                await SendTamperAsync(serverUrl);
            }
            else
            {
                Log("ℹ️ توقف طبيعي - لا tamper");
            }

            // إعادة التشغيل عبر launchctl
            Log("🔄 إعادة تشغيل الخدمة...");
            RestartService();
        }

        await Task.Delay(CHECK_INTERVAL_MS, cts.Token);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Log($"خطأ: {ex.Message}");
        await Task.Delay(10_000, cts.Token);
    }
}

Log("Watchdog توقف");

// ─── دوال مساعدة ─────────────────────────────────────────────────────

static bool IsServiceRunning()
{
    try
    {
        // فحص عبر launchctl list
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "launchctl",
            Arguments = $"list {ServiceName}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
        proc?.WaitForExit(3000);

        // إذا وُجد الـ service في القائمة فهو يعمل
        return output.Contains("\"PID\"") || !output.Contains("Could not find service");
    }
    catch { }

    // فحص بديل: البحث عن العملية مباشرة
    try
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("MetacyberAgentService");
        return procs.Length > 0;
    }
    catch { return false; }
}

static void RestartService()
{
    try
    {
        // محاولة إعادة التشغيل عبر launchctl kickstart
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "launchctl",
            Arguments = $"kickstart -k system/{ServiceName}",
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit(5000);
        Log("✅ تم إرسال أمر إعادة التشغيل");
    }
    catch (Exception ex)
    {
        Log($"فشل إعادة التشغيل: {ex.Message}");
    }
}

static bool IsGracefulShutdown()
{
    try
    {
        if (!File.Exists(FlagFilePath)) return false;
        string content = File.ReadAllText(FlagFilePath).Trim();
        if (DateTime.TryParse(content, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime flagTime))
        {
            return (DateTime.UtcNow - flagTime).TotalMinutes <= FLAG_VALIDITY_MIN;
        }
        var fileTime = File.GetLastWriteTimeUtc(FlagFilePath);
        return (DateTime.UtcNow - fileTime).TotalMinutes <= FLAG_VALIDITY_MIN;
    }
    catch { return false; }
}

static async Task SendTamperAsync(string serverUrl)
{
    if (string.IsNullOrEmpty(serverUrl)) return;
    try
    {
        string username = GetConsoleUser() ?? Environment.UserName;
        var entry = new
        {
            username,
            deviceName = Environment.MachineName,
            ipAddress  = GetLocalIpAddress(),
            tamperType = "serviceKilled",
            timestamp  = DateTime.UtcNow
        };
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var json    = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        await client.PostAsync($"{serverUrl}/api/tamper/logs", content);
        Log("✅ تم إرسال tamper notification");
    }
    catch (Exception ex)
    {
        Log($"فشل إرسال tamper: {ex.Message}");
    }
}

static string? GetConsoleUser()
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
        return (string.IsNullOrEmpty(user) || user == "root") ? null : user;
    }
    catch { return null; }
}

static string GetLocalIpAddress()
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

static void Log(string message)
{
    try
    {
        string? dir = Path.GetDirectoryName(LogPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        File.AppendAllText(LogPath, line + Environment.NewLine);
        Console.WriteLine(line);
    }
    catch { }
}
