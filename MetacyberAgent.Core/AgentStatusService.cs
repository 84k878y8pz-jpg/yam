using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MetacyberAgent.Core;

/// <summary>
/// خدمة إرسال حالة الـ Agent إلى الخادم - نسخة macOS
/// تستبدل WMI بـ macOS-native APIs
/// </summary>
public class AgentStatusService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;
    private readonly string _productId;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _reportingTask;
    private string _currentUsername = string.Empty;
    private int _currentPolicyVersion = 0;

    private const int REPORT_INTERVAL_SECONDS = 30;

    // ─── CPU tracking ───────────────────────────────────────────────
    private static DateTime _lastCpuCheck = DateTime.MinValue;
    private static TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
    private static readonly object _cpuLock = new();

    public AgentStatusService(string serverUrl, string productId, ILogger logger)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _productId = productId;
        _logger    = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public void StartReporting(string username, int policyVersion)
    {
        _currentUsername     = username;
        _currentPolicyVersion = policyVersion;

        _cts = new CancellationTokenSource();
        _reportingTask = Task.Run(() => ReportLoopAsync(_cts.Token));
        _logger.LogInformation("بدأ إرسال حالة الـ Agent للمستخدم: {User}", username);
    }

    public void StopReporting()
    {
        _cts?.Cancel();
        try { _reportingTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    public void UpdateUsername(string username)
    {
        _currentUsername = username;
    }

    private async Task ReportLoopAsync(CancellationToken ct)
    {
        // إرسال فوري عند البدء
        await SendStatusAsync("connected", ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(REPORT_INTERVAL_SECONDS), ct);
                await SendStatusAsync("connected", ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("خطأ في حلقة الإرسال: {Msg}", ex.Message);
            }
        }

        // إرسال حالة "غير متصل" عند الإيقاف
        await SendStatusAsync("disconnected", CancellationToken.None);
    }

    private async Task SendStatusAsync(string status, CancellationToken ct)
    {
        try
        {
            // ✅ v3.5.3 FIX: التحقق من أن اسم المستخدم ليس حساب نظام
            string username = _currentUsername;
            if (IsMachineOrSystemAccount(username))
            {
                username = GetRealUsername();
                if (IsMachineOrSystemAccount(username))
                {
                    _logger.LogWarning("تخطي الإرسال: لم يُعثر على مستخدم حقيقي");
                    return;
                }
            }

            var (cpu, mem) = GetResourceUsage();
            var report = new AgentStatusReport
            {
                DeviceName    = Environment.MachineName,
                Username      = username,
                Status        = status,
                MacAddress    = GetMacAddress(),
                IpAddress     = GetLocalIpAddress(),
                PolicyVersion = _currentPolicyVersion,
                IsVirtualMachine  = DetectVirtualMachine(),
                IsSandbox         = false,
                IsRemoteSession   = DetectRemoteSession(),
                CpuUsage          = cpu,
                MemoryUsage       = mem
            };

            var json    = JsonSerializer.Serialize(report, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            await _httpClient.PostAsync(
                $"{_serverUrl}/api/agents/status", content, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("فشل إرسال الحالة: {Msg}", ex.Message);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static bool IsMachineOrSystemAccount(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return true;
        if (username.Equals("root", StringComparison.OrdinalIgnoreCase)) return true;
        if (username.EndsWith("$")) return true;
        return false;
    }

    private static string GetRealUsername()
    {
        // على macOS: استخدام $USER أو logname
        try
        {
            var user = Environment.GetEnvironmentVariable("USER") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(user) && !IsMachineOrSystemAccount(user))
                return user;

            // محاولة عبر logname
            var psi = new System.Diagnostics.ProcessStartInfo("logname")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string? output = proc?.StandardOutput.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(output) && !IsMachineOrSystemAccount(output))
                return output;
        }
        catch { }
        return Environment.UserName;
    }

    /// <summary>
    /// كشف الآلة الافتراضية على macOS
    /// </summary>
    private static bool DetectVirtualMachine()
    {
        try
        {
            // فحص عبر system_profiler
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "system_profiler",
                Arguments = "SPHardwareDataType",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
            proc?.WaitForExit(3000);

            // مؤشرات الـ VM
            string[] vmIndicators = { "VMware", "VirtualBox", "Parallels", "QEMU", "Virtual Machine" };
            return vmIndicators.Any(v => output.Contains(v, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// كشف الجلسة البعيدة على macOS (SSH أو Screen Sharing)
    /// </summary>
    private static bool DetectRemoteSession()
    {
        // SSH session
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT"))) return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY")))    return true;
        return false;
    }

    /// <summary>
    /// قياس استخدام CPU و RAM على macOS
    /// </summary>
    public static (float cpuUsage, float memoryUsage) GetResourceUsage()
    {
        float cpu = 0, memory = 0;

        // CPU
        try
        {
            lock (_cpuLock)
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var now  = DateTime.UtcNow;
                var totalCpu = proc.TotalProcessorTime;

                if (_lastCpuCheck != DateTime.MinValue)
                {
                    double timeDiff = (now - _lastCpuCheck).TotalMilliseconds;
                    double cpuDiff  = (totalCpu - _lastTotalProcessorTime).TotalMilliseconds;
                    if (timeDiff > 0)
                    {
                        cpu = (float)(cpuDiff / (Environment.ProcessorCount * timeDiff) * 100);
                        cpu = Math.Clamp(cpu, 0, 100);
                    }
                }
                _lastCpuCheck = now;
                _lastTotalProcessorTime = totalCpu;
            }
        }
        catch { cpu = 0; }

        // Memory - استخدام vm_stat على macOS
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "vm_stat",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
            proc?.WaitForExit(2000);

            // استخراج الصفحات النشطة والمقيمة
            long pageSize = 4096; // الافتراضي على macOS
            long activePages = 0, wiredPages = 0, compressedPages = 0;

            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("Pages active:"))
                    long.TryParse(ExtractNumber(line), out activePages);
                else if (line.StartsWith("Pages wired down:"))
                    long.TryParse(ExtractNumber(line), out wiredPages);
                else if (line.StartsWith("Pages occupied by compressor:"))
                    long.TryParse(ExtractNumber(line), out compressedPages);
            }

            // إجمالي الذاكرة عبر sysctl
            var sysctl = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sysctl",
                Arguments = "-n hw.memsize",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var sysctlProc = System.Diagnostics.Process.Start(sysctl);
            string memStr = sysctlProc?.StandardOutput.ReadLine()?.Trim() ?? "0";
            sysctlProc?.WaitForExit(2000);

            if (long.TryParse(memStr, out long totalBytes) && totalBytes > 0)
            {
                long usedBytes = (activePages + wiredPages + compressedPages) * pageSize;
                memory = (float)usedBytes / totalBytes * 100;
                memory = Math.Clamp(memory, 0, 100);
            }
        }
        catch { memory = 0; }

        return (cpu, memory);
    }

    private static string ExtractNumber(string line)
    {
        var digits = new string(line.Where(c => char.IsDigit(c)).ToArray());
        return digits;
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

    private static string GetMacAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                {
                    var mac = ni.GetPhysicalAddress().ToString();
                    if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                        return string.Join(":", Enumerable.Range(0, 6)
                            .Select(i => mac.Substring(i * 2, 2)));
                }
            }
        }
        catch { }
        return "unknown";
    }

    public void Dispose()
    {
        StopReporting();
        _cts?.Dispose();
        _httpClient?.Dispose();
    }
}

public class AgentStatusReport
{
    [System.Text.Json.Serialization.JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "inactive";

    [System.Text.Json.Serialization.JsonPropertyName("macAddress")]
    public string? MacAddress { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("policyVersion")]
    public int? PolicyVersion { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("isVirtualMachine")]
    public bool IsVirtualMachine { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("isSandbox")]
    public bool IsSandbox { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("isRemoteSession")]
    public bool IsRemoteSession { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("cpuUsage")]
    public float? CpuUsage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("memoryUsage")]
    public float? MemoryUsage { get; set; }
}
