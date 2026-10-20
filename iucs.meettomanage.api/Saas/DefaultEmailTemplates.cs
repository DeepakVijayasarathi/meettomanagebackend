namespace iucs.meettomanage.api.Saas;

/// <summary>
/// Built-in billing emails, inserted into platform_email_template when missing and then editable by the
/// operator. Placeholders: {{tenant_name}} {{plan_name}} {{amount}} {{invoice_number}} {{due_date}}
/// {{trial_end_date}} {{days_left}} {{period_end_date}} {{grace_end_date}} {{payment_url}} {{portal_url}}
/// {{platform_name}} {{support_email}} {{usage_label}} {{usage_percent}} {{usage_limit}}.
/// </summary>
public static class DefaultEmailTemplates
{
    public sealed record Template(string Key, string Title, string Subject, string Body);

    private const string Portal = "<p>Manage your subscription: <a href=\"{{portal_url}}\">{{portal_url}}</a></p>";

    public static readonly IReadOnlyList<Template> All =
    [
        new("trial_started", "Welcome / trial started", "Welcome to {{platform_name}} - your trial has started",
            "<p>Hello {{tenant_name}},</p><p>Your free trial is active until <b>{{trial_end_date}}</b>. Set up your branding, add teachers and invite parents.</p>" + Portal),
        new("trial_ending_7", "Trial ending (7 days)", "Your {{platform_name}} trial ends in 7 days",
            "<p>Hello {{tenant_name}},</p><p>Your trial ends on <b>{{trial_end_date}}</b>. Choose a plan to keep your workspace running without interruption.</p>" + Portal),
        new("trial_ending_3", "Trial ending (3 days)", "Only 3 days left in your {{platform_name}} trial",
            "<p>Hello {{tenant_name}},</p><p>Your trial ends on <b>{{trial_end_date}}</b>. Choose a plan now so your classes are not interrupted.</p>" + Portal),
        new("trial_ending_1", "Trial ending (1 day)", "Your {{platform_name}} trial ends tomorrow",
            "<p>Hello {{tenant_name}},</p><p>Your trial ends tomorrow (<b>{{trial_end_date}}</b>). After that your workspace is paused until you choose a plan.</p>" + Portal),
        new("trial_expired", "Trial expired", "Your {{platform_name}} trial has ended",
            "<p>Hello {{tenant_name}},</p><p>Your trial has ended and the workspace is paused. Your data is safe. Choose a plan to restore access.</p>{{pay_block}}<p>Questions? {{support_email}}</p>"),
        new("renewal_upcoming", "Upcoming renewal / invoice", "Invoice {{invoice_number}} for your {{platform_name}} subscription",
            "<p>Hello {{tenant_name}},</p><p>Your <b>{{plan_name}}</b> subscription renews on <b>{{due_date}}</b>. Amount due: <b>{{amount}}</b> (incl. GST).</p>{{pay_block}}" + Portal),
        new("payment_success", "Payment received", "Payment received - thank you, {{tenant_name}}",
            "<p>Hello {{tenant_name}},</p><p>We received your payment of <b>{{amount}}</b> for invoice {{invoice_number}}. Your <b>{{plan_name}}</b> plan is active until <b>{{period_end_date}}</b>.</p>" + Portal),
        new("payment_failed", "Payment failed", "Action needed: payment for {{platform_name}} failed",
            "<p>Hello {{tenant_name}},</p><p>We could not process the payment for invoice {{invoice_number}} ({{amount}}). Please try again or use another payment method.</p>{{pay_block}}"),
        new("past_due_started", "Payment overdue", "Your {{platform_name}} payment is overdue",
            "<p>Hello {{tenant_name}},</p><p>Invoice {{invoice_number}} ({{amount}}) is overdue. Your workspace stays available until <b>{{grace_end_date}}</b>, after which it will be suspended.</p>{{pay_block}}"),
        new("past_due_day_1", "Payment overdue (day 1)", "Reminder: invoice {{invoice_number}} is overdue",
            "<p>Hello {{tenant_name}},</p><p>Invoice {{invoice_number}} ({{amount}}) is still unpaid. Please pay before <b>{{grace_end_date}}</b> to avoid suspension.</p>{{pay_block}}"),
        new("past_due_day_3", "Payment overdue (day 3)", "Second reminder: invoice {{invoice_number}} is overdue",
            "<p>Hello {{tenant_name}},</p><p>Invoice {{invoice_number}} ({{amount}}) is unpaid. Your workspace will be suspended on <b>{{grace_end_date}}</b> unless payment is received.</p>{{pay_block}}"),
        new("past_due_day_5", "Payment overdue (day 5)", "Final reminders: invoice {{invoice_number}} is overdue",
            "<p>Hello {{tenant_name}},</p><p>Invoice {{invoice_number}} ({{amount}}) remains unpaid. Suspension is scheduled for <b>{{grace_end_date}}</b>.</p>{{pay_block}}"),
        new("grace_ending_48h", "Grace period ending (48h)", "Your {{platform_name}} workspace will be suspended in 48 hours",
            "<p>Hello {{tenant_name}},</p><p>Unless invoice {{invoice_number}} ({{amount}}) is paid, your workspace will be suspended on <b>{{grace_end_date}}</b>.</p>{{pay_block}}"),
        new("grace_ending_24h", "Grace period ending (24h)", "Last day: your {{platform_name}} workspace will be suspended tomorrow",
            "<p>Hello {{tenant_name}},</p><p>Your workspace will be suspended on <b>{{grace_end_date}}</b> unless invoice {{invoice_number}} ({{amount}}) is paid.</p>{{pay_block}}"),
        new("suspended", "Account suspended", "Your {{platform_name}} workspace has been suspended",
            "<p>Hello {{tenant_name}},</p><p>Your workspace is suspended because of an unpaid invoice. Your data is retained. Pay to restore access immediately.</p>{{pay_block}}<p>Questions? {{support_email}}</p>"),
        new("reactivated", "Account reactivated", "Your {{platform_name}} workspace is active again",
            "<p>Hello {{tenant_name}},</p><p>Thank you. Your workspace is active again on the <b>{{plan_name}}</b> plan.</p>" + Portal),
        new("plan_changed", "Plan changed", "Your {{platform_name}} plan has changed",
            "<p>Hello {{tenant_name}},</p><p>Your plan is now <b>{{plan_name}}</b>. Current period ends <b>{{period_end_date}}</b>.</p>" + Portal),
        new("downgrade_scheduled", "Downgrade scheduled", "Your {{platform_name}} plan will change at renewal",
            "<p>Hello {{tenant_name}},</p><p>Your plan will change to <b>{{plan_name}}</b> when your current period ends on <b>{{period_end_date}}</b>.</p>" + Portal),
        new("cancelled", "Subscription cancelled", "Your {{platform_name}} subscription has been cancelled",
            "<p>Hello {{tenant_name}},</p><p>Your subscription ends on <b>{{period_end_date}}</b>. You can resume it before then from your billing page.</p>" + Portal),
        new("usage_limit_80", "Usage at 80%", "You have used {{usage_percent}}% of your {{usage_label}} allowance",
            "<p>Hello {{tenant_name}},</p><p>Your usage of <b>{{usage_label}}</b> is at {{usage_percent}}% of the {{usage_limit}} included in the {{plan_name}} plan. Consider upgrading to avoid interruptions.</p>" + Portal),
        new("usage_limit_100", "Usage limit reached", "You have reached your {{usage_label}} limit",
            "<p>Hello {{tenant_name}},</p><p>You have reached the {{usage_limit}} {{usage_label}} included in the {{plan_name}} plan. Upgrade to add more.</p>" + Portal),
    ];
}
