using System.Globalization;
using System.Text.RegularExpressions;

namespace iucs.meettomanage.api.Marketing;

/// <summary>
/// Everything about how cold e-mail is sent, editable by the marketing team in the console. Stored as
/// <c>outreach.*</c> app settings in the marketing tenant; a value never saved falls back to the server's
/// configuration (Outreach:*), then to the built-in default.
/// </summary>
public sealed record OutreachSettings
{
    public string FromName { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string? ReplyTo { get; init; }
    public int DailyLimit { get; init; }
    /// <summary>Local hours (in <see cref="TimeZoneId"/>) between which e-mails go out, spread evenly.</summary>
    public int WindowStartHour { get; init; }
    public int WindowEndHour { get; init; }
    /// <summary>IANA id such as "Asia/Kolkata" or "Asia/Dubai".</summary>
    public string TimeZoneId { get; init; } = "Asia/Kolkata";
    /// <summary>Why the person is getting the e-mail; shown above the unsubscribe link.</summary>
    public string FooterText { get; init; } = "";
    /// <summary>Sender's name and postal location, shown under the unsubscribe link (expected by anti-spam rules).</summary>
    public string CompanyLine { get; init; } = "";
    /// <summary>Shortest gap allowed between a campaign's e-mails.</summary>
    public int MinFollowUpDays { get; init; }
    /// <summary>Failed sends of one step before the address is treated as bounced.</summary>
    public int MaxFailuresPerStep { get; init; }
    public bool EmailEnabled { get; init; } = true;
    public bool CampaignSendingEnabled { get; init; } = true;
    public bool ManualEmailEnabled { get; init; } = true;
    public bool ReplySendingEnabled { get; init; } = true;
    public bool ReplySyncEnabled { get; init; } = true;
    public bool AiEnabled { get; init; } = true;
    public bool AiLeadQualification { get; init; } = true;
    public bool AiMessageDrafting { get; init; } = true;
    public bool AiCampaignGeneration { get; init; } = true;
    public bool AiReplyAssistant { get; init; } = true;
    public bool AiInsights { get; init; } = true;

    public string From => $"{FromName.Replace("\"", "").Replace("<", "").Replace(">", "").Trim()} <{FromAddress.Trim()}>";

    public TimeZoneInfo Zone => TryZone(TimeZoneId) ?? TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static TimeZoneInfo? TryZone(string? id)
    {
        try { return string.IsNullOrWhiteSpace(id) ? null : TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    /// <summary>Splits "Name &lt;address&gt;" (or a bare address) into its parts.</summary>
    public static (string? Name, string? Address) ParseFrom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var m = Regex.Match(value.Trim(), @"^\s*""?(?<name>[^""<]*?)""?\s*<(?<addr>[^>]+)>\s*$");
        return m.Success ? (m.Groups["name"].Value.Trim() is { Length: > 0 } n ? n : null, m.Groups["addr"].Value.Trim()) : (null, value.Trim());
    }

    // ---------------------------------------------------------------- storage

    private const string P = OutreachService.KeyPrefix;

    public IEnumerable<(string Key, string Value)> ToSettings() =>
    [
        (P + "fromName", FromName.Trim()),
        (P + "fromAddress", FromAddress.Trim()),
        (P + "replyTo", ReplyTo?.Trim() ?? ""),
        (P + "dailyLimit", DailyLimit.ToString(CultureInfo.InvariantCulture)),
        (P + "windowStartHour", WindowStartHour.ToString(CultureInfo.InvariantCulture)),
        (P + "windowEndHour", WindowEndHour.ToString(CultureInfo.InvariantCulture)),
        (P + "timeZone", TimeZoneId.Trim()),
        (P + "footerText", FooterText.Trim()),
        (P + "companyLine", CompanyLine.Trim()),
        (P + "minFollowUpDays", MinFollowUpDays.ToString(CultureInfo.InvariantCulture)),
        (P + "maxFailuresPerStep", MaxFailuresPerStep.ToString(CultureInfo.InvariantCulture)),
        (P + "emailEnabled", EmailEnabled.ToString(CultureInfo.InvariantCulture)),
        (P + "campaignSendingEnabled", CampaignSendingEnabled.ToString(CultureInfo.InvariantCulture)),
        (P + "manualEmailEnabled", ManualEmailEnabled.ToString(CultureInfo.InvariantCulture)),
        (P + "replySendingEnabled", ReplySendingEnabled.ToString(CultureInfo.InvariantCulture)),
        (P + "replySyncEnabled", ReplySyncEnabled.ToString(CultureInfo.InvariantCulture)),
        (P + "aiEnabled", AiEnabled.ToString(CultureInfo.InvariantCulture)),
        (P + "aiLeadQualification", AiLeadQualification.ToString(CultureInfo.InvariantCulture)),
        (P + "aiMessageDrafting", AiMessageDrafting.ToString(CultureInfo.InvariantCulture)),
        (P + "aiCampaignGeneration", AiCampaignGeneration.ToString(CultureInfo.InvariantCulture)),
        (P + "aiReplyAssistant", AiReplyAssistant.ToString(CultureInfo.InvariantCulture)),
        (P + "aiInsights", AiInsights.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>These defaults with any saved values on top (a saved value that no longer validates is ignored).</summary>
    public OutreachSettings Overlay(IReadOnlyDictionary<string, string?> saved)
    {
        string? S(string key) => saved.TryGetValue(P + key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        int I(string key, int fallback, int min, int max) => int.TryParse(S(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : fallback;
        bool B(string key, bool fallback) => bool.TryParse(S(key), out var v) ? v : fallback;
        var merged = this with
        {
            FromName = S("fromName") ?? FromName,
            FromAddress = S("fromAddress") ?? FromAddress,
            // A saved empty reply-to means "no separate reply address" (replies go to the from address).
            ReplyTo = saved.TryGetValue(P + "replyTo", out var reply) ? (string.IsNullOrWhiteSpace(reply) ? null : reply.Trim()) : ReplyTo,
            DailyLimit = I("dailyLimit", DailyLimit, 1, OutreachService.MaxDailyLimit),
            WindowStartHour = I("windowStartHour", WindowStartHour, 0, 23),
            WindowEndHour = I("windowEndHour", WindowEndHour, 1, 24),
            TimeZoneId = TryZone(S("timeZone")) is not null ? S("timeZone")! : TimeZoneId,
            FooterText = S("footerText") ?? FooterText,
            CompanyLine = S("companyLine") ?? CompanyLine,
            MinFollowUpDays = I("minFollowUpDays", MinFollowUpDays, 1, 30),
            MaxFailuresPerStep = I("maxFailuresPerStep", MaxFailuresPerStep, 1, 10),
            EmailEnabled = B("emailEnabled", EmailEnabled),
            CampaignSendingEnabled = B("campaignSendingEnabled", CampaignSendingEnabled),
            ManualEmailEnabled = B("manualEmailEnabled", ManualEmailEnabled),
            ReplySendingEnabled = B("replySendingEnabled", ReplySendingEnabled),
            ReplySyncEnabled = B("replySyncEnabled", ReplySyncEnabled),
            AiEnabled = B("aiEnabled", AiEnabled),
            AiLeadQualification = B("aiLeadQualification", AiLeadQualification),
            AiMessageDrafting = B("aiMessageDrafting", AiMessageDrafting),
            AiCampaignGeneration = B("aiCampaignGeneration", AiCampaignGeneration),
            AiReplyAssistant = B("aiReplyAssistant", AiReplyAssistant),
            AiInsights = B("aiInsights", AiInsights),
        };
        // Never let a bad combination (end before start) stop sending entirely: fall back to the defaults' window.
        return merged.WindowEndHour > merged.WindowStartHour ? merged : merged with { WindowStartHour = WindowStartHour, WindowEndHour = WindowEndHour };
    }

    /// <summary>The first problem with these settings, or null when they are valid.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(FromName) || FromName.Length > 80) return "Enter a sender name (up to 80 characters).";
        if (!OutreachService.LooksLikeEmail(FromAddress)) return "Enter a valid sender e-mail address.";
        if (!string.IsNullOrWhiteSpace(ReplyTo) && !OutreachService.LooksLikeEmail(ReplyTo)) return "Enter a valid reply-to address, or leave it empty.";
        if (DailyLimit is < 1 or > OutreachService.MaxDailyLimit) return $"The daily limit must be between 1 and {OutreachService.MaxDailyLimit}.";
        if (WindowStartHour is < 0 or > 23 || WindowEndHour is < 1 or > 24 || WindowEndHour <= WindowStartHour) return "Sending hours must end after they start (0–24).";
        if (TryZone(TimeZoneId) is null) return $"\"{TimeZoneId}\" isn't a known time zone (e.g. Asia/Kolkata, Asia/Dubai).";
        if (string.IsNullOrWhiteSpace(FooterText) || FooterText.Length > 500) return "Enter the footer text (up to 500 characters). It explains why the person got the e-mail.";
        if (string.IsNullOrWhiteSpace(CompanyLine) || CompanyLine.Length > 200) return "Enter your company name and location (up to 200 characters).";
        if (MinFollowUpDays is < 1 or > 30) return "The shortest follow-up gap must be 1 to 30 days.";
        if (MaxFailuresPerStep is < 1 or > 10) return "Failed attempts before bouncing must be 1 to 10.";
        return null;
    }
}
