using System.Text;
using iucs.meettomanage.api.Services;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class BrandLogoTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10];
    private static readonly byte[] Webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");

    [Fact]
    public void RealImagesAreAcceptedByTheirBytes()
    {
        Assert.Equal("image/png", BrandLogo.Detect(Png, "logo.png"));
        Assert.Equal("image/jpeg", BrandLogo.Detect(Jpeg, "logo.JPG"));
        Assert.Equal("image/webp", BrandLogo.Detect(Webp, "logo.webp"));
    }

    [Fact]
    public void ABadExtensionOrDisguisedFileIsRefused()
    {
        Assert.Null(BrandLogo.Detect(Png, "logo.exe"));
        Assert.Null(BrandLogo.Detect(Encoding.UTF8.GetBytes("MZ not an image"), "logo.png"));
        Assert.Null(BrandLogo.Detect(Png, "logo.gif"));
    }

    [Fact]
    public void SvgIsNotAcceptedUnderAnyName()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><circle r='5'/></svg>");
        Assert.Null(BrandLogo.Detect(svg, "mark.svg"));
        Assert.Null(BrandLogo.Detect(svg, "mark.png"));
    }
}
