using iucs.meettomanage.application.Services;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace iucs.meettomanage.api.Services
{
    public record CertificatePdfData(CertificateDto Certificate, string AcademyName, string VerifyUrl);

    /// <summary>Landscape A4 completion certificate with a QR code that opens the public verification page.</summary>
    public static class CertificatePdfGenerator
    {
        private const string Ink = "#1F2937";
        private const string Accent = "#F97316";
        private const string Muted = "#6B7280";

        static CertificatePdfGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public static byte[] QrPng(string text)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            return new PngByteQRCode(data).GetGraphic(10);
        }

        public static byte[] Generate(CertificatePdfData data)
        {
            var c = data.Certificate;
            var qr = QrPng(data.VerifyUrl);
            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(28);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontColor(Ink).FontSize(12));

                    page.Content().Border(3).BorderColor(Accent).Padding(10).Border(1).BorderColor(Accent).Padding(36).Column(col =>
                    {
                        col.Spacing(10);
                        col.Item().AlignCenter().Text(data.AcademyName.ToUpperInvariant()).FontSize(16).Bold().LetterSpacing(0.15f).FontColor(Accent);
                        col.Item().AlignCenter().PaddingTop(10).Text("Certificate of Completion").FontSize(40).Bold();
                        col.Item().AlignCenter().PaddingTop(16).Text("This certifies that").FontSize(14).FontColor(Muted);
                        col.Item().AlignCenter().Text(c.StudentName).FontSize(34).Bold().Italic();
                        col.Item().AlignCenter().Text("has successfully completed").FontSize(14).FontColor(Muted);
                        col.Item().AlignCenter().Text(c.CourseName).FontSize(24).SemiBold();

                        col.Item().PaddingTop(30).Row(row =>
                        {
                            row.RelativeItem().AlignBottom().Column(left =>
                            {
                                left.Item().Text(c.IssuedAtUtc.ToString("d MMMM yyyy")).FontSize(13).SemiBold();
                                left.Item().LineHorizontal(1).LineColor(Muted);
                                left.Item().Text("Date").FontSize(10).FontColor(Muted);
                            });
                            row.ConstantItem(40);
                            row.RelativeItem().AlignBottom().Column(middle =>
                            {
                                middle.Item().Text(string.IsNullOrWhiteSpace(c.TeacherName) ? data.AcademyName : c.TeacherName).FontSize(13).SemiBold();
                                middle.Item().LineHorizontal(1).LineColor(Muted);
                                middle.Item().Text(string.IsNullOrWhiteSpace(c.TeacherName) ? "Academy" : "Teacher").FontSize(10).FontColor(Muted);
                            });
                            row.ConstantItem(40);
                            row.ConstantItem(120).AlignBottom().Column(right =>
                            {
                                right.Item().Width(96).AlignCenter().Image(qr);
                                right.Item().AlignCenter().Text($"Verify: {c.Code}").FontSize(8).FontColor(Muted);
                            });
                        });
                    });
                });
            }).GeneratePdf();
        }
    }
}
