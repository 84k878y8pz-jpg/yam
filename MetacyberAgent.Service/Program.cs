using MetacyberAgent.Service;
using Microsoft.Extensions.Logging.EventLog;
using Serilog;

// ═══════════════════════════════════════════════════════════════════════
// MetaCyber Agent - macOS Edition
// يعمل في وضعين:
//   1. Service Mode  : يُشغَّل بواسطة launchd (root) في الخلفية
//   2. UI Mode       : يُشغَّل في جلسة المستخدم لعرض العلامة المائية
// ═══════════════════════════════════════════════════════════════════════

// ✅ FIX v3.5.4: Mutex مختلف لكل وضع لمنع التعارض بين Service و UI
// المشكلة السابقة: كلا الوضعين كانا يستخدمان نفس الـ Mutex مما يجعل
// Agent UI يخرج فوراً لأن الخدمة الرئيسية تحتجز الـ Mutex بالفعل.
bool isUiMode = args.Contains("--ui");

string mutexName = isUiMode
    ? $"MetacyberAgent_macOS_UI_{Environment.UserName}"
    : "MetacyberAgent_macOS_Service";

using var mutex = new Mutex(true, mutexName, out bool isFirstInstance);
if (!isFirstInstance)
{
    Console.Error.WriteLine(isUiMode
        ? $"MetaCyber Agent UI يعمل بالفعل للمستخدم {Environment.UserName}."
        : "MetaCyber Agent Service يعمل بالفعل.");
    return;
}

try
{
    bool isUiModeCheck = isUiMode; // متغير محلي للاستخدام داخل try

    if (isUiModeCheck)
    {
        // ═══════════════════════════════════════════════════════════════
        // وضع UI: يعمل في جلسة المستخدم لعرض العلامة المائية
        // يُشغَّل من LaunchAgent (per-user) أو من الـ Service مباشرة
        // ═══════════════════════════════════════════════════════════════
        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "MetacyberAgent", "ui-.log");

        // إنشاء مجلد السجلات إذا لم يكن موجوداً
        string? uiLogDir = Path.GetDirectoryName(logPath);
        if (uiLogDir != null && !Directory.Exists(uiLogDir))
            Directory.CreateDirectory(uiLogDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger);
        builder.Services.AddHostedService<AgentUiWorker>();

        var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("═══════════════════════════════════════════");
        logger.LogInformation("MetaCyber Agent - UI Mode (macOS) v3.5.4");
        logger.LogInformation("المستخدم: {User}", Environment.UserName);
        logger.LogInformation("═══════════════════════════════════════════");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception in UI mode");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogCritical(e.Exception, "Unobserved task exception in UI mode");
            e.SetObserved();
        };

        await host.RunAsync();
    }
    else
    {
        // ═══════════════════════════════════════════════════════════════
        // وضع Service: يعمل كـ LaunchDaemon (root) في الخلفية
        // يُشغَّل بواسطة launchd عند بدء تشغيل النظام
        // ═══════════════════════════════════════════════════════════════
        string serviceLogPath = Path.Combine(
            "/Library/Logs",
            "MetacyberAgent",
            "service-.log");

        // إنشاء مجلد السجلات إذا لم يكن موجوداً
        string? logDir = Path.GetDirectoryName(serviceLogPath);
        if (logDir != null && !Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                serviceLogPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger);
        builder.Services.AddHostedService<AgentWorker>();

        var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("═══════════════════════════════════════════");
        logger.LogInformation("MetaCyber Agent - Service Mode (macOS/launchd) v3.5.4");
        logger.LogInformation("═══════════════════════════════════════════");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception in Service mode");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogCritical(e.Exception, "Unobserved task exception in Service mode");
            e.SetObserved();
        };

        await host.RunAsync();
    }
}
finally
{
    Log.CloseAndFlush();
    try { mutex.ReleaseMutex(); } catch { }
}
