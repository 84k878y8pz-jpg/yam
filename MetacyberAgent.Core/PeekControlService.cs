using Microsoft.Extensions.Logging;

namespace MetacyberAgent.Core;

/// <summary>
/// خدمة التحكم في إمكانية رؤية العلامة المائية - نسخة macOS
///
/// على macOS تُستخدم NSWindow APIs عبر P/Invoke لـ CoreGraphics
/// لضمان أن العلامة المائية تظهر دائماً فوق جميع النوافذ
/// وتُدرج في لقطات الشاشة.
/// </summary>
public class PeekControlService
{
    private readonly ILogger _logger;
    private bool _isEnabled = true;

    public PeekControlService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// تفعيل وضع "دائماً في المقدمة" للعلامة المائية
    /// على macOS: NSWindowLevel.screenSaver + CGWindowLevel
    /// </summary>
    public void EnableAlwaysOnTop()
    {
        _isEnabled = true;
        _logger.LogInformation("تفعيل وضع Always-On-Top للعلامة المائية");
        // يتم التطبيق الفعلي في WatermarkOverlayApp عبر NSWindow APIs
    }

    public void DisableAlwaysOnTop()
    {
        _isEnabled = false;
    }

    public bool IsEnabled => _isEnabled;
}
