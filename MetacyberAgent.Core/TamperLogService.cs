using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MetacyberAgent.Core;

/// <summary>
/// خدمة تسجيل أحداث العبث - نسخة macOS
///
/// قواعد الإرسال (v3.5.3):
/// - لا يُرسل tamper عند تسجيل الخروج الطبيعي
/// - لا يُرسل tamper عند إعادة التشغيل أو الإيقاف
/// - لا يُرسل tamper عند تسجيل الدخول
/// - يُرسل tamper فقط عند قتل العملية قسراً
/// </summary>
public class TamperLogService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;
    private readonly ILogger _logger;

    // deduplication: منع إرسال tamper مزدوج (v3.5.3)
    private readonly Dictionary<string, DateTime> _lastTamperSent = new();
    private const int TAMPER_DEDUP_SECONDS = 10;

    public TamperLogService(string serverUrl, ILogger logger)
    {
        _serverUrl  = serverUrl.TrimEnd('/');
        _logger     = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>
    /// إرسال سجل عبث مع فحص deduplication
    /// </summary>
    public async Task SendTamperAsync(string username, string tamperType, string sessionKey = "default")
    {
        // ✅ v3.5.3: منع إرسال tamper مزدوج
        if (!ShouldSendTamper(sessionKey))
        {
            _logger.LogInformation("تخطي tamper مكرر للجلسة {Key}", sessionKey);
            return;
        }

        // ✅ لا tamper عند الإيقاف الطبيعي
        if (GracefulShutdownHelper.IsGracefulShutdown())
        {
            _logger.LogInformation("تخطي tamper: إيقاف طبيعي للنظام");
            return;
        }

        try
        {
            var entry = new TamperLogEntry
            {
                Username   = username,
                DeviceName = Environment.MachineName,
                IpAddress  = GetLocalIpAddress(),
                TamperType = tamperType,
                Timestamp  = DateTime.UtcNow
            };

            var url = $"{_serverUrl}/api/tamper/logs";
            _logger.LogInformation("[HTTP POST] {Url}", url);
            var json    = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            _logger.LogInformation("[HTTP POST] {Url} → {Code}", url, (int)response.StatusCode);
            if (response.IsSuccessStatusCode)
            {
                RecordTamperSent(sessionKey);
                _logger.LogWarning("⚠️ تم إرسال tamper [{Type}] للمستخدم {User}", tamperType, username);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("فشل إرسال tamper: {Msg}", ex.Message);
        }
    }

    private bool ShouldSendTamper(string sessionKey)
    {
        lock (_lastTamperSent)
        {
            if (_lastTamperSent.TryGetValue(sessionKey, out DateTime last))
            {
                if ((DateTime.UtcNow - last).TotalSeconds < TAMPER_DEDUP_SECONDS)
                    return false;
            }
            return true;
        }
    }

    private void RecordTamperSent(string sessionKey)
    {
        lock (_lastTamperSent)
        {
            _lastTamperSent[sessionKey] = DateTime.UtcNow;
        }
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

    public void Dispose() => _httpClient?.Dispose();
}
