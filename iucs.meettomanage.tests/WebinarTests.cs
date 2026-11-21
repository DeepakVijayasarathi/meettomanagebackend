using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using iucs.meettomanage.api.Controllers;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Entities.Integrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class WebinarTests
{
    [Fact]
    public async Task PublishedWebinarHasItsOwnJitsiRoomAndStaysOutOfTheBlog()
    {
        using var db = new TestDatabase();
        var audit = new AuditLogService(db.UnitOfWork, db.CurrentUser);
        var blog = new BlogService(db.UnitOfWork, audit);
        var leads = new PlatformDemoRequestService(db.UnitOfWork, audit);
        var links = new ShortLinkService(db.UnitOfWork);
        var controller = new WebinarsController(db.Context, blog, new JitsiTokenService(), links)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"))
                }
            }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("api.example.test");
        var integration = await db.Context.Integrations.SingleOrDefaultAsync(row => row.Key == "jitsi");
        if (integration is null)
        {
            integration = new Integration { Key = "jitsi", Name = "Jitsi" };
            db.Context.Integrations.Add(integration);
        }
        integration.ConfigJson = """
            {"domain":"meet.example.test","appId":"test","appSecret":"webinar-test-secret-of-at-least-32-bytes"}
            """;
        await db.Context.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var input = new WebinarsController.WebinarInput(
            "Marketing webinar", "An introduction", "A live walkthrough", "Live Q&A", "Trial offer",
            now.AddHours(1), now.AddHours(2), false);
        var created = AsJson(await controller.Create(input, default));
        var id = created.GetProperty("Id").GetGuid();
        var slug = created.GetProperty("Slug").GetString()!;
        Assert.IsType<NotFoundResult>(await controller.Get(slug, default));
        Assert.IsType<NotFoundResult>(await controller.AttendeeLink(id, default));

        Assert.IsType<NoContentResult>(await controller.Update(id, input with { IsPublished = true }, default));
        Assert.Equal(1, AsJson(await controller.List(default)).GetArrayLength());
        Assert.Equal("Trial offer", AsJson(await controller.Get(slug, default)).GetProperty("Details").GetProperty("Offers").GetString());
        Assert.DoesNotContain(await blog.ListPublishedAsync(), post => post.Id == id);
        Assert.DoesNotContain(await blog.ListAllAsync(), post => post.Id == id);
        var contact = new CreatePlatformDemoRequestRequest
        {
            FullName = "Webinar attendee", WorkEmail = "attendee@example.test", Phone = "+919123456789",
            AcademyName = "Example Academy"
        };
        var other = AsJson(await controller.Create(input with { IsPublished = true }, default)).GetProperty("Slug").GetString()!;
        Assert.IsType<NoContentResult>(await controller.Register(slug, contact, leads, default));
        Assert.IsType<NoContentResult>(await controller.Register(slug, contact, leads, default));
        Assert.IsType<NoContentResult>(await controller.Register(other, contact, leads, default));
        var campaigns = await db.Context.Set<iucs.meettomanage.domain.Entities.Marketing.PlatformDemoRequest>()
            .Where(lead => lead.WorkEmail == contact.WorkEmail)
            .Select(lead => lead.Campaign)
            .ToListAsync();
        Assert.Equal(2, campaigns.Count);
        Assert.Contains(slug, campaigns);
        Assert.Contains(other, campaigns);

        var hostUrl = AsJson(await controller.Host(id, default)).GetProperty("url").GetString()!;
        var attendee = AsJson(await controller.AttendeeLink(id, default));
        var shortUrl = new Uri(attendee.GetProperty("url").GetString()!);
        var guestUrl = await links.ResolveAsync(shortUrl.Segments.Last());
        Assert.NotNull(guestUrl);
        Assert.Equal(input.End.UtcDateTime, attendee.GetProperty("expiresAtUtc").GetDateTime());
        AssertRoomToken(hostUrl, id, true);
        AssertRoomToken(guestUrl, id, false);

        Assert.IsType<NoContentResult>(await controller.Update(id, input, default));
        Assert.IsType<NotFoundResult>(await controller.Get(slug, default));
        Assert.IsType<NotFoundResult>(await controller.Register(slug, contact, leads, default));
        Assert.Equal(1, AsJson(await controller.List(default)).GetArrayLength());
    }

    private static JsonElement AsJson(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private static void AssertRoomToken(string url, Guid id, bool moderator)
    {
        var uri = new Uri(url);
        Assert.Equal("meet.example.test", uri.Host);
        Assert.Equal($"/m2m-webinar-{id:N}", uri.AbsolutePath);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(QueryHelpers.ParseQuery(uri.Query)["jwt"].ToString());
        Assert.Equal(uri.AbsolutePath.TrimStart('/'), jwt.Payload["room"]?.ToString());
        var context = JsonSerializer.SerializeToElement(jwt.Payload["context"]);
        Assert.Equal(moderator, context.GetProperty("user").GetProperty("moderator").GetBoolean());
    }
}
