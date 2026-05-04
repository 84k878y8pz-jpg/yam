namespace MetacyberAgent.Core;

/// <summary>
/// مساعد الإيقاف الطبيعي - نسخة macOS
///
/// يستبدل Named Events (Windows) بملف Flag مشترك بين العمليات.
/// مسار الملف: /Library/Application Support/MetacyberAgent/graceful_shutdown.flag
///
/// سيناريوهات الإيقاف الطبيعي (لا يُرسل tamper):
///   - تسجيل خروج المستخدم بشكل طبيعي
///   - إعادة تشغيل النظام
///   - إيقاف تشغيل النظام
///   - إيقاف الخدمة عبر launchctl
///
/// سيناريوهات العبث (يُرسل tamper):
///   - قتل العملية بـ kill -9
///   - حذف ملفات الـ Agent
/// </summary>
public static class GracefulShutdownHelper
{
    private static readonly string FlagFilePath = Path.Combine(
        "/Library/Application Support",
        "MetacyberAgent",
        "graceful_shutdown.flag");

    private const int FLAG_VALIDITY_MINUTES = 5;

    /// <summary>
    /// تعيين علم الإيقاف الطبيعي
    /// </summary>
    public static void SignalGracefulShutdown()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FlagFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(FlagFilePath, DateTime.UtcNow.ToString("O"));
        }
        catch { /* فشل الكتابة - نتجاهل */ }
    }

    /// <summary>
    /// التحقق مما إذا كان الإيقاف طبيعياً
    /// </summary>
    public static bool IsGracefulShutdown()
    {
        try
        {
            if (!File.Exists(FlagFilePath)) return false;

            string content = File.ReadAllText(FlagFilePath).Trim();

            if (DateTime.TryParse(content, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime flagTime))
            {
                return (DateTime.UtcNow - flagTime).TotalMinutes <= FLAG_VALIDITY_MINUTES;
            }

            // محتوى غير صالح - نتحقق من وقت الكتابة
            var fileTime = File.GetLastWriteTimeUtc(FlagFilePath);
            return (DateTime.UtcNow - fileTime).TotalMinutes <= FLAG_VALIDITY_MINUTES;
        }
        catch { return false; }
    }

    /// <summary>
    /// إعادة تعيين العلم بعد بدء الخدمة بنجاح
    /// </summary>
    public static void ResetFlag()
    {
        try
        {
            if (File.Exists(FlagFilePath))
                File.Delete(FlagFilePath);
        }
        catch { }
    }
}
