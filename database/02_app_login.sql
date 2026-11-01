/* =====================================================================
   OPTIONAL (recommended for production): least-privilege SQL login.
   The API only needs SELECT/INSERT/UPDATE/DELETE - never 'sa' or db_owner.
   Change the password before running.
   ===================================================================== */
USE master;
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'complaint_app')
    CREATE LOGIN complaint_app WITH PASSWORD = 'Change_Me_Str0ng!Passw0rd', CHECK_POLICY = ON;
GO

USE ComplaintPortal;
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'complaint_app')
    CREATE USER complaint_app FOR LOGIN complaint_app;
GO
ALTER ROLE db_datareader ADD MEMBER complaint_app;
ALTER ROLE db_datawriter ADD MEMBER complaint_app;
GO
/* Audit logs should be append-only for the application */
DENY UPDATE, DELETE ON dbo.AuditLogs TO complaint_app;
GO
