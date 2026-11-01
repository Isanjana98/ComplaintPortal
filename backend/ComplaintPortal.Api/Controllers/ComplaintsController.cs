using ComplaintPortal.Api.Dtos;
using ComplaintPortal.Api.Infrastructure;
using ComplaintPortal.Api.Models;
using ComplaintPortal.Api.Repositories;
using ComplaintPortal.Api.Security;
using ComplaintPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComplaintPortal.Api.Controllers;

[ApiController]
[Route("api/complaints")]
[Authorize]
public sealed class ComplaintsController : ControllerBase
{
    private readonly IComplaintRepository _complaints;
    private readonly IUserRepository _users;
    private readonly IAccessCache _access;
    private readonly IFileStorageService _files;
    private readonly IAuditRepository _audit;

    public ComplaintsController(IComplaintRepository complaints, IUserRepository users, IAccessCache access,
        IFileStorageService files, IAuditRepository audit)
    {
        _complaints = complaints;
        _users = users;
        _access = access;
        _files = files;
        _audit = audit;
    }

    /// <summary>Without complaints.view_all a user sees only their own complaints.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<ComplaintRow>>> List([FromQuery] ComplaintQuery query)
    {
        var userId = User.GetUserId();
        var viewAll = User.HasPermission(Permissions.ComplaintsViewAll);
        int? owner = viewAll ? null : userId;
        int? assignee = viewAll && query.AssignedToMe ? userId : null;

        var (items, total) = await _complaints.SearchAsync(query, owner, assignee);
        return new PagedResult<ComplaintRow>(items, total, query.Page, query.PageSize);
    }

