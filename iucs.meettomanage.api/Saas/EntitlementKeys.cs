using iucs.meettomanage.domain.Entities.Saas;

namespace iucs.meettomanage.api.Saas;

/// <summary>The catalogue of things a plan can grant. Keys are stored in plan_entitlement.key.</summary>
public static class EntitlementKeys
{
    public const long Unlimited = -1;

    // Limits (Number). -1 = unlimited.
    public const string MaxStudents = "max_students";
    public const string MaxStaff = "max_staff";
    public const string MaxStorageMb = "max_storage_mb";
    public const string MaxSessionMinutesPerMonth = "max_session_minutes_per_month";
    public const string RecordingRetentionDays = "recording_retention_days";

    // Features (Bool, 0/1).
    public const string WhiteLabel = "feature_white_label";
    public const string ApiAccess = "feature_api_access";
    public const string Whatsapp = "feature_whatsapp";
    public const string CustomDomain = "feature_custom_domain";
    public const string PaymentGateway = "feature_payment_gateway";
    public const string Recordings = "feature_recordings";

    public sealed record Definition(string Key, string Label, EntitlementValueType Type, string Unit);

    public static readonly IReadOnlyList<Definition> Catalog =
    [
        new(MaxStudents, "Active students", EntitlementValueType.Number, "students"),
        new(MaxStaff, "Staff & teacher accounts", EntitlementValueType.Number, "accounts"),
        new(MaxStorageMb, "File storage", EntitlementValueType.Number, "MB"),
        new(MaxSessionMinutesPerMonth, "Live-session minutes per month", EntitlementValueType.Number, "minutes"),
        new(RecordingRetentionDays, "Recording retention", EntitlementValueType.Number, "days"),
        new(WhiteLabel, "White-label branding", EntitlementValueType.Bool, ""),
        new(ApiAccess, "API & webhook access", EntitlementValueType.Bool, ""),
        new(Whatsapp, "WhatsApp / SMS notifications", EntitlementValueType.Bool, ""),
        new(CustomDomain, "Custom domain", EntitlementValueType.Bool, ""),
        new(PaymentGateway, "Fee collection gateway", EntitlementValueType.Bool, ""),
        new(Recordings, "Session recordings", EntitlementValueType.Bool, ""),
    ];

    public static Definition? Find(string key) => Catalog.FirstOrDefault(x => x.Key == key);
}
