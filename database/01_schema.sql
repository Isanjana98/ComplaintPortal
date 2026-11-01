/* =====================================================================
   Complaint Portal - SQL Server schema
   Run against SQL Server 2019+ / Azure SQL.
   ===================================================================== */

IF DB_ID('ComplaintPortal') IS NULL
    CREATE DATABASE ComplaintPortal;
GO

USE ComplaintPortal;
GO

/* ---------- Users ---------- */
IF OBJECT_ID('dbo.Users') IS NULL
CREATE TABLE dbo.Users
(
    Id                INT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    FullName          NVARCHAR(100)      NOT NULL,
    Email             NVARCHAR(256)      NOT NULL,
    PasswordHash      NVARCHAR(500)      NOT NULL,      -- PBKDF2 (ASP.NET Identity v3 format)
    Role              NVARCHAR(50)       NOT NULL CONSTRAINT DF_Users_Role DEFAULT ('User'),
    IsActive          BIT                NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
    SecurityStamp     UNIQUEIDENTIFIER   NOT NULL CONSTRAINT DF_Users_SecurityStamp DEFAULT (NEWID()),
    FailedLoginCount  INT                NOT NULL CONSTRAINT DF_Users_Failed DEFAULT (0),
    LockoutEndUtc     DATETIME2(0)       NULL,
    LastLoginUtc      DATETIME2(0)       NULL,
    CreatedAtUtc      DATETIME2(0)       NOT NULL CONSTRAINT DF_Users_Created DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_Users_Email UNIQUE (Email),
    CONSTRAINT CK_Users_Role CHECK (Role IN ('User','Staff','Admin'))
);
GO

/* ---------- Categories ---------- */
IF OBJECT_ID('dbo.Categories') IS NULL
CREATE TABLE dbo.Categories
(
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Categories PRIMARY KEY,
    Name      NVARCHAR(100)     NOT NULL CONSTRAINT UQ_Categories_Name UNIQUE,
    IsActive  BIT               NOT NULL CONSTRAINT DF_Categories_IsActive DEFAULT (1)
);
GO

