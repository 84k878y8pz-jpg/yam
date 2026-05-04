using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MetacyberAgent.Core;

/// <summary>
/// خدمة تسجيل أحداث التقاط الشاشة - نسخة macOS
///
/// على macOS يتم الرصد عبر:
/// 1. مراقبة مجلد ~/Desktop و~/Downloads لملفات Screenshot
/// 2. مراقبة مجلد الـ Screenshots الافتراضي
/// 3. FileSystemWatcher على مسارات الحفظ المعروفة
/// </summary>
public class CaptureLogService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;
    private readonly string _username;
    private readonly ILogger _logger;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Queue<CaptureLogEntry> _pendingLogs = new();
    private bool _isMonitoring = false;

    // مسارات حفظ Screenshots الافتراضية على macOS
    private static readonly string[] ScreenshotPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
        "/tmp"
    };

    public CaptureLogService(string serverUrl, string username, ILogger logger)
    {
        _serverUrl  = serverUrl.TrimEnd('/');
        _username   = username;
        _logger     = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public void StartMonitoring()
    {
        if (_isMonitoring) return;
        _isMonitoring = true;

        foreach (var path in ScreenshotPaths)
        {
            try
            {
                if (!Directory.Exists(path)) continue;

                var watcher = new FileSystemWatcher(path)
                {
                    Filter = "*.png",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnScreenshotCreated;
                _watchers.Add(watcher);
                _logger.LogInformation("مراقبة مجلد Screenshots: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("تعذر مراقبة {Path}: {Msg}", path, ex.Message);
            }
        }
    }

    public void StopMonitoring()
    {
        _isMonitoring = false;
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    private void OnScreenshotCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            // فحص إذا كان الاسم يشبه Screenshot
            string fileName = Path.GetFileName(e.FullPath);
            bool isScreenshot = fileName.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase) ||
                                fileName.StartsWith("Screen Shot", StringComparison.OrdinalIgnoreCase) ||
                                fileName.Contains("screenshot", StringComparison.OrdinalIgnoreCase);

            if (!isScreenshot) return;

            _logger.LogWarning("📸 اكتشاف Screenshot: {File}", fileName);

            var entry = new CaptureLogEntry
            {
                Username   = _username,
                DeviceName = Environment.MachineName,
                IpAddress  = GetLocalIpAddress(),
                CaptureType = "screenshot",
                Timestamp  = DateTime.UtcNow,
                Status     = "detected"
            };

            _ = SendLogAsync(entry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("خطأ في معالجة حدث Screenshot: {Msg}", ex.Message);
        }
    }

    private async Task SendLogAsync(CaptureLogEntry entry)
    {
        try
        {
            var json    = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_serverUrl}/api/capture/logs", content);

            if (response.IsSuccessStatusCode)
                await SendPendingLogsAsync();
            else
                QueueLog(entry);
        }
        catch
        {
            QueueLog(entry);
        }
    }

    private void QueueLog(CaptureLogEntry entry)
    {
        lock (_pendingLogs)
        {
            if (_pendingLogs.Count < 100)
                _pendingLogs.Enqueue(entry);
        }
    }

    private async Task SendPendingLogsAsync()
    {
        while (true)
        {
            CaptureLogEntry? entry;
            lock (_pendingLogs)
            {
                if (_pendingLogs.Count == 0) break;
                entry = _pendingLogs.Dequeue();
            }
            try
            {
                var json    = JsonSerializer.Serialize(entry, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{_serverUrl}/api/capture/logs", content);
            }
            catch
            {
                lock (_pendingLogs) { _pendingLogs.Enqueue(entry); }
                break;
            }
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

    public void Dispose()
    {
        StopMonitoring();
        _httpClient?.Dispose();
    }
}