    [HttpGet("stats")]
    public async Task<ActionResult<ComplaintStats>> Stats()
    {
        int? owner = User.HasPermission(Permissions.ComplaintsViewAll) ? null : User.GetUserId();
        return await _complaints.GetStatsAsync(owner);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var complaint = await _complaints.GetAsync(id);
        if (complaint is null || !CanAccess(complaint)) return NotFound(); // 404, not 403: don't reveal existence

        var comments = await _complaints.GetCommentsAsync(id,
            includeInternal: User.HasPermission(Permissions.ComplaintsInternalNotes));
        var attachments = await _complaints.GetAttachmentsAsync(id);
        var history = await _complaints.GetHistoryAsync(id);

        return Ok(new
        {
            complaint,
            comments,
            // Never expose the server-side storage name.
            attachments = attachments.Select(a => new { a.Id, a.OriginalFileName, a.ContentType, a.SizeBytes, a.CreatedAtUtc }),
            history,
            allowedTransitions = User.HasPermission(Permissions.ComplaintsUpdateStatus)
                ? ComplaintStatus.NextFor(complaint.Status) : Array.Empty<string>(),
            canComment = complaint.Status != ComplaintStatus.Closed,
        });
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ComplaintsCreate)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 20 * 1024 * 1024)]
    public async Task<IActionResult> Create([FromForm] CreateComplaintRequest request, CancellationToken ct)
    {
        if (!await _complaints.CategoryExistsAsync(request.CategoryId))
            throw new AppValidationException("Selected category does not exist.");

        request.Title = request.Title.Trim();
        request.Description = request.Description.Trim();

        var userId = User.GetUserId();
        var saved = await _files.SaveAsync(request.Files ?? new List<IFormFile>(), ct);

        int id;
        try
        {
            id = await _complaints.CreateAsync(userId, request, saved);
        }
        catch
        {
            foreach (var f in saved) _files.Delete(f.StoredFileName); // don't leave orphaned files
            throw;
        }

        await _audit.LogAsync(userId, "complaint.created", "Complaint", id.ToString(), HttpContext.GetClientIp(),
            $"Attachments: {saved.Count}");

        var created = await _complaints.GetAsync(id);
        return CreatedAtAction(nameof(Get), new { id }, created);
    }

    [HttpPost("{id:int}/comments")]
    public async Task<IActionResult> AddComment(int id, [FromBody] AddCommentRequest request)
    {
        var complaint = await _complaints.GetAsync(id);
        if (complaint is null || !CanAccess(complaint)) return NotFound();
        if (complaint.Status == ComplaintStatus.Closed)
            throw new AppValidationException("This complaint is closed and no longer accepts comments.");

        // Only users with complaints.internal_notes can write internal notes - silently downgrade otherwise.
        var isInternal = request.IsInternal && User.HasPermission(Permissions.ComplaintsInternalNotes);
        var userId = User.GetUserId();
        var commentId = await _complaints.AddCommentAsync(id, userId, request.Message.Trim(), isInternal);

        await _audit.LogAsync(userId, isInternal ? "complaint.note_added" : "complaint.comment_added",
            "Complaint", id.ToString(), HttpContext.GetClientIp());

        return Ok(new { id = commentId });
    }

    [HttpPatch("{id:int}/status")]
    [Authorize(Policy = Permissions.ComplaintsUpdateStatus)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        var complaint = await _complaints.GetAsync(id);
        if (complaint is null || !CanAccess(complaint)) return NotFound();

        if (!ComplaintStatus.CanTransition(complaint.Status, request.Status))
            throw new AppValidationException($"Cannot change status from {complaint.Status} to {request.Status}.");

        var userId = User.GetUserId();
        var ok = await _complaints.UpdateStatusAsync(id, complaint.Status, request.Status, userId, request.Note?.Trim());
        if (!ok)
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This complaint was updated by someone else. Refresh and try again.");

        await _audit.LogAsync(userId, "complaint.status_changed", "Complaint", id.ToString(), HttpContext.GetClientIp(),
            $"{complaint.Status} -> {request.Status}");
        return NoContent();
    }

    /// <summary>complaints.assign_any: assign to anyone eligible. complaints.assign_self: only to/from yourself.</summary>
    [HttpPatch("{id:int}/assign")]
    [Authorize(Policy = Policies.CanAssign)]
    public async Task<IActionResult> Assign(int id, [FromBody] AssignRequest request)
    {
        var userId = User.GetUserId();
        var canAssignAny = User.HasPermission(Permissions.ComplaintsAssignAny);

        if (!canAssignAny && request.AssignedToUserId is not null && request.AssignedToUserId != userId)
            return Forbid();

        var complaint = await _complaints.GetAsync(id);
        if (complaint is null || !CanAccess(complaint)) return NotFound();

        if (!canAssignAny && request.AssignedToUserId is null && complaint.AssignedToUserId != userId)
            return Forbid(); // can only un-assign yourself

        if (request.AssignedToUserId is int assigneeId)
        {
            // The assignee must be active and able to see all complaints, otherwise they couldn't open it.
            var assignee = await _users.GetByIdAsync(assigneeId);
            var eligible = assignee is { IsActive: true } &&
                           (await _access.GetRolePermissionsAsync(assignee.Role)).Contains(Permissions.ComplaintsViewAll);
            if (!eligible)
                throw new AppValidationException("The assignee must be an active user whose role can view all complaints.");
        }

        await _complaints.AssignAsync(id, request.AssignedToUserId);
        await _audit.LogAsync(userId, "complaint.assigned", "Complaint", id.ToString(), HttpContext.GetClientIp(),
            $"AssignedTo: {request.AssignedToUserId?.ToString() ?? "none"}");
        return NoContent();
    }

    [HttpGet("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> DownloadAttachment(int id, int attachmentId)
    {
        var complaint = await _complaints.GetAsync(id);
        if (complaint is null || !CanAccess(complaint)) return NotFound();

        var attachment = await _complaints.GetAttachmentAsync(id, attachmentId);
        if (attachment is null) return NotFound();

        Stream stream;
        try
        {
            stream = _files.OpenRead(attachment.StoredFileName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        await _audit.LogAsync(User.GetUserId(), "attachment.downloaded", "Attachment", attachmentId.ToString(),
            HttpContext.GetClientIp());

        // File(..., fileDownloadName) sends Content-Disposition: attachment, so the browser never renders it inline.
        return File(stream, attachment.ContentType, attachment.OriginalFileName);
    }

    private bool CanAccess(ComplaintRow c) =>
        User.HasPermission(Permissions.ComplaintsViewAll) || c.UserId == User.GetUserId();
}
