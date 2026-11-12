using iucs.meettomanage.application.Common;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class ReservedEmailTests
{
    [Theory]
    [InlineData("parent@example.com", true)]
    [InlineData("Parent@Example.ORG", true)]
    [InlineData("a@mail.example.net", true)]
    [InlineData("kid@school.test", true)]
    [InlineData("x@no-email.invalid", true)]
    [InlineData("dev@localhost", true)]
    [InlineData("a@foo.example", true)]
    [InlineData("parent@gmail.com", false)]
    [InlineData("admin@meettomanage.cloud", false)]
    [InlineData("someone@examples.com", false)]
    [InlineData("someone@myexample.com", false)]
    [InlineData("not-an-email", false)]
    [InlineData(null, false)]
    public void RecognisesDomainsReservedForTesting(string? email, bool reserved) =>
        Assert.Equal(reserved, ParentLogin.IsReservedTestAddress(email));
}
