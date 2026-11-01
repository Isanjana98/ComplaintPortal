/* =====================================================================
   Complaint Portal - Role & permission management
   Run AFTER 01_schema.sql. Safe to run more than once.
   ===================================================================== */
USE ComplaintPortal;
GO

/* ---------- Roles ---------- */
IF OBJECT_ID('dbo.Roles') IS NULL
CREATE TABLE dbo.Roles
(
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    Name          NVARCHAR(50)      NOT NULL CONSTRAINT UQ_Roles_Name UNIQUE,
    Description   NVARCHAR(200)     NULL,
    IsSystem      BIT               NOT NULL CONSTRAINT DF_Roles_IsSystem DEFAULT (0),  -- Admin/Staff/User: cannot be renamed or deleted
    CreatedAtUtc  DATETIME2(0)      NOT NULL CONSTRAINT DF_Roles_Created DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc  DATETIME2(0)      NOT NULL CONSTRAINT DF_Roles_Updated DEFAULT (SYSUTCDATETIME())
);
GO

/* ---------- Role -> permission mapping ---------- */
IF OBJECT_ID('dbo.RolePermissions') IS NULL
CREATE TABLE dbo.RolePermissions
(
    RoleId      INT           NOT NULL CONSTRAINT FK_RolePermissions_Role REFERENCES dbo.Roles(Id) ON DELETE CASCADE,
    Permission  NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleId, Permission)
);
GO

/* ---------- System roles ---------- */
MERGE dbo.Roles AS t
USING (VALUES
    (N'Admin', N'Full access to everything. Permissions cannot be changed.', 1),
    (N'Staff', N'Handles and resolves complaints.', 1),
    (N'User',  N'Customer who submits and tracks their own complaints.', 1)
) AS s(Name, Description, IsSystem)
   ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, Description, IsSystem) VALUES (s.Name, s.Description, s.IsSystem);
GO

/* Default permissions (Admin implicitly has ALL permissions in code, so it needs no rows). */
INSERT INTO dbo.RolePermissions (RoleId, Permission)
SELECT r.Id, p.Permission
  FROM dbo.Roles r
  JOIN (VALUES
        (N'User',  N'complaints.create'),
        (N'Staff', N'complaints.create'),
        (N'Staff', N'complaints.view_all'),
        (N'Staff', N'complaints.update_status'),
        (N'Staff', N'complaints.assign_self'),
        (N'Staff', N'complaints.internal_notes')
       ) AS p(RoleName, Permission) ON p.RoleName = r.Name
 WHERE NOT EXISTS (SELECT 1 FROM dbo.RolePermissions x WHERE x.RoleId = r.Id AND x.Permission = p.Permission)
   AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions y WHERE y.RoleId = r.Id); -- only seed roles that have no permissions yet
GO

/* ---------- Users: link to Roles instead of a fixed CHECK list ---------- */
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Users_Role')
    ALTER TABLE dbo.Users DROP CONSTRAINT CK_Users_Role;
GO
/* The FK needs Users.Role to match Roles.Name exactly (NVARCHAR(50)). */
IF (SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Users') AND name = 'Role') < 100
BEGIN
    ALTER TABLE dbo.Users DROP CONSTRAINT DF_Users_Role;
    ALTER TABLE dbo.Users ALTER COLUMN Role NVARCHAR(50) NOT NULL;
    ALTER TABLE dbo.Users ADD CONSTRAINT DF_Users_Role DEFAULT ('User') FOR Role;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Users_Role')
    ALTER TABLE dbo.Users WITH CHECK
        ADD CONSTRAINT FK_Users_Role FOREIGN KEY (Role) REFERENCES dbo.Roles(Name) ON UPDATE CASCADE;
GO

/* Force a password change at next sign-in (used for accounts created / reset by an admin). */
IF COL_LENGTH('dbo.Users', 'MustChangePassword') IS NULL
    ALTER TABLE dbo.Users ADD MustChangePassword BIT NOT NULL CONSTRAINT DF_Users_MustChange DEFAULT (0);
GO
IF COL_LENGTH('dbo.Users', 'CreatedByUserId') IS NULL
    ALTER TABLE dbo.Users ADD CreatedByUserId INT NULL CONSTRAINT FK_Users_CreatedBy REFERENCES dbo.Users(Id);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Users_Role')
    CREATE INDEX IX_Users_Role ON dbo.Users(Role);
GO
