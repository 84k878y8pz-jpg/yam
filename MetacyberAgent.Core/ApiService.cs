using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MetacyberAgent.Core;

/// <summary>
/// خدمة التواصل مع API الخادم - نسخة macOS
/// </summary>
public class ApiService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;
    private readonly string _productId;
    private readonly ILogger _logger;

    // أوقات الانتظار المحسّنة (v3.5.3)
    private const int NETWORK_WAIT_MAX_MS   = 15_000;
    private const int NETWORK_CHECK_INTERVAL_MS = 500;
    private const int HTTP_TIMEOUT_SECONDS  = 8;
    private const int RETRY_DELAY_SECONDS   = 3;
    private const int MAX_RETRIES           = 3;

    public ApiService(string serverUrl, string productId, ILogger logger)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _productId = productId;
        _logger    = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS)
        };
    }

    /// <summary>
    /// انتظار توفر الشبكة قبل الاتصال بالـ API
    /// </summary>
    public async Task WaitForNetworkAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < NETWORK_WAIT_MAX_MS && !ct.IsCancellationRequested)
        {
            if (NetworkInterface.GetIsNetworkAvailable())
                return;
            await Task.Delay(NETWORK_CHECK_INTERVAL_MS, ct);
        }
        _logger.LogWarning("انتهت مهلة انتظار الشبكة ({Ms}ms)", NETWORK_WAIT_MAX_MS);
    }

    /// <summary>
    /// جلب إعدادات العلامة المائية من الـ API مع إعادة المحاولة
    /// </summary>
    public async Task<ApiResponse?> FetchSettingsAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
        {
            try
            {
                var url = $"{_serverUrl}/api/products/{_productId}";
                _logger.LogInformation("جلب الإعدادات (محاولة {A}/{M}): {Url}", attempt, MAX_RETRIES, url);
                var response = await _httpClient.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var result = JsonSerializer.Deserialize<ApiResponse>(json);
                    _logger.LogInformation("تم جلب الإعدادات بنجاح (v{V})", result?.Version);
                    return result;
                }
                _logger.LogWarning("HTTP {Code} من الـ API", (int)response.StatusCode);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("فشل الاتصال (محاولة {A}): {Msg}", attempt, ex.Message);
            }

            if (attempt < MAX_RETRIES)
                await Task.Delay(TimeSpan.FromSeconds(RETRY_DELAY_SECONDS), ct);
        }
        return null;
    }

    /// <summary>
    /// إرسال سجل التقاط الشاشة
    /// </summary>
    public async Task SendCaptureLogAsync(CaptureLogEntry entry)
    {
        try
        {
            var url = $"{_serverUrl}/api/capture/logs";
            _logger.LogInformation("[HTTP POST] {Url}", url);
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            _logger.LogInformation("[HTTP POST] {Url} → {Code}", url, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("فشل إرسال سجل التقاط: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// إرسال سجل الطباعة
    /// </summary>
    public async Task SendPrintLogAsync(PrintLogEntry entry)
    {
        try
        {
            var url = $"{_serverUrl}/api/print/logs";
            _logger.LogInformation("[HTTP POST] {Url}", url);
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            _logger.LogInformation("[HTTP POST] {Url} → {Code}", url, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("فشل إرسال سجل الطباعة: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// إرسال سجل العبث (Tamper)
    /// </summary>
    public async Task SendTamperLogAsync(TamperLogEntry entry)
    {
        try
        {
            var url = $"{_serverUrl}/api/tamper/logs";
            _logger.LogInformation("[HTTP POST] {Url}", url);
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            _logger.LogInformation("[HTTP POST] {Url} → {Code}", url, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("فشل إرسال سجل العبث: {Msg}", ex.Message);
        }
    }

    public void Dispose() => _httpClient?.Dispose();
}

/// <summary>
/// سجل التقاط الشاشة
/// </summary>
public class CaptureLogEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("captureType")]
    public string CaptureType { get; set; } = "screenshot";

    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "blocked";
}

/// <summary>
/// سجل الطباعة
/// </summary>
public class PrintLogEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("printerName")]
    public string? PrinterName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("pageCount")]
    public int? PageCount { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("fileSize")]
    public string? FileSize { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "sent";
}

/// <summary>
/// سجل العبث
/// </summary>
public class TamperLogEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("tamperType")]
    public string TamperType { get; set; } = "processKilled";

    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
