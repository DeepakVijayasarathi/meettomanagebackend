using iucs.meettomanage.application.Common;
using Xunit;

namespace iucs.meettomanage.tests
{
    public sealed class InvoicePayTokenTests
    {
        private const string SigningKey = "test-invoice-signing-key-that-is-long-enough";

        [Fact]
        public void RoundTrip_AcceptsOnlyTheOriginalInvoiceAndSignature()
        {
            var invoiceId = Guid.NewGuid();
            var token = InvoicePayToken.Create(invoiceId, SigningKey);

            Assert.Equal(invoiceId, InvoicePayToken.Read(token, SigningKey));
            Assert.Null(InvoicePayToken.Read(token, SigningKey + "-different"));
            Assert.Null(InvoicePayToken.Read(token[..^1] + (token[^1] == 'a' ? 'b' : 'a'), SigningKey));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("short-public-secret")]
        public void Create_RejectsMissingOrWeakSigningKeys(string? signingKey)
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                InvoicePayToken.Create(Guid.NewGuid(), signingKey));

            Assert.Contains("at least 32 bytes", error.Message);
        }

        [Fact]
        public void Read_RejectsMissingSigningKeyEvenForAWellFormedToken()
        {
            var token = InvoicePayToken.Create(Guid.NewGuid(), SigningKey);

            Assert.Throws<InvalidOperationException>(() => InvoicePayToken.Read(token, null));
        }
    }
}
