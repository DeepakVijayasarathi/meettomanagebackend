using iucs.meettomanage.api.Saas;
using iucs.meettomanage.domain.Entities.Saas;
using iucs.meettomanage.domain.Entities.Tenancy;
using Xunit;

namespace iucs.meettomanage.tests;

public class GstRulesTests
{
    private const string KarnatakaGstin = "29ABCDE1234F1Z5";
    private const string MaharashtraGstin = "27ABCDE1234F1Z5";

    [Theory]
    [InlineData(KarnatakaGstin, "29")]
    [InlineData("27abcde1234f1z5", "27")]   // lower case is accepted
    [InlineData("99ABCDE1234F1Z5", null)]   // not a real state prefix
    [InlineData("29ABCDE1234", null)]       // wrong shape
    [InlineData("", null)]
    [InlineData(null, null)]
    public void StateComesFromAWellFormedGstin(string? gstin, string? expected) =>
        Assert.Equal(expected, GstRules.StateCodeFromGstin(gstin));

    [Theory]
    [InlineData("Karnataka", "29")]
    [InlineData("  tamil nadu ", "33")]
    [InlineData("Orissa", "21")]
    [InlineData("Delhi", "07")]
    [InlineData("36", "36")]
    [InlineData("Atlantis", null)]
    [InlineData(null, null)]
    public void StateNamesAndAliasesResolve(string? name, string? expected) =>
        Assert.Equal(expected, GstRules.StateCodeFromName(name));

    [Fact]
    public void SameStateSupplySplitsIntoCgstAndSgstThatAddUpExactly()
    {
        var split = GstRules.Split(539.83m, "29", KarnatakaGstin, null);
        Assert.True(split.IntraState);
        Assert.Equal(0m, split.Igst);
        Assert.Equal(539.83m, split.Cgst + split.Sgst); // an odd paisa is never lost
        Assert.Equal("29-Karnataka", split.PlaceOfSupply);
    }

    [Fact]
    public void OtherStateSupplyIsAllIgst()
    {
        var split = GstRules.Split(540m, "29", MaharashtraGstin, null);
        Assert.False(split.IntraState);
        Assert.Equal(540m, split.Igst);
        Assert.Equal(0m, split.Cgst + split.Sgst);
        Assert.Equal("27-Maharashtra", split.PlaceOfSupply);
    }

    [Fact]
    public void ProfileStateIsUsedWhenTheBuyerHasNoGstin()
    {
        Assert.Equal(540m, GstRules.Split(540m, "29", null, "Tamil Nadu").Igst);
        Assert.Equal(540m, GstRules.Split(540m, "29", null, "Karnataka").Cgst * 2);
    }

    [Fact]
    public void BuyerGstinWinsOverAConflictingProfileState() =>
        Assert.Equal(540m, GstRules.Split(540m, "29", MaharashtraGstin, "Karnataka").Igst);

    [Fact]
    public void UnknownBuyerStateIsTreatedAsTheSuppliersOwn() =>
        Assert.True(GstRules.Split(540m, "29", null, null).IntraState);

    [Fact]
    public void NothingIsSplitWithoutASupplierStateOrWithoutTax()
    {
        var noSeller = GstRules.Split(540m, null, MaharashtraGstin, null);
        Assert.Equal(0m, noSeller.Cgst + noSeller.Sgst + noSeller.Igst);
        Assert.Null(noSeller.PlaceOfSupply);
        var noTax = GstRules.Split(0m, "29", KarnatakaGstin, null);
        Assert.Equal(0m, noTax.Cgst + noTax.Sgst + noTax.Igst);
    }

    private static (PlatformInvoice Invoice, Tenant Tenant) Sample(decimal cgst, decimal sgst, decimal igst) => (new PlatformInvoice
    {
        InvoiceNumber = "SAAS-2026-000042", Subtotal = 3000m, TaxRatePercent = 18m, TaxAmount = cgst + sgst + igst, Total = 3000m + cgst + sgst + igst,
        CgstAmount = cgst, SgstAmount = sgst, IgstAmount = igst, SellerGstin = KarnatakaGstin, BuyerGstin = MaharashtraGstin,
        SacCode = "998431", PlaceOfSupply = "27-Maharashtra", DueAtUtc = DateTime.UtcNow,
        Lines = [new PlatformInvoiceLine { Description = "Growth plan (monthly) renewal", Quantity = 1, UnitAmount = 3000m, Amount = 3000m }],
    }, new Tenant { Name = "Bright Minds Academy" });

    [Theory]
    [InlineData(270, 270, 0)]
    [InlineData(0, 0, 540)]
    [InlineData(0, 0, 0)]
    public void InvoicePdfRendersForEveryTaxShape(int cgst, int sgst, int igst)
    {
        var (invoice, tenant) = Sample(cgst, sgst, igst);
        var pdf = PlatformInvoicePdf.Render(invoice, tenant, null, new SaasOptions { SellerLegalName = "Example Pvt Ltd", SellerAddress = "Bengaluru" });
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
