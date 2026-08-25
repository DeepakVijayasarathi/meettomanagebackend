using System.Reflection;
using iucs.readernest.application.Common.Interfaces;
using iucs.readernest.application.Dto.Billing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace iucs.readernest.api.Services
{
    /// <summary>
    /// Renders one invoice as a "Bill of Supply" PDF matching the org's existing manually-made
    /// invoice template exactly (cream background, red title, logo, bank/GST payment info,
    /// itemized table, refund terms, composition-scheme declaration, founder signature).
    /// Company/bank/GST/signatory details are fixed constants here, not pulled from settings —
    /// they're the org's own unchanging payment-collection details, identical on every invoice.
    /// </summary>
    public class InvoicePdfGenerator : IInvoicePdfGenerator
    {
        private const string AccountNumber = "777705999305";
        private const string IfscCode = "ICIC0008065";
        private const string BranchName = "sector 17 Faridabad";
        private const string GstNumber = "06AWCPN6985H1Z3";
        private const string AccountName = "THE READER NEST";
        private const string ContactEmail = "INFO@THEREADERNEST.COM";
        private const string SignatoryName = "Akanksha Nagar";
        private const string SignatoryTitle = "Founder & MD";

        private const string Cream = "#EDE7DC";
        private const string Red = "#D62839";
        private const string Ink = "#3B2A1E";
        private const string LineColor = "#B8A48C";

        private static readonly byte[] LogoBytes = LoadLogo();

        static InvoicePdfGenerator()
        {
            // Community license: free for organisations under the revenue threshold QuestPDF's
            // license sets — see https://www.questpdf.com/license/. This deployment qualifies.
            QuestPDF.Settings.License = LicenseType.Community;
        }

        private static byte[] LoadLogo()
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("iucs.readernest.api.Assets.invoice-logo.png")
                ?? throw new InvalidOperationException("Embedded invoice logo resource not found.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public byte[] Generate(InvoicePdfData data)
        {
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(36);
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor(Ink));
                    page.PageColor(Cream);

                    page.Content().Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text("BILL OF SUPPLY").FontSize(28).Bold().FontColor(Red);
                            row.ConstantItem(110).Column(c =>
                            {
                                c.Item().AlignRight().Height(80).Image(LogoBytes).FitArea();
                                c.Item().AlignRight().PaddingTop(4).Text($"DATE– {data.IssuedAtUtc:d MMMM,yyyy}".ToUpperInvariant()).FontSize(9);
                            });
                        });

                        col.Item().PaddingTop(20).Row(row =>
                        {
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text("Bill To:").Bold();
                                c.Item().PaddingTop(4).Text(data.ParentName);
                                if (!string.IsNullOrWhiteSpace(data.ParentPhone))
                                {
                                    c.Item().Text(data.ParentPhone);
                                }
                            });
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().AlignCenter().Text("Payment Info").Bold();
                                c.Item().PaddingTop(6).Text($"Account no.– {AccountNumber}");
                                c.Item().Text($"IFSC code– {IfscCode}");
                                c.Item().Text($"Branch Name – {BranchName}");
                                c.Item().Text($"GST NO– {GstNumber}");
                                c.Item().Text($"ACCOUNT NAME– {AccountName}");
                            });
                        });

                        col.Item().PaddingTop(24).Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn(3);
                                columns.RelativeColumn(1);
                                columns.RelativeColumn(1);
                                columns.RelativeColumn(1);
                            });

                            table.Header(header =>
                            {
                                header.Cell().Border(1).BorderColor(LineColor).Padding(6).Text("DESCRIPTION").Bold().FontSize(9);
                                header.Cell().Border(1).BorderColor(LineColor).Padding(6).AlignCenter().Text("SESSIONS").Bold().FontSize(9);
                                header.Cell().Border(1).BorderColor(LineColor).Padding(6).AlignCenter().Text("FEE").Bold().FontSize(9);
                                header.Cell().Border(1).BorderColor(LineColor).Padding(6).AlignCenter().Text("SUBTOTAL").Bold().FontSize(9);
                            });

                            table.Cell().Border(1).BorderColor(LineColor).Padding(6).Text(data.Description);
                            table.Cell().Border(1).BorderColor(LineColor).Padding(6).Text("");
                            table.Cell().Border(1).BorderColor(LineColor).Padding(6).Text("");
                            table.Cell().Border(1).BorderColor(LineColor).Padding(6).AlignCenter().Text($"{data.Amount:0.##}");

                            // Blank rows so the table reads the same as the org's own template
                            // (room for a staff member to hand-annotate a printed copy).
                            for (var i = 0; i < 5; i++)
                            {
                                table.Cell().Border(1).BorderColor(LineColor).Padding(6).Text("");
                                table.Cell().Border(1).BorderColor(LineColor).Padding(6).Text("");
                                table.Cell().Border(1).BorderColor(LineColor).Padding(6).Text("");
                                table.Cell().Border(1).BorderColor(LineColor).Padding(6).Text("");
                            }

                            table.Cell().ColumnSpan(3).Border(1).BorderColor(LineColor).Padding(6).AlignRight().Text("GRAND TOTAL").Bold();
                            table.Cell().Border(1).BorderColor(LineColor).Padding(6).AlignCenter().Text($"{data.Amount:0.##}").Bold();
                        });

                        col.Item().PaddingTop(6).AlignRight().Text($"FOR ANY QUESTIONS, PLEASE CONTACT {ContactEmail}").FontSize(8);

                        col.Item().PaddingTop(20).Row(row =>
                        {
                            row.RelativeItem(2).Column(c =>
                            {
                                c.Item().Text("TERM & CONDITION").Bold().FontSize(9);
                                c.Item().PaddingTop(4).Text(
                                    "Except for eligible claims covered under the MoneyBack Guarantee stated on our website, we do not " +
                                    "provide refunds under any circumstances. All fees paid towards enrolment, course registration, classes, " +
                                    "learning materials, administrative charges or any other services are strictly non-refundable and non-" +
                                    "transferable.\nNo exceptions will be considered beyond the conditions stated above."
                                ).FontSize(7);
                            });
                            row.RelativeItem(1).Column(c =>
                            {
                                c.Item().AlignCenter().PaddingBottom(2).Text(SignatoryName).Italic().FontSize(20);
                                c.Item().AlignCenter().Text(SignatoryName).Bold().FontSize(10);
                                c.Item().AlignCenter().Text(SignatoryTitle).FontSize(9);
                            });
                        });

                        col.Item().PaddingTop(16).Text("DECLARATION– COMPOSITION TAXABLE PERSON NOT ELIGIBLE TO COLLECT TAX ON SUPPLY.").FontSize(7).Bold();
                    });
                });
            });

            return document.GeneratePdf();
        }
    }
}
