using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Dto.Billing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Billing;
using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class DiscountTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly DiscountService _discounts;
    private readonly BillingService _billing;

    public DiscountTests()
    {
        var audit = new AuditLogService(_db.UnitOfWork, _db.CurrentUser);
        var templates = new EmailTemplateService(_db.UnitOfWork, audit, new MemoryCache(new MemoryCacheOptions()));
        var notifications = new NotificationService(_db.UnitOfWork, new FakeEmailSender(), templates, new FakePushSender(), NullLogger<NotificationService>.Instance);
        _billing = new BillingService(_db.UnitOfWork, audit, new FakePaymentGateway(), notifications, _db.CurrentUser, new FakeBulkFileReader(), new FakeInvoicePdfGenerator());
        _discounts = new DiscountService(_db.UnitOfWork);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Calculate_RoundsAndNeverExceedsTheInvoice()
    {
        Assert.Equal(150.00m, DiscountService.Calculate(1500, 10, null));
        Assert.Equal(333.33m, DiscountService.Calculate(999.99m, 33.3334m, null));
        Assert.Equal(500m, DiscountService.Calculate(500, null, 800));
        Assert.Equal(200m, DiscountService.Calculate(1000, null, 200));
    }

    [Fact]
    public async Task PromoCode_ReducesAnUnpaidInvoice_AndRespectsItsRules()
    {
        var (parentUser, parent, _) = await SeedFamilyAsync();
        await _discounts.CreateAsync(new SaveDiscountCodeRequest { Code = "summer10", PercentOff = 10, MaxRedemptions = 5 });
        await Assert.ThrowsAsync<ConflictException>(() => _discounts.CreateAsync(new SaveDiscountCodeRequest { Code = "SUMMER10", AmountOff = 100 }));
        await Assert.ThrowsAsync<DomainValidationException>(() => _discounts.CreateAsync(new SaveDiscountCodeRequest { Code = "BOTH", PercentOff = 5, AmountOff = 5 }));

        var first = await InvoiceAsync(parent, 2000);
        var applied = await _discounts.ApplyAsync(first, " Summer10 ", parentUser.Id);
        Assert.Equal(1800m, applied.Amount);
        Assert.Equal(200m, applied.DiscountAmount);
        Assert.Equal("Code SUMMER10", applied.DiscountLabel);

        // One discount per invoice, once per family, only on the family's own invoices.
        await Assert.ThrowsAsync<DomainValidationException>(() => _discounts.ApplyAsync(first, "SUMMER10", parentUser.Id));
        var second = await InvoiceAsync(parent, 2000);
        var once = await Assert.ThrowsAsync<DomainValidationException>(() => _discounts.ApplyAsync(second, "SUMMER10", parentUser.Id));
        Assert.Contains("already used", once.Message);
        var (otherUser, _, _) = await SeedFamilyAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => _discounts.ApplyAsync(second, "SUMMER10", otherUser.Id));
        await Assert.ThrowsAsync<DomainValidationException>(() => _discounts.ApplyAsync(second, "NOPE", null));

        var code = Assert.Single(await _discounts.ListAsync());
        Assert.Equal(1, code.RedemptionCount);
        Assert.Equal(200m, code.TotalDiscounted);

        // Staff can take it back off; the use is returned.
        var restored = await _discounts.RemoveAsync(first);
        Assert.Equal(2000m, restored.Amount);
        Assert.Equal(0m, restored.DiscountAmount);
        Assert.Equal(0, (await _discounts.ListAsync())[0].RedemptionCount);
    }

    [Fact]
    public async Task ExpiredFullyUsedOrPartPaid_AreRefused_AndAFullDiscountMarksPaid()
    {
        var (_, parent, _) = await SeedFamilyAsync();
        await _discounts.CreateAsync(new SaveDiscountCodeRequest { Code = "OLD", PercentOff = 10, ValidUntilUtc = DateTime.UtcNow.AddDays(-1) });
        await _discounts.CreateAsync(new SaveDiscountCodeRequest { Code = "ONCE", AmountOff = 100, MaxRedemptions = 1, OncePerFamily = false });
        await _discounts.CreateAsync(new SaveDiscountCodeRequest { Code = "FREE", PercentOff = 100, OncePerFamily = false });

        var invoice = await InvoiceAsync(parent, 1000);
        await Assert.ThrowsAsync<DomainValidationException>(() => _discounts.ApplyAsync(invoice, "OLD", null));
        await _discounts.ApplyAsync(invoice, "ONCE", null);
        var another = await InvoiceAsync(parent, 1000);
        var used = await Assert.ThrowsAsync<DomainValidationException>(() => _discounts.ApplyAsync(another, "ONCE", null));
        Assert.Contains("fully used", used.Message);

        var paid = await _discounts.ApplyAsync(another, "FREE", null);
        Assert.Equal(0m, paid.Amount);
        Assert.Equal(InvoiceStatus.Paid, paid.Status);

        var partPaid = await InvoiceAsync(parent, 1000);
        var tracked = await _db.Context.Invoices.SingleAsync(i => i.Id == partPaid);
        tracked.AmountPaid = 100;
        await _db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => _discounts.ApplyAsync(partPaid, "FREE", null));
    }

    [Fact]
    public async Task SiblingDiscount_AppliesToRecurringInvoicesForTheSecondChildOnly()
    {
        var (_, parent, firstChild) = await SeedFamilyAsync();
        var secondChild = new Child { ParentProfileId = parent.Id, FirstName = "Diya", LastName = "Kid" };
        _db.Context.Add(secondChild);
        _db.Context.AppSettings.Add(new AppSetting { Key = DiscountService.SiblingDiscountSetting, Value = "10", Category = default });
        var plan = new PackagePlan { Name = "Monthly", Price = 1000 };
        _db.Context.Add(plan);
        await _db.Context.SaveChangesAsync();
        _db.Context.AddRange(
            new Subscription { ParentProfileId = parent.Id, ChildId = firstChild.Id, PackagePlanId = plan.Id, StartDate = DateOnly.FromDateTime(DateTime.UtcNow) },
            new Subscription { ParentProfileId = parent.Id, ChildId = secondChild.Id, PackagePlanId = plan.Id, StartDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        await _db.Context.SaveChangesAsync();

        var forFirst = await _billing.CreateInvoiceAsync(Request(parent, firstChild.Id), isAutoGenerated: true, notifyParent: false);
        var forSecond = await _billing.CreateInvoiceAsync(Request(parent, secondChild.Id), isAutoGenerated: true, notifyParent: false);
        var manual = await _billing.CreateInvoiceAsync(Request(parent, secondChild.Id), notifyParent: false);

        Assert.Equal(1000m, forFirst.Amount);
        Assert.Equal(900m, forSecond.Amount);
        Assert.Equal(100m, forSecond.DiscountAmount);
        Assert.Equal("Sibling discount", forSecond.DiscountLabel);
        Assert.Equal(1000m, manual.Amount);
    }

    private static CreateInvoiceRequest Request(ParentProfile parent, Guid? childId) => new()
    {
        ParentProfileId = parent.Id, ChildId = childId, DepartmentId = WellKnownDepartments.Phonics, Amount = 1000,
        DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
    };

    private async Task<Guid> InvoiceAsync(ParentProfile parent, decimal amount)
    {
        var dto = await _billing.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            ParentProfileId = parent.Id, DepartmentId = WellKnownDepartments.Phonics, Amount = amount,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
        }, notifyParent: false);
        return dto.Id;
    }

    private async Task<(User User, ParentProfile Parent, Child Child)> SeedFamilyAsync()
    {
        var user = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
        var parent = new ParentProfile { UserId = user.Id };
        var child = new Child { ParentProfile = parent, FirstName = "Asha", LastName = "Kid" };
        _db.Context.AddRange(parent, child);
        if (!await _db.Context.PaymentAccounts.AnyAsync())
            _db.Context.Add(new PaymentAccount { Name = "P", DepartmentId = WellKnownDepartments.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
        await _db.Context.SaveChangesAsync();
        return (user, parent, child);
    }
}
