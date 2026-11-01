using System.Text;
using ComplaintPortal.Api.Data;
using ComplaintPortal.Api.Dtos;
using ComplaintPortal.Api.Models;
using Dapper;

namespace ComplaintPortal.Api.Repositories;

public interface IComplaintRepository
{
    Task<int> CreateAsync(int userId, CreateComplaintRequest request, IReadOnlyList<NewAttachment> attachments);
    Task<(IReadOnlyList<ComplaintRow> Items, int Total)> SearchAsync(ComplaintQuery query, int? ownerUserId, int? assigneeUserId);
    Task<ComplaintRow?> GetAsync(int id);
    Task<IReadOnlyList<CommentRow>> GetCommentsAsync(int complaintId, bool includeInternal);
    Task<IReadOnlyList<AttachmentRow>> GetAttachmentsAsync(int complaintId);
    Task<AttachmentRow?> GetAttachmentAsync(int complaintId, int attachmentId);
    Task<IReadOnlyList<StatusHistoryRow>> GetHistoryAsync(int complaintId);
    Task<int> AddCommentAsync(int complaintId, int userId, string message, bool isInternal);
    Task<bool> UpdateStatusAsync(int complaintId, string expectedOldStatus, string newStatus, int changedBy, string? note);
    Task<bool> AssignAsync(int complaintId, int? assigneeId);
    Task<ComplaintStats> GetStatsAsync(int? ownerUserId);
    Task<IReadOnlyList<Category>> GetCategoriesAsync();
    Task<bool> CategoryExistsAsync(int categoryId);
}

public sealed class ComplaintRepository : IComplaintRepository
{
    private const string SelectComplaint = """
        SELECT c.Id, c.ReferenceNo, c.UserId, u.FullName AS SubmittedBy, c.CategoryId, cat.Name AS CategoryName,
               c.Title, c.Description, c.Priority, c.Status, c.AssignedToUserId, a.FullName AS AssignedToName,
               c.CreatedAtUtc, c.UpdatedAtUtc, c.ResolvedAtUtc
          FROM dbo.Complaints c
          JOIN dbo.Users u        ON u.Id = c.UserId
          JOIN dbo.Categories cat ON cat.Id = c.CategoryId
          LEFT JOIN dbo.Users a   ON a.Id = c.AssignedToUserId
        """;

