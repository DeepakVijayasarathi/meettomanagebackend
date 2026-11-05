using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Entities.Billing;
using iucs.meettomanage.domain.Entities.Settings;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.application.Services
{
    public class DiscountCodeDto
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = null!;
        public string? Description { get; set; }
        public decimal? PercentOff { get; set; }
        public decimal? AmountOff { get; set; }
        public DateTime? ValidFromUtc { get; set; }
        public DateTime? ValidUntilUtc { get; set; }
        public int? MaxRedemptions { get; set; }
        public int RedemptionCount { get; set; }
        public bool OncePerFamily { get; set; }
        public Guid? CourseId { get; set; }
        public string? CourseName { get; set; }
        public bool IsActive { get; set; }
        public decimal TotalDiscounted { get; set; }
    }

    public class SaveDiscountCodeRequest
    {
        [Required, StringLength(32, MinimumLength = 3), RegularExpression("^[A-Za-z0-9_-]+$", ErrorMessage = "Use letters, numbers, - or _ only.")]
        public string Code { get; set; } = null!;
        [StringLength(200)] public string? Description { get; set; }
        [Range(0.01, 100)] public decimal? PercentOff { get; set; }
        [Range(0.01, 10000000)] public decimal? AmountOff { get; set; }
        public DateTime? ValidFromUtc { get; set; }
        public DateTime? ValidUntilUtc { get; set; }
        [Range(1, 1000000)] public int? MaxRedemptions { get; set; }
        public bool OncePerFamily { get; set; } = true;
        public Guid? CourseId { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class ApplyDiscountRequest
    {
        [Required, StringLength(32)] public string Code { get; set; } = null!;
    }

    public interface IDiscountService
    {
        Task<IReadOnlyList<DiscountCodeDto>> ListAsync(CancellationToken cancellationToken = default);
        Task<DiscountCodeDto> CreateAsync(SaveDiscountCodeRequest request, CancellationToken cancellationToken = default);
        Task<DiscountCodeDto> UpdateAsync(Guid id, SaveDiscountCodeRequest request, CancellationToken cancellationToken = default);
        /// <summary>Applies a code to an unpaid invoice. A parent may only use it on their own invoices.</summary>
        Task<Invoice> ApplyAsync(Guid invoiceId, string code, Guid? parentUserId, CancellationToken cancellationToken = default);
        /// <summary>Staff: takes any discount back off an unpaid invoice.</summary>
        Task<Invoice> RemoveAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    }

    public class DiscountService(IUnitOfWork unitOfWork) : IDiscountService
    {
        public const string SiblingDiscountSetting = "billing.siblingDiscountPercent";

        public async Task<IReadOnlyList<DiscountCodeDto>> ListAsync(CancellationToken cancellationToken = default)
        {
            var totals = await unitOfWork.Repository<DiscountRedemption>().Query()
                .GroupBy(r => r.DiscountCodeId).Select(g => new { g.Key, Total = g.Sum(r => r.Amount) })
                .ToDictionaryAsync(x => x.Key, x => x.Total, cancellationToken);
            var codes = await unitOfWork.Repository<DiscountCode>().Query().Include(c => c.Course)
                .OrderByDescending(c => c.IsActive).ThenBy(c => c.Code).ToListAsync(cancellationToken);
            return codes.Select(c => ToDto(c, totals.GetValueOrDefault(c.Id))).ToList();
        }

        public async Task<DiscountCodeDto> CreateAsync(SaveDiscountCodeRequest request, CancellationToken cancellationToken = default)
        {
            Validate(request);
            var code = Normalize(request.Code);
            if (await unitOfWork.Repository<DiscountCode>().Query().AnyAsync(c => c.Code == code, cancellationToken))
                throw new ConflictException($"The code {code} already exists.");
            var entity = new DiscountCode { Code = code };
            Copy(request, entity);
            await unitOfWork.Repository<DiscountCode>().AddAsync(entity, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToDto(entity, 0);
        }

        public async Task<DiscountCodeDto> UpdateAsync(Guid id, SaveDiscountCodeRequest request, CancellationToken cancellationToken = default)
        {
            Validate(request);
            var entity = await unitOfWork.Repository<DiscountCode>().TrackedQuery().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                ?? throw new NotFoundException("Discount code not found.");
            var code = Normalize(request.Code);
            if (code != entity.Code)
            {
                if (entity.RedemptionCount > 0) throw new DomainValidationException("A code that has been used can't be renamed. Create a new code instead.");
                if (await unitOfWork.Repository<DiscountCode>().Query().AnyAsync(c => c.Code == code && c.Id != id, cancellationToken))
                    throw new ConflictException($"The code {code} already exists.");
                entity.Code = code;
            }
            Copy(request, entity);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            var total = await unitOfWork.Repository<DiscountRedemption>().Query().Where(r => r.DiscountCodeId == id).SumAsync(r => r.Amount, cancellationToken);
            return ToDto(entity, total);
        }

        public async Task<Invoice> ApplyAsync(Guid invoiceId, string code, Guid? parentUserId, CancellationToken cancellationToken = default)
        {
            var invoice = await unitOfWork.Repository<Invoice>().TrackedQuery()
                .Include(i => i.ParentProfile)
                .Include(i => i.Subscription!).ThenInclude(s => s.PackagePlan)
                .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
                ?? throw new NotFoundException("Invoice not found.");
            if (parentUserId is not null && invoice.ParentProfile.UserId != parentUserId) throw new NotFoundException("Invoice not found.");
            EnsureDiscountable(invoice);
            if (invoice.DiscountAmount > 0) throw new DomainValidationException("This invoice already has a discount.");

            var normalized = Normalize(code);
            var discount = await unitOfWork.Repository<DiscountCode>().TrackedQuery().FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
            var now = DateTime.UtcNow;
            if (discount is null || !discount.IsActive || (discount.ValidFromUtc is { } from && now < from) || (discount.ValidUntilUtc is { } until && now > until))
                throw new DomainValidationException("That code isn't valid. Check it and try again.");
            if (discount.MaxRedemptions is { } max && discount.RedemptionCount >= max)
                throw new DomainValidationException("That code has been fully used.");
            var courseId = invoice.CourseId ?? invoice.Subscription?.PackagePlan?.CourseId;
            if (discount.CourseId is { } onlyCourse && onlyCourse != courseId)
                throw new DomainValidationException("That code doesn't apply to this course.");
            if (discount.OncePerFamily && await unitOfWork.Repository<DiscountRedemption>().Query()
                    .AnyAsync(r => r.DiscountCodeId == discount.Id && r.ParentProfileId == invoice.ParentProfileId, cancellationToken))
                throw new DomainValidationException("Your family has already used this code.");

            var amount = Calculate(invoice.Amount, discount.PercentOff, discount.AmountOff);
            if (amount <= 0) throw new DomainValidationException("That code doesn't reduce this invoice.");
            ApplyToInvoice(invoice, amount, $"Code {discount.Code}");
            discount.RedemptionCount++;
            await unitOfWork.Repository<DiscountRedemption>().AddAsync(new DiscountRedemption
            {
                DiscountCodeId = discount.Id, InvoiceId = invoice.Id, ParentProfileId = invoice.ParentProfileId, Amount = amount,
            }, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return invoice;
        }

        public async Task<Invoice> RemoveAsync(Guid invoiceId, CancellationToken cancellationToken = default)
        {
            var invoice = await unitOfWork.Repository<Invoice>().TrackedQuery().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
                ?? throw new NotFoundException("Invoice not found.");
            if (invoice.DiscountAmount <= 0) return invoice;
            if (invoice.AmountPaid > 0) throw new DomainValidationException("A payment has already been made against this invoice.");
            var redemption = await unitOfWork.Repository<DiscountRedemption>().TrackedQuery()
                .Include(r => r.DiscountCode).FirstOrDefaultAsync(r => r.InvoiceId == invoiceId, cancellationToken);
            if (redemption is not null)
            {
                redemption.DiscountCode.RedemptionCount = Math.Max(0, redemption.DiscountCode.RedemptionCount - 1);
                unitOfWork.Repository<DiscountRedemption>().Remove(redemption);
            }
            invoice.Amount += invoice.DiscountAmount;
            invoice.DiscountAmount = 0;
            invoice.DiscountLabel = null;
            if (invoice.Status == InvoiceStatus.Paid && invoice.AmountPaid < invoice.Amount)
            {
                invoice.Status = InvoiceStatus.Pending;
                invoice.PaidAtUtc = null;
            }
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return invoice;
        }

        /// <summary>
        /// Sibling discount for a new recurring invoice: the academy's percentage, for every child after the
        /// family's first (by enrolment order) who has an active subscription. Returns 0 when it doesn't apply.
        /// </summary>
        public static async Task<decimal> SiblingDiscountAsync(IUnitOfWork unitOfWork, Guid parentProfileId, Guid? childId, decimal amount, CancellationToken cancellationToken)
        {
            if (childId is null || amount <= 0) return 0;
            var raw = await unitOfWork.Repository<AppSetting>().Query().Where(s => s.Key == SiblingDiscountSetting).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
            if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var percent) || percent <= 0 || percent > 100) return 0;
            var payingChildren = await unitOfWork.Repository<Subscription>().Query()
                .Where(s => s.Child.ParentProfileId == parentProfileId && s.Status == SubscriptionStatus.Active)
                .Select(s => new { s.ChildId, s.Child.CreatedAtUtc })
                .Distinct().OrderBy(x => x.CreatedAtUtc).Select(x => x.ChildId).ToListAsync(cancellationToken);
            if (payingChildren.Count < 2 || payingChildren[0] == childId || !payingChildren.Contains(childId.Value)) return 0;
            return Calculate(amount, percent, null);
        }

        /// <summary>Takes <paramref name="discount"/> off the amount due and records it on the invoice.</summary>
        public static void ApplyToInvoice(Invoice invoice, decimal discount, string label)
        {
            invoice.DiscountAmount = discount;
            invoice.DiscountLabel = label;
            invoice.Amount -= discount;
            if (invoice.Amount <= 0)
            {
                invoice.Amount = 0;
                invoice.Status = InvoiceStatus.Paid;
                invoice.PaidAtUtc ??= DateTime.UtcNow;
            }
        }

        public static decimal Calculate(decimal amount, decimal? percentOff, decimal? amountOff)
        {
            var off = percentOff is { } p ? amount * p / 100m : amountOff ?? 0;
            return Math.Round(Math.Min(amount, Math.Max(0, off)), 2, MidpointRounding.AwayFromZero);
        }

        private static void EnsureDiscountable(Invoice invoice)
        {
            if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled || invoice.AmountPaid > 0)
                throw new DomainValidationException("Discounts can only be applied before any payment is made.");
        }

        private static void Validate(SaveDiscountCodeRequest request)
        {
            if ((request.PercentOff is null) == (request.AmountOff is null))
                throw new DomainValidationException("Set either a percentage or a fixed amount off.");
            if (request.ValidFromUtc is { } from && request.ValidUntilUtc is { } until && until <= from)
                throw new DomainValidationException("The end date must be after the start date.");
        }

        private static void Copy(SaveDiscountCodeRequest request, DiscountCode entity)
        {
            entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            entity.PercentOff = request.PercentOff;
            entity.AmountOff = request.AmountOff;
            entity.ValidFromUtc = request.ValidFromUtc;
            entity.ValidUntilUtc = request.ValidUntilUtc;
            entity.MaxRedemptions = request.MaxRedemptions;
            entity.OncePerFamily = request.OncePerFamily;
            entity.CourseId = request.CourseId;
            entity.IsActive = request.IsActive;
        }

        private static string Normalize(string code) => (code ?? string.Empty).Trim().ToUpperInvariant();

        private static DiscountCodeDto ToDto(DiscountCode c, decimal total) => new()
        {
            Id = c.Id, Code = c.Code, Description = c.Description, PercentOff = c.PercentOff, AmountOff = c.AmountOff,
            ValidFromUtc = c.ValidFromUtc, ValidUntilUtc = c.ValidUntilUtc, MaxRedemptions = c.MaxRedemptions,
            RedemptionCount = c.RedemptionCount, OncePerFamily = c.OncePerFamily, CourseId = c.CourseId,
            CourseName = c.Course?.Name, IsActive = c.IsActive, TotalDiscounted = total,
        };
    }
}
