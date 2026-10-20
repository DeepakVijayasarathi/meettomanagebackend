namespace iucs.meettomanage.domain.Entities.Saas;

/// <summary>Lifecycle of a tenant's platform subscription. Distinct from the academy-level SubscriptionStatus.</summary>
public enum SaasSubscriptionStatus { Trialing, Active, PastDue, Suspended, Expired, Cancelled }

public enum BillingInterval { Month, Year }

public enum EntitlementValueType { Bool, Number }

public enum PlatformInvoiceStatus { Open, Paid, Void }

public enum PlatformPaymentStatus { Pending, Succeeded, Failed, Refunded }

/// <summary>Skipped = the channel was not configured, so nothing could be delivered (visible to operators).</summary>
public enum ReminderLogStatus { Pending, Sent, Failed, Skipped }