/* ---------- Complaints ---------- */
IF OBJECT_ID('dbo.Complaints') IS NULL
CREATE TABLE dbo.Complaints
(
    Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Complaints PRIMARY KEY,
    ReferenceNo       AS ('CMP-' + RIGHT('00000000' + CAST(Id AS VARCHAR(10)), 8)) PERSISTED,
    UserId            INT               NOT NULL CONSTRAINT FK_Complaints_User REFERENCES dbo.Users(Id),
    CategoryId        INT               NOT NULL CONSTRAINT FK_Complaints_Category REFERENCES dbo.Categories(Id),
    Title             NVARCHAR(200)     NOT NULL,
    Description       NVARCHAR(4000)    NOT NULL,
    Priority          NVARCHAR(10)      NOT NULL CONSTRAINT DF_Complaints_Priority DEFAULT ('Medium'),
    Status            NVARCHAR(20)      NOT NULL CONSTRAINT DF_Complaints_Status DEFAULT ('Open'),
    AssignedToUserId  INT               NULL CONSTRAINT FK_Complaints_Assignee REFERENCES dbo.Users(Id),
    CreatedAtUtc      DATETIME2(0)      NOT NULL CONSTRAINT DF_Complaints_Created DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc      DATETIME2(0)      NOT NULL CONSTRAINT DF_Complaints_Updated DEFAULT (SYSUTCDATETIME()),
    ResolvedAtUtc     DATETIME2(0)      NULL,
    CONSTRAINT CK_Complaints_Priority CHECK (Priority IN ('Low','Medium','High','Critical')),
    CONSTRAINT CK_Complaints_Status   CHECK (Status IN ('Open','InProgress','Resolved','Closed','Rejected'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Complaints_User')
    CREATE INDEX IX_Complaints_User ON dbo.Complaints(UserId, CreatedAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Complaints_Status')
    CREATE INDEX IX_Complaints_Status ON dbo.Complaints(Status, CreatedAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Complaints_Assignee')
    CREATE INDEX IX_Complaints_Assignee ON dbo.Complaints(AssignedToUserId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Complaints_ReferenceNo')
    CREATE UNIQUE INDEX UX_Complaints_ReferenceNo ON dbo.Complaints(ReferenceNo);
GO

/* ---------- Comments ---------- */
IF OBJECT_ID('dbo.ComplaintComments') IS NULL
CREATE TABLE dbo.ComplaintComments
(
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ComplaintComments PRIMARY KEY,
    ComplaintId   INT               NOT NULL CONSTRAINT FK_Comments_Complaint REFERENCES dbo.Complaints(Id) ON DELETE CASCADE,
    UserId        INT               NOT NULL CONSTRAINT FK_Comments_User REFERENCES dbo.Users(Id),
    Message       NVARCHAR(2000)    NOT NULL,
    IsInternal    BIT               NOT NULL CONSTRAINT DF_Comments_Internal DEFAULT (0),  -- staff-only note
    CreatedAtUtc  DATETIME2(0)      NOT NULL CONSTRAINT DF_Comments_Created DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Comments_Complaint')
    CREATE INDEX IX_Comments_Complaint ON dbo.ComplaintComments(ComplaintId, CreatedAtUtc);
GO

/* ---------- Attachments ---------- */
IF OBJECT_ID('dbo.ComplaintAttachments') IS NULL
CREATE TABLE dbo.ComplaintAttachments
(
    Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ComplaintAttachments PRIMARY KEY,
    ComplaintId       INT               NOT NULL CONSTRAINT FK_Attachments_Complaint REFERENCES dbo.Complaints(Id) ON DELETE CASCADE,
    OriginalFileName  NVARCHAR(255)     NOT NULL,
    StoredFileName    NVARCHAR(100)     NOT NULL,   -- random GUID name, never user supplied
    ContentType       NVARCHAR(100)     NOT NULL,
    SizeBytes         BIGINT            NOT NULL,
    UploadedByUserId  INT               NOT NULL CONSTRAINT FK_Attachments_User REFERENCES dbo.Users(Id),
    CreatedAtUtc      DATETIME2(0)      NOT NULL CONSTRAINT DF_Attachments_Created DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Attachments_Complaint')
    CREATE INDEX IX_Attachments_Complaint ON dbo.ComplaintAttachments(ComplaintId);
GO

/* ---------- Status history ---------- */
IF OBJECT_ID('dbo.ComplaintStatusHistory') IS NULL
CREATE TABLE dbo.ComplaintStatusHistory
(
    Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ComplaintStatusHistory PRIMARY KEY,
    ComplaintId      INT               NOT NULL CONSTRAINT FK_History_Complaint REFERENCES dbo.Complaints(Id) ON DELETE CASCADE,
    OldStatus        NVARCHAR(20)      NULL,
    NewStatus        NVARCHAR(20)      NOT NULL,
    Note             NVARCHAR(1000)    NULL,
    ChangedByUserId  INT               NOT NULL CONSTRAINT FK_History_User REFERENCES dbo.Users(Id),
    ChangedAtUtc     DATETIME2(0)      NOT NULL CONSTRAINT DF_History_Changed DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_History_Complaint')
    CREATE INDEX IX_History_Complaint ON dbo.ComplaintStatusHistory(ComplaintId, ChangedAtUtc);
GO

/* ---------- Refresh tokens (stored as SHA-256 hashes only) ---------- */
IF OBJECT_ID('dbo.RefreshTokens') IS NULL
CREATE TABLE dbo.RefreshTokens
(
    Id                   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
    UserId               INT               NOT NULL CONSTRAINT FK_RefreshTokens_User REFERENCES dbo.Users(Id) ON DELETE CASCADE,
    TokenHash            CHAR(64)          NOT NULL,
    ExpiresAtUtc         DATETIME2(0)      NOT NULL,
    CreatedAtUtc         DATETIME2(0)      NOT NULL CONSTRAINT DF_RefreshTokens_Created DEFAULT (SYSUTCDATETIME()),
    CreatedByIp          NVARCHAR(45)      NULL,
    RevokedAtUtc         DATETIME2(0)      NULL,
    ReplacedByTokenHash  CHAR(64)          NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RefreshTokens_Hash')
    CREATE UNIQUE INDEX UX_RefreshTokens_Hash ON dbo.RefreshTokens(TokenHash);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RefreshTokens_User')
    CREATE INDEX IX_RefreshTokens_User ON dbo.RefreshTokens(UserId);
GO

/* ---------- Audit log ---------- */
IF OBJECT_ID('dbo.AuditLogs') IS NULL
CREATE TABLE dbo.AuditLogs
(
    Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLogs PRIMARY KEY,
    UserId        INT                  NULL,
    Action        NVARCHAR(100)        NOT NULL,
    EntityType    NVARCHAR(50)         NULL,
    EntityId      NVARCHAR(50)         NULL,
    IpAddress     NVARCHAR(45)         NULL,
    Details       NVARCHAR(1000)       NULL,
    CreatedAtUtc  DATETIME2(0)         NOT NULL CONSTRAINT DF_AuditLogs_Created DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLogs_Created')
    CREATE INDEX IX_AuditLogs_Created ON dbo.AuditLogs(CreatedAtUtc DESC);
GO

/* ---------- Seed categories ---------- */
MERGE dbo.Categories AS t
USING (VALUES (N'Service Quality'), (N'Billing & Payments'), (N'Technical Issue'),
              (N'Staff Behaviour'), (N'Delivery / Logistics'), (N'Facilities'), (N'Other')) AS s(Name)
   ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name) VALUES (s.Name);
GO

/* NEXT: run 03_roles_permissions.sql to enable role & permission management. */

/* NOTE: The first Admin account is created by the API on startup from
   the "Seed" section of appsettings / user-secrets, so the password is
   hashed with the same PBKDF2 algorithm the app uses. */
