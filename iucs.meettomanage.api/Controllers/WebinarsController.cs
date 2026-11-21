using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Helper;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Marketing;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace iucs.meettomanage.api.Controllers;

[ApiController]
[Route("api/marketing/webinars")]
public sealed class WebinarsController(
    MeetToManageDbContext db, IBlogService publishing, IJitsiTokenService jitsi, IShortLinkService shortLinks) : ControllerBase
{
    private const string Prefix = BlogService.WebinarSlugPrefix;
    private const string Marketing = nameof(UserRole.Marketing);
    private const int MaxBannerBytes = 500_000;
    private const int MaxBannerDataLength = ((MaxBannerBytes + 2) / 3) * 4 + 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record WebinarInput(
        [Required, MaxLength(150)] string Title,
        [Required, MaxLength(240)] string Excerpt,
        [Required, MaxLength(3000)] string Description,
        [MaxLength(2000)] string? Benefits,
        [MaxLength(2000)] string? Offers,
        DateTimeOffset Start, DateTimeOffset End, bool IsPublished,
        [MaxLength(MaxBannerDataLength)] string? BannerImage = null);

    public sealed record WebinarDetails(string Description, string Benefits, string Offers, DateTimeOffset Start, DateTimeOffset End, string? BannerImage = null);
    public sealed record Webinar(
        Guid Id, string Slug, string Title, string Excerpt, bool IsPublished, WebinarDetails Details);

    private IQueryable<BlogPost> Events => db.BlogPosts.AsNoTracking().Where(post => post.Slug.StartsWith(Prefix));
    private IQueryable<BlogPost> Published(DateTime now) =>
        Events.Where(post => post.IsPublished && post.PublishedAtUtc <= now);

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await Published(now.UtcDateTime).ToListAsync(cancellationToken);
        var upcoming = records.Select(Map).OfType<Webinar>()
            .Where(webinar => webinar.Details.End > now)
            .OrderBy(webinar => webinar.Details.Start)
            .Select(webinar => new { webinar.Id, webinar.Slug, webinar.Title, webinar.Excerpt, webinar.Details.Start, webinar.Details.End });
        return Ok(upcoming);
    }

    [AllowAnonymous]
    [HttpGet("{slug}")]
    public async Task<IActionResult> Get(string slug, CancellationToken cancellationToken)
    {
        var post = await Published(DateTime.UtcNow).FirstOrDefaultAsync(row => row.Slug == slug, cancellationToken);
        return Map(post) is { } webinar ? Ok(webinar) : NotFound();
    }

    [AllowAnonymous]
    [EnableRateLimiting("store-inquiry")]
    [HttpPost("{slug}/register")]
    public async Task<IActionResult> Register(string slug, CreatePlatformDemoRequestRequest input,
        [FromServices] IPlatformDemoRequestService leads, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var webinar = Map(await Published(now.UtcDateTime).FirstOrDefaultAsync(row => row.Slug == slug, cancellationToken));
        if (webinar is null) return NotFound();
        if (webinar.Details.End <= now) return Conflict("Registration for this webinar has closed.");
        await leads.CreateAsync(new CreatePlatformDemoRequestRequest
        {
            FullName = input.FullName,
            WorkEmail = input.WorkEmail,
            Phone = input.Phone,
            AcademyName = input.AcademyName,
            Source = "webinar",
            Campaign = webinar.Slug,
            LandingPath = $"/webinars/{webinar.Slug}",
            Message = $"Webinar registration for {webinar.Title} (starts {webinar.Details.Start:O})"
        }, cancellationToken);
        return NoContent();
    }

    [Authorize(Roles = Marketing)]
    [HttpGet("manage")]
    public async Task<IActionResult> Manage(CancellationToken cancellationToken)
    {
        var records = await Events.OrderByDescending(post => post.CreatedAtUtc).ToListAsync(cancellationToken);
        return Ok(records.Select(Map).OfType<Webinar>());
    }

    [Authorize(Roles = Marketing)]
    [HttpGet("manage/{id:guid}/registrations")]
    public async Task<IActionResult> Registrations(Guid id, [FromServices] CrmAccess crm,
        CancellationToken cancellationToken, [FromQuery, Range(1, 1_000_000)] int page = 1)
    {
        var slug = await Events.Where(post => post.Id == id).Select(post => post.Slug).FirstOrDefaultAsync(cancellationToken);
        if (slug is null) return NotFound();

        const int pageSize = 25;
        var registrations = db.PlatformDemoRequests.AsNoTracking()
            .Where(lead => lead.Source == "webinar" && lead.Campaign == slug && lead.ArchivedAtUtc == null);
        if (await crm.VisibleToAsync(cancellationToken) is { } userId)
            registrations = registrations.Where(lead => lead.AssignedToUserId == null || lead.AssignedToUserId == userId);

        var totalCount = await registrations.CountAsync(cancellationToken);
        var items = await registrations.OrderByDescending(lead => lead.CreatedAtUtc).ThenByDescending(lead => lead.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(lead => new { lead.Id, lead.FullName, lead.WorkEmail, lead.Phone, lead.AcademyName, lead.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { items, totalCount, page, pageSize });
    }

    [Authorize(Roles = Marketing)]
    [HttpPost("manage")]
    public async Task<IActionResult> Create(WebinarInput input, CancellationToken cancellationToken)
    {
        if (!ValidSchedule(input)) return BadRequest("Enter a valid webinar start and end time.");
        if (!ValidBanner(input.BannerImage)) return BadRequest("Upload a PNG, JPG or WebP image up to 500 KB.");
        var created = await publishing.CreateAsync(new CreateBlogPostRequest
        {
            Slug = $"{Prefix}{Guid.NewGuid():N}",
            Title = input.Title,
            Excerpt = input.Excerpt,
            Content = SerializeDetails(input),
            IsPublished = input.IsPublished
        }, cancellationToken);
        return Ok(new { created.Id, created.Slug });
    }

    [Authorize(Roles = Marketing)]
    [HttpPut("manage/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, WebinarInput input, CancellationToken cancellationToken)
    {
        if (!ValidSchedule(input)) return BadRequest("Enter a valid webinar start and end time.");
        if (!ValidBanner(input.BannerImage)) return BadRequest("Upload a PNG, JPG or WebP image up to 500 KB.");
        var post = await Events.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (post is null) return NotFound();
        await publishing.UpdateAsync(id, new UpdateBlogPostRequest
        {
            Slug = post.Slug,
            Title = input.Title,
            Excerpt = input.Excerpt,
            Content = SerializeDetails(input with { BannerImage = input.BannerImage ?? Map(post)?.Details.BannerImage }),
            IsPublished = input.IsPublished
        }, cancellationToken);
        return NoContent();
    }

    [Authorize(Roles = Marketing)]
    [HttpPost("manage/{id:guid}/host")]
    public async Task<IActionResult> Host(Guid id, CancellationToken cancellationToken)
    {
        var webinar = Map(await Events.FirstOrDefaultAsync(post => post.Id == id, cancellationToken));
        if (webinar is null) return NotFound();
        if (webinar.Details.End <= DateTimeOffset.UtcNow) return Conflict("This webinar has ended.");
        var meetingUrl = await JoinUrl(id, moderator: true, expiresAtUtc: DateTime.UtcNow.AddHours(6), cancellationToken);
        return meetingUrl is null ? Problem("Jitsi signing is unavailable.", statusCode: 503) : Ok(new { url = meetingUrl });
    }

    [Authorize(Roles = Marketing)]
    [HttpPost("manage/{id:guid}/attendee-link")]
    public async Task<IActionResult> AttendeeLink(Guid id, CancellationToken cancellationToken)
    {
        var webinar = Map(await Published(DateTime.UtcNow).FirstOrDefaultAsync(post => post.Id == id, cancellationToken));
        if (webinar is null) return NotFound();
        var expiresAtUtc = webinar.Details.End.UtcDateTime;
        if (expiresAtUtc <= DateTime.UtcNow) return Conflict("This webinar has ended.");
        var meetingUrl = await JoinUrl(id, moderator: false, expiresAtUtc, cancellationToken);
        if (meetingUrl is null) return Problem("Jitsi signing is unavailable.", statusCode: 503);
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var slug = await shortLinks.CreateAsync(meetingUrl, expiresAtUtc, userId, cancellationToken);
        var apiBase = TenantExecution.Url($"{Request.Scheme}://{Request.Host}");
        return Ok(new { url = $"{apiBase}/m/{slug}", expiresAtUtc });
    }

    private async Task<string?> JoinUrl(Guid id, bool moderator, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        var config = await db.Integrations.Where(row => row.Key == "jitsi").Select(row => row.ConfigJson)
            .FirstOrDefaultAsync(cancellationToken);
        var room = $"m2m-webinar-{id:N}";
        var name = moderator ? User.Identity?.Name ?? "Webinar host" : "Guest";
        var token = jitsi.CreateToken(JitsiLinkBuilder.ResolveDomain(config), config, room, name, null, moderator, expiresAtUtc);
        return string.IsNullOrWhiteSpace(token) ? null : JitsiLinkBuilder.BuildJoinUrl(room, config, token);
    }

    private static bool ValidSchedule(WebinarInput input) =>
        input.Start != default && input.End > input.Start && (!input.IsPublished || input.End > DateTimeOffset.UtcNow);

    private static string SerializeDetails(WebinarInput input) =>
        JsonSerializer.Serialize(new WebinarDetails(
            input.Description.Trim(), input.Benefits?.Trim() ?? "", input.Offers?.Trim() ?? "", input.Start, input.End,
            string.IsNullOrEmpty(input.BannerImage) ? null : input.BannerImage), Json);

    private static bool ValidBanner(string? image)
    {
        if (string.IsNullOrEmpty(image)) return true;
        if (image.Length > MaxBannerDataLength) return false;
        var separator = image.IndexOf(',');
        if (separator < 0) return false;
        var extension = image[..separator] switch
        {
            "data:image/png;base64" => ".png",
            "data:image/jpeg;base64" => ".jpg",
            "data:image/webp;base64" => ".webp",
            _ => null,
        };
        if (extension is null) return false;
        try
        {
            var bytes = Convert.FromBase64String(image[(separator + 1)..]);
            var type = BrandLogo.Detect(bytes, $"banner{extension}");
            return bytes.Length <= MaxBannerBytes && type is not null && image.StartsWith($"data:{type};", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Webinar? Map(BlogPost? post)
    {
        if (post is null) return null;
        try
        {
            var details = JsonSerializer.Deserialize<WebinarDetails>(post.Content, Json);
            return details is null ? null : new Webinar(post.Id, post.Slug, post.Title, post.Excerpt, post.IsPublished, details);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
