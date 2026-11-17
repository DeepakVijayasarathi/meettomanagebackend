using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.api.Marketing;
using iucs.meettomanage.application.Dto.Marketing;
using iucs.meettomanage.application.Mappings;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace iucs.meettomanage.api.Controllers;

/// <summary>The marketing console's CRM: team, tasks, lead import, deals, reports and customers. Roles are checked in <see cref="CrmService"/>.</summary>
[ApiController, Route("api/marketing/crm"), Authorize(Roles = nameof(UserRole.Marketing))]
public sealed class CrmController(CrmService crm) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<CrmMe>> Me(CancellationToken ct) => Ok(await crm.MeAsync(ct));

    // ---- team
    [HttpGet("team")]
    public async Task<ActionResult<List<CrmMember>>> Team(CancellationToken ct) => Ok(await crm.TeamAsync(ct));

    [HttpPost("team")]
    public async Task<ActionResult<CrmMember>> AddMember(AddCrmMemberRequest request, CancellationToken ct) => Ok(await crm.AddMemberAsync(request, ct));

    [HttpPut("team/{userId:guid}")]
    public async Task<IActionResult> UpdateMember(Guid userId, UpdateCrmMemberRequest request, CancellationToken ct)
    {
        await crm.UpdateMemberAsync(userId, request, ct);
        return NoContent();
    }

    // ---- tasks
    [HttpGet("today")]
    public async Task<ActionResult<CrmToday>> Today([FromQuery] int utcOffsetMinutes = 330, CancellationToken ct = default) =>
        Ok(await crm.TodayAsync(Math.Clamp(utcOffsetMinutes, -720, 840), ct));

    [HttpGet("tasks")]
    public async Task<ActionResult<List<CrmTaskDto>>> Tasks([FromQuery] string scope = "mine", [FromQuery] string status = "open",
        [FromQuery] Guid? leadId = null, [FromQuery] Guid? tenantId = null, CancellationToken ct = default) =>
        Ok(await crm.TasksAsync(scope, status, leadId, tenantId, ct));

    [HttpPost("tasks")]
    public async Task<ActionResult<CrmTaskDto>> CreateTask(SaveCrmTaskRequest request, CancellationToken ct) => Ok(await crm.SaveTaskAsync(null, request, ct));

    [HttpPut("tasks/{id:guid}")]
    public async Task<ActionResult<CrmTaskDto>> UpdateTask(Guid id, SaveCrmTaskRequest request, CancellationToken ct) => Ok(await crm.SaveTaskAsync(id, request, ct));

    [HttpPost("tasks/{id:guid}/complete")]
    public async Task<ActionResult<CrmTaskDto>> CompleteTask(Guid id, CompleteCrmTaskRequest request, CancellationToken ct) => Ok(await crm.CompleteTaskAsync(id, request, ct));

    [HttpDelete("tasks/{id:guid}")]
    public async Task<IActionResult> DeleteTask(Guid id, CancellationToken ct)
    {
        await crm.DeleteTaskAsync(id, ct);
        return NoContent();
    }

    // ---- leads
    [HttpPost("leads")]
    public async Task<ActionResult<PlatformDemoRequestDto>> CreateLead(CreateCrmLeadRequest request, CancellationToken ct) => Ok((await crm.CreateLeadAsync(request, ct)).ToDto());

    [HttpPost("leads/import")]
    public async Task<ActionResult<CrmImportResult>> Import(ImportCrmLeadsRequest request, CancellationToken ct) => Ok(await crm.ImportLeadsAsync(request, ct));

    [HttpPut("leads/{id:guid}/deal")]
    public async Task<ActionResult<PlatformDemoRequestDto>> Deal(Guid id, UpdateCrmDealRequest request, CancellationToken ct) => Ok((await crm.UpdateDealAsync(id, request, ct)).ToDto());

    [HttpPost("leads/{id:guid}/log")]
    public async Task<ActionResult<PlatformDemoRequestDto>> LogInteraction(Guid id, LogCrmInteractionRequest request, CancellationToken ct) => Ok((await crm.LogInteractionAsync(id, request, ct)).ToDto());

    // ---- reports
    [HttpGet("reports")]
    public async Task<ActionResult<CrmReport>> Reports([FromQuery] int days = 90, CancellationToken ct = default) => Ok(await crm.ReportAsync(days, ct));

    // ---- customers
    [HttpGet("customers")]
    public async Task<ActionResult<object>> Customers([FromQuery] string? search, [FromQuery] CustomerHealth? health, CancellationToken ct) =>
        Ok(new { available = crm.CustomersAvailable, items = await crm.CustomersAsync(search, health, ct) });

    [HttpGet("customers/{tenantId:guid}")]
    public async Task<ActionResult<CrmCustomerDetail>> Customer(Guid tenantId, CancellationToken ct) => Ok(await crm.CustomerAsync(tenantId, ct));

    public sealed record NoteInput([Required, MaxLength(2000)] string Text);

    [HttpPost("customers/{tenantId:guid}/notes")]
    public async Task<ActionResult<CrmNote>> AddNote(Guid tenantId, NoteInput input, CancellationToken ct) => Ok(await crm.AddCustomerNoteAsync(tenantId, input.Text, ct));
}