    // Sort keys come from an allow-list - never from raw user input.
    private static readonly Dictionary<string, string> SortMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["newest"]   = "c.CreatedAtUtc DESC",
        ["oldest"]   = "c.CreatedAtUtc ASC",
        ["updated"]  = "c.UpdatedAtUtc DESC",
        ["priority"] = "CASE c.Priority WHEN 'Critical' THEN 0 WHEN 'High' THEN 1 WHEN 'Medium' THEN 2 ELSE 3 END, c.CreatedAtUtc DESC",
    };

    private readonly IDbConnectionFactory _db;

    public ComplaintRepository(IDbConnectionFactory db) => _db = db;

    public async Task<int> CreateAsync(int userId, CreateComplaintRequest r, IReadOnlyList<NewAttachment> attachments)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        using var tx = conn.BeginTransaction();

        var id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO dbo.Complaints (UserId, CategoryId, Title, Description, Priority, Status)
            OUTPUT INSERTED.Id
            VALUES (@userId, @CategoryId, @Title, @Description, @Priority, 'Open');
            """, new { userId, r.CategoryId, r.Title, r.Description, r.Priority }, tx);

        await conn.ExecuteAsync("""
            INSERT INTO dbo.ComplaintStatusHistory (ComplaintId, OldStatus, NewStatus, Note, ChangedByUserId)
            VALUES (@id, NULL, 'Open', N'Complaint submitted', @userId);
            """, new { id, userId }, tx);

        if (attachments.Count > 0)
        {
            await conn.ExecuteAsync("""
                INSERT INTO dbo.ComplaintAttachments
                    (ComplaintId, OriginalFileName, StoredFileName, ContentType, SizeBytes, UploadedByUserId)
                VALUES (@ComplaintId, @OriginalFileName, @StoredFileName, @ContentType, @SizeBytes, @UploadedByUserId);
                """,
                attachments.Select(a => new
                {
                    ComplaintId = id, a.OriginalFileName, a.StoredFileName, a.ContentType, a.SizeBytes,
                    UploadedByUserId = userId
                }), tx);
        }

        tx.Commit();
        return id;
    }

    public async Task<(IReadOnlyList<ComplaintRow> Items, int Total)> SearchAsync(
        ComplaintQuery q, int? ownerUserId, int? assigneeUserId)
    {
        var where = new StringBuilder("WHERE 1 = 1");
        var p = new DynamicParameters();

        if (ownerUserId is not null)    { where.Append(" AND c.UserId = @ownerUserId"); p.Add("ownerUserId", ownerUserId); }
        if (assigneeUserId is not null) { where.Append(" AND c.AssignedToUserId = @assigneeUserId"); p.Add("assigneeUserId", assigneeUserId); }
        if (!string.IsNullOrWhiteSpace(q.Status))   { where.Append(" AND c.Status = @status"); p.Add("status", q.Status); }
        if (!string.IsNullOrWhiteSpace(q.Priority)) { where.Append(" AND c.Priority = @priority"); p.Add("priority", q.Priority); }
        if (q.CategoryId is > 0)                    { where.Append(" AND c.CategoryId = @categoryId"); p.Add("categoryId", q.CategoryId); }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            where.Append(" AND (c.Title LIKE @search ESCAPE '\\' OR c.ReferenceNo LIKE @search ESCAPE '\\' OR c.Description LIKE @search ESCAPE '\\')");
            p.Add("search", SqlLike.Contains(q.Search));
        }

        var orderBy = SortMap.GetValueOrDefault(q.Sort ?? "newest", SortMap["newest"]);
        p.Add("offset", (q.Page - 1) * q.PageSize);
        p.Add("pageSize", q.PageSize);

        var sql = $"""
            SELECT COUNT(*) FROM dbo.Complaints c {where};
            {SelectComplaint}
            {where}
            ORDER BY {orderBy}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;

        using var conn = await _db.CreateOpenConnectionAsync();
        using var multi = await conn.QueryMultipleAsync(sql, p);
        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<ComplaintRow>()).ToList();
        return (items, total);
    }

    public async Task<ComplaintRow?> GetAsync(int id)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<ComplaintRow>($"{SelectComplaint} WHERE c.Id = @id", new { id });
    }

    public async Task<IReadOnlyList<CommentRow>> GetCommentsAsync(int complaintId, bool includeInternal)
    {
        const string sql = """
            SELECT cm.Id, cm.ComplaintId, cm.UserId, u.FullName AS AuthorName, u.Role AS AuthorRole,
                   cm.Message, cm.IsInternal, cm.CreatedAtUtc
              FROM dbo.ComplaintComments cm
              JOIN dbo.Users u ON u.Id = cm.UserId
             WHERE cm.ComplaintId = @complaintId
               AND (@includeInternal = 1 OR cm.IsInternal = 0)
             ORDER BY cm.CreatedAtUtc;
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        return (await conn.QueryAsync<CommentRow>(sql, new { complaintId, includeInternal })).ToList();
    }

    public async Task<IReadOnlyList<AttachmentRow>> GetAttachmentsAsync(int complaintId)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return (await conn.QueryAsync<AttachmentRow>(
            "SELECT * FROM dbo.ComplaintAttachments WHERE ComplaintId = @complaintId ORDER BY Id",
            new { complaintId })).ToList();
    }

    public async Task<AttachmentRow?> GetAttachmentAsync(int complaintId, int attachmentId)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<AttachmentRow>(
            "SELECT * FROM dbo.ComplaintAttachments WHERE Id = @attachmentId AND ComplaintId = @complaintId",
            new { complaintId, attachmentId });
    }

    public async Task<IReadOnlyList<StatusHistoryRow>> GetHistoryAsync(int complaintId)
    {
        const string sql = """
            SELECT h.Id, h.OldStatus, h.NewStatus, h.Note, u.FullName AS ChangedBy, h.ChangedAtUtc
              FROM dbo.ComplaintStatusHistory h
              JOIN dbo.Users u ON u.Id = h.ChangedByUserId
             WHERE h.ComplaintId = @complaintId
             ORDER BY h.ChangedAtUtc, h.Id;
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        return (await conn.QueryAsync<StatusHistoryRow>(sql, new { complaintId })).ToList();
    }

    public async Task<int> AddCommentAsync(int complaintId, int userId, string message, bool isInternal)
    {
        const string sql = """
            INSERT INTO dbo.ComplaintComments (ComplaintId, UserId, Message, IsInternal)
            OUTPUT INSERTED.Id
            VALUES (@complaintId, @userId, @message, @isInternal);
            UPDATE dbo.Complaints SET UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @complaintId;
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(sql, new { complaintId, userId, message, isInternal });
    }

    public async Task<bool> UpdateStatusAsync(int complaintId, string expectedOldStatus, string newStatus, int changedBy, string? note)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        using var tx = conn.BeginTransaction();

        // "WHERE Status = @expectedOldStatus" is an optimistic-concurrency guard:
        // if someone else changed the status first, nothing is updated.
        var updated = await conn.ExecuteAsync("""
            UPDATE dbo.Complaints
               SET Status        = @newStatus,
                   UpdatedAtUtc  = SYSUTCDATETIME(),
                   ResolvedAtUtc = CASE WHEN @newStatus IN ('Resolved','Closed') THEN COALESCE(ResolvedAtUtc, SYSUTCDATETIME())
                                        WHEN @newStatus IN ('Open','InProgress') THEN NULL
                                        ELSE ResolvedAtUtc END
             WHERE Id = @complaintId AND Status = @expectedOldStatus;
            """, new { complaintId, expectedOldStatus, newStatus }, tx);

        if (updated != 1)
        {
            tx.Rollback();
            return false;
        }

        await conn.ExecuteAsync("""
            INSERT INTO dbo.ComplaintStatusHistory (ComplaintId, OldStatus, NewStatus, Note, ChangedByUserId)
            VALUES (@complaintId, @expectedOldStatus, @newStatus, @note, @changedBy);
            """, new { complaintId, expectedOldStatus, newStatus, note, changedBy }, tx);

        tx.Commit();
        return true;
    }

    public async Task<bool> AssignAsync(int complaintId, int? assigneeId)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        var rows = await conn.ExecuteAsync(
            "UPDATE dbo.Complaints SET AssignedToUserId = @assigneeId, UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @complaintId",
            new { complaintId, assigneeId });
        return rows == 1;
    }

    public async Task<ComplaintStats> GetStatsAsync(int? ownerUserId)
    {
        const string sql = """
            SELECT COUNT(*)                                                   AS Total,
                   ISNULL(SUM(CASE WHEN Status = 'Open'       THEN 1 END), 0) AS [Open],
                   ISNULL(SUM(CASE WHEN Status = 'InProgress' THEN 1 END), 0) AS InProgress,
                   ISNULL(SUM(CASE WHEN Status = 'Resolved'   THEN 1 END), 0) AS Resolved,
                   ISNULL(SUM(CASE WHEN Status = 'Closed'     THEN 1 END), 0) AS Closed,
                   ISNULL(SUM(CASE WHEN Status = 'Rejected'   THEN 1 END), 0) AS Rejected,
                   ISNULL(SUM(CASE WHEN Priority = 'Critical' AND Status IN ('Open','InProgress') THEN 1 END), 0) AS Critical
              FROM dbo.Complaints
             WHERE (@ownerUserId IS NULL OR UserId = @ownerUserId);
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        var row = await conn.QuerySingleAsync(sql, new { ownerUserId });
        return new ComplaintStats((int)row.Total, (int)row.Open, (int)row.InProgress, (int)row.Resolved,
            (int)row.Closed, (int)row.Rejected, (int)row.Critical);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync()
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return (await conn.QueryAsync<Category>(
            "SELECT Id, Name, IsActive FROM dbo.Categories WHERE IsActive = 1 ORDER BY Name")).ToList();
    }

    public async Task<bool> CategoryExistsAsync(int categoryId)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Categories WHERE Id = @categoryId AND IsActive = 1) THEN 1 ELSE 0 END",
            new { categoryId });
    }
}
