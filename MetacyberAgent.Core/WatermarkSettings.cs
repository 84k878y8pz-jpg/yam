using System.Text.Json.Serialization;

namespace MetacyberAgent.Core;

/// <summary>
/// إعدادات العلامة المائية المستلمة من API
/// </summary>
public class WatermarkSettings
{
    [JsonPropertyName("type")]
    public string WatermarkType { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("customTexts")]
    public List<string>? CustomTexts { get; set; }

    [JsonPropertyName("opacity")]
    public float Opacity { get; set; } = 0.5f;

    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; } = 14;

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FFFFFF";

    [JsonPropertyName("position")]
    public string Position { get; set; } = "bottomRight";

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("showUsername")]
    public bool ShowUsername { get; set; } = true;

    [JsonPropertyName("showDeviceName")]
    public bool ShowDeviceName { get; set; } = false;

    [JsonPropertyName("showDateTime")]
    public bool ShowDateTime { get; set; } = false;

    [JsonPropertyName("showIpAddress")]
    public bool ShowIpAddress { get; set; } = false;

    [JsonPropertyName("scrollSpeed")]
    public int ScrollSpeed { get; set; } = 50;

    [JsonPropertyName("backgroundColor")]
    public string? BackgroundColor { get; set; }

    [JsonPropertyName("backgroundOpacity")]
    public float BackgroundOpacity { get; set; } = 0.3f;

    /// <summary>
    /// الحصول على قائمة النصوص النشطة
    /// </summary>
    public List<string> GetActiveTexts()
    {
        var texts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Text))
            texts.Add(Text);
        if (CustomTexts != null)
            texts.AddRange(CustomTexts.Where(t => !string.IsNullOrWhiteSpace(t)));
        return texts;
    }

    public bool HasValidImage() =>
        WatermarkType == "image" && !string.IsNullOrWhiteSpace(ImageUrl);

    public bool IsScrollingText() =>
        WatermarkType == "scrollingText";

    public string GetTickerText()
    {
        var texts = GetActiveTexts();
        return texts.Count == 0 ? string.Empty : string.Join("   \u2022   ", texts);
    }

    public static bool IsUserInList(string username, List<ExcludedUserEntry>? userList)
    {
        if (string.IsNullOrEmpty(username) || userList == null || userList.Count == 0)
            return false;
        string userLower = username.ToLower().Trim();
        var entry = userList.FirstOrDefault(e => e?.Username?.ToLower().Trim() == userLower);
        return entry?.IsActiveNow() ?? false;
    }

    public static bool IsUserInListQuiet(string username, List<ExcludedUserEntry>? userList) =>
        IsUserInList(username, userList);
}

/// <summary>
/// نموذج استجابة API
/// </summary>
public class ApiResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("config")]
    public WatermarkSettings? Config { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("watermarkExcludedUsers")]
    public List<ExcludedUserEntry> WatermarkExcludedUsers { get; set; } = new();

    [JsonPropertyName("scpExcludedUsers")]
    public List<ExcludedUserEntry> ScpExcludedUsers { get; set; } = new();

    [JsonPropertyName("scp")]
    public ScpSettings? Scp { get; set; }
}

/// <summary>
/// إعدادات منع التصوير (SCP)
/// </summary>
public class ScpSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("defaultResponse")]
    public string DefaultResponse { get; set; } = "blank";

    [JsonPropertyName("detectVirtualMachine")]
    public bool DetectVirtualMachine { get; set; }

    [JsonPropertyName("detectSandbox")]
    public bool DetectSandbox { get; set; }

    [JsonPropertyName("blockScreenshots")]
    public bool BlockScreenshots { get; set; }

    [JsonPropertyName("blockRecording")]
    public bool BlockRecording { get; set; }

    [JsonPropertyName("blockRemoteSession")]
    public bool BlockRemoteSession { get; set; }

    [JsonPropertyName("pushVersion")]
    public int PushVersion { get; set; }
}

/// <summary>
/// كائن الاستثناء للمستخدم
/// </summary>
public class ExcludedUserEntry
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public string? EndDate { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    public bool IsActiveNow()
    {
        DateTime now = DateTime.Now;
        DateTime start = DateTime.MinValue;
        DateTime end = DateTime.MaxValue;
        if (!string.IsNullOrEmpty(StartDate)) DateTime.TryParse(StartDate, out start);
        if (!string.IsNullOrEmpty(EndDate)) DateTime.TryParse(EndDate, out end);
        return now >= start && now <= end;
    }
}
