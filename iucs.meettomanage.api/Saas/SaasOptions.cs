namespace iucs.meettomanage.api.Saas;

/// <summary>Configuration section "SaasBilling".</summary>
public sealed class SaasOptions
{
    public const string Section = "SaasBilling";

    /// <summary>Brand shown in emails and invoices.</summary>
    public string PlatformName { get; set; } = "Meet to Manage";
    public string SupportEmail { get; set; } = "";

    /// <summary>Plan new operator-created tenants start on when the request names none (an attached existing database always gets "legacy").</summary>
    public string DefaultPlanForNewTenants { get; set; } = "trial";
    public int DefaultTrialDays { get; set; } = 14;

    /// <summary>Days between a missed renewal payment and automatic suspension.</summary>
    public int GraceDays { get; set; } = 7;
    /// <summary>The renewal invoice is issued this many days before the period ends.</summary>
    public int InvoiceLeadDays { get; set; } = 7;
    /// <summary>GST on SaaS services in India.</summary>
    public decimal TaxRatePercent { get; set; } = 18m;
    public string InvoicePrefix { get; set; } = "SAAS";

    /// <summary>Public self-service signup. Off by default; the operator turns it on deliberately.</summary>
    public bool SignupEnabled { get; set; }
    public string[] ReservedTenantCodes { get; set; } = ["admin", "api", "app", "www", "platform", "billing", "support", "demo", "test", "mail", "static", "t"];

    /// <summary>"Manual" (operator confirms payments) or "Razorpay" (payment link + webhook).</summary>
    public string Gateway { get; set; } = "Manual";
    public RazorpayOptions Razorpay { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();

    public sealed class RazorpayOptions
    {
        public string KeyId { get; set; } = "";
        public string KeySecret { get; set; } = "";
        public string WebhookSecret { get; set; } = "";
    }

    public sealed class SmtpOptions
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 587;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string From { get; set; } = "";
        public bool UseTls { get; set; } = true;
    }
}
