using System.Globalization;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace iucs.meettomanage.api.Saas;

/// <summary>Tax invoice the platform issues to a tenant for its subscription.</summary>
public static class PlatformInvoicePdf
{
    static PlatformInvoicePdf() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] Render(PlatformInvoice invoice, Tenant tenant, TenantBillingProfile? profile, SaasOptions options)
    {
        var culture = CultureInfo.GetCultureInfo("en-IN");
        string Money(decimal value) => $"{invoice.Currency} {value.ToString("N2", culture)}";
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(x => x.FontSize(10));
            page.Content().Column(col =>
            {
                col.Spacing(14);
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(options.PlatformName).FontSize(20).Bold();
                        c.Item().Text("Tax invoice").FontSize(12).FontColor(Colors.Grey.Darken1);
                    });
                    row.ConstantItem(200).Column(c =>
                    {
                        c.Item().AlignRight().Text($"Invoice {invoice.InvoiceNumber}").Bold();
                        c.Item().AlignRight().Text($"Issued {invoice.IssuedAtUtc:dd MMM yyyy}");
                        c.Item().AlignRight().Text($"Due {invoice.DueAtUtc:dd MMM yyyy}");
                        c.Item().AlignRight().Text(invoice.Status == PlatformInvoiceStatus.Paid ? $"PAID {invoice.PaidAtUtc:dd MMM yyyy}" : invoice.Status.ToString().ToUpperInvariant()).Bold();
                    });
                });
                col.Item().Column(c =>
                {
                    c.Item().Text("Billed to").FontColor(Colors.Grey.Darken1);
                    c.Item().Text(profile?.LegalName ?? tenant.Name).Bold();
                    if (!string.IsNullOrWhiteSpace(profile?.Address)) c.Item().Text(profile.Address);
                    if (!string.IsNullOrWhiteSpace(profile?.State)) c.Item().Text(profile.State);
                    if (!string.IsNullOrWhiteSpace(profile?.Gstin)) c.Item().Text($"GSTIN {profile.Gstin}");
                });
                col.Item().Text($"Service period {invoice.PeriodStartUtc:dd MMM yyyy} to {invoice.PeriodEndUtc:dd MMM yyyy}");
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(); c.ConstantColumn(40); c.ConstantColumn(90); c.ConstantColumn(90); });
                    table.Header(h =>
                    {
                        h.Cell().BorderBottom(1).PaddingVertical(4).Text("Description").Bold();
                        h.Cell().BorderBottom(1).PaddingVertical(4).AlignRight().Text("Qty").Bold();
                        h.Cell().BorderBottom(1).PaddingVertical(4).AlignRight().Text("Rate").Bold();
                        h.Cell().BorderBottom(1).PaddingVertical(4).AlignRight().Text("Amount").Bold();
                    });
                    foreach (var line in invoice.Lines)
                    {
                        table.Cell().PaddingVertical(4).Text(line.Description);
                        table.Cell().PaddingVertical(4).AlignRight().Text(line.Quantity.ToString());
                        table.Cell().PaddingVertical(4).AlignRight().Text(Money(line.UnitAmount));
                        table.Cell().PaddingVertical(4).AlignRight().Text(Money(line.Amount));
                    }
                });
                col.Item().AlignRight().Width(240).Column(c =>
                {
                    void Row(string label, string value, bool bold = false) => c.Item().Row(r =>
                    {
                        var left = r.RelativeItem().Text(label); var right = r.ConstantItem(110).AlignRight().Text(value);
                        if (bold) { left.Bold(); right.Bold(); }
                    });
                    Row("Subtotal", Money(invoice.Subtotal));
                    Row($"GST ({invoice.TaxRatePercent:0.##}%)", Money(invoice.TaxAmount));
                    Row("Total", Money(invoice.Total), true);
                });
                if (!string.IsNullOrWhiteSpace(invoice.PaymentUrl) && invoice.Status == PlatformInvoiceStatus.Open)
                    col.Item().Text($"Pay online: {invoice.PaymentUrl}");
                if (!string.IsNullOrWhiteSpace(options.SupportEmail))
                    col.Item().Text($"Questions: {options.SupportEmail}").FontColor(Colors.Grey.Darken1);
            });
        })).GeneratePdf();
    }
}
