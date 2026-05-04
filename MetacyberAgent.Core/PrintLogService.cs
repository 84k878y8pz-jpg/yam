using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MetacyberAgent.Core;

/// <summary>
/// خدمة تسجيل أحداث الطباعة - نسخة macOS
///
/// على macOS يتم رصد الطباعة عبر:
/// 1. مراقبة CUPS (Common Unix Printing System) spool directory
/// 2. مراقبة /var/spool/cups لملفات الطباعة الجديدة
/// 3. تحليل سجلات CUPS من /var/log/cups/access_log
/// </summary>
public class PrintLogService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;
    private readonly string _username;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private readonly Queue<PrintLogEntry> _pendingLogs = new();

    // مسار سجل CUPS على macOS
    private const string CupsAccessLog = "/var/log/cups/access_log";
    private long _lastLogPosition = 0;

    public PrintLogService(string serverUrl, string username, ILogger logger)
    {
        _serverUrl  = serverUrl.TrimEnd('/');
        _username   = username;
        _logger     = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public void StartMonitoring()
    {
        // تهيئة موضع القراءة من نهاية الملف الحالي
        try
        {
            if (File.Exists(CupsAccessLog))
                _lastLogPosition = new FileInfo(CupsAccessLog).Length;
        }
        catch { }

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
        _logger.LogInformation("بدأ مراقبة طباعة CUPS على macOS");
    }

    public void StopMonitoring()
    {
        _cts?.Cancel();
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckCupsLogAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("خطأ في مراقبة CUPS: {Msg}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
    }

    private async Task CheckCupsLogAsync()
    {
        try
        {
            if (!File.Exists(CupsAccessLog)) return;

            long currentSize = new FileInfo(CupsAccessLog).Length;
            if (currentSize <= _lastLogPosition) return;

            using var stream = new FileStream(CupsAccessLog,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(_lastLogPosition, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                ParseCupsLogLine(line);
            }

            _lastLogPosition = stream.Position;
        }
        catch (UnauthorizedAccessException)
        {
            // سجل CUPS يتطلب صلاحيات - نستخدم lpstat بدلاً منه
            await CheckPrintJobsViaLpstatAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("خطأ في قراءة سجل CUPS: {Msg}", ex.Message);
        }
    }

    private void ParseCupsLogLine(string line)
    {
        // تنسيق سجل CUPS: [date] [level] [message]
        // مثال: localhost - - [01/Jan/2025:10:00:00 +0000] "POST /printers/HP_LaserJet HTTP/1.1" 200 1234 Create-Job successful-ok
        if (!line.Contains("Create-Job") && !line.Contains("Print-Job")) return;

        try
        {
            // استخراج اسم الطابعة
            string printerName = "Unknown";
            var printerMatch = System.Text.RegularExpressions.Regex.Match(line, @"/printers/([^\s]+)");
            if (printerMatch.Success)
                printerName = printerMatch.Groups[1].Value;

            var entry = new PrintLogEntry
            {
                // AgentUiWorker يعمل في جلسة المستخدم الحقيقي
                // لذا Environment.UserName دائماً صحيح
                Username    = Environment.UserName,
                DeviceName  = Environment.MachineName,
                IpAddress   = GetLocalIpAddress(),
                FileName    = "Print Job",
                PrinterName = printerName,
                Status      = "sent"
            };

            _ = SendLogAsync(entry);
        }
        catch { }
    }

    private async Task CheckPrintJobsViaLpstatAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "lpstat",
                Arguments = "-W completed -l",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
            proc?.WaitForExit(3000);

            if (!string.IsNullOrWhiteSpace(output))
                _logger.LogDebug("lpstat output: {Output}", output.Substring(0, Math.Min(200, output.Length)));
        }
        catch { }
        await Task.CompletedTask;
    }

    private async Task SendLogAsync(PrintLogEntry entry)
    {
        try
        {
            var url = $"{_serverUrl}/api/print/logs";
            _logger.LogInformation("[HTTP POST] {Url}", url);
            var json    = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            _logger.LogInformation("[HTTP POST] {Url} → {Code}", url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
                await SendPendingLogsAsync();
            else
                QueueLog(entry);
        }
        catch { QueueLog(entry); }
    }

    private void QueueLog(PrintLogEntry entry)
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
            PrintLogEntry? entry;
            lock (_pendingLogs)
            {
                if (_pendingLogs.Count == 0) break;
                entry = _pendingLogs.Dequeue();
            }
            try
            {
                var url = $"{_serverUrl}/api/print/logs";
                _logger.LogInformation("[HTTP POST] {Url} (retry)", url);
                var json    = JsonSerializer.Serialize(entry, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync(url, content);
                _logger.LogInformation("[HTTP POST] {Url} → {Code}", url, (int)resp.StatusCode);
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
        _cts?.Dispose();
        _httpClient?.Dispose();
    }
}
