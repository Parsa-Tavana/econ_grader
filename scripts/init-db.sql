-- ═══════════════════════════════════════════════════════════════════════════
-- Creates the least-privilege application login.
--
-- Run AFTER the first `docker compose up` (EF Core creates+migrates the
-- EconGrader database at api startup). Idempotent — safe to re-run.
--
-- Usage:
--   DB_APP_PASSWORD must be exported in the shell or set inline below.
--   docker exec -i econgrader-db /opt/mssql-tools18/bin/sqlcmd -C -S localhost \
--     -U sa -P "$SA_PASSWORD" -d master -i - < scripts/init-db.sql
--
-- Result: econgrader_app can read/write and run migrations in EconGrader ONLY.
-- It gets db_owner there because EF migrations need DDL; it has no access to
-- any other database and no server-level roles. sa remains admin-only.
-- ═══════════════════════════════════════════════════════════════════════════

-- Password comes from the environment via sqlcmd scripting variables so it
-- never appears inside this file. Pass with: -v AppPassword="$DB_APP_PASSWORD"
DECLARE @app_password nvarchar(128) = '$(AppPassword)';
IF (@app_password IS NULL OR LEN(@app_password) = 0)
BEGIN
    RAISERROR('AppPassword variable is required (-v AppPassword="...")', 16, 1);
    RETURN;
END

DECLARE @sql nvarchar(max);

-- Server-level login (no server roles).
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = 'econgrader_app')
BEGIN
    SET @sql = N'CREATE LOGIN econgrader_app WITH PASSWORD = ''' +
               REPLACE(@app_password, '''', '''''') + N''', CHECK_POLICY = ON;';
    EXEC (@sql);
    PRINT 'Created login econgrader_app';
END
ELSE
BEGIN
    SET @sql = N'ALTER LOGIN econgrader_app WITH PASSWORD = ''' +
               REPLACE(@app_password, '''', '''''') + N''';';
    EXEC (@sql);
    PRINT 'Login existed — password reset to match DB_APP_PASSWORD';
END

-- Database user mapped to the login, db_owner of EconGrader only
-- (EF Core __MigrationsHistory + DDL require db_ddladmin+; db_owner is the
-- documented, simplest fit — scoped to this one database).
USE EconGrader;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'econgrader_app')
BEGIN
    CREATE USER econgrader_app FOR LOGIN econgrader_app;
    PRINT 'Created user econgrader_app in EconGrader';
END;
ALTER ROLE db_owner ADD MEMBER econgrader_app;

-- Explicit deny-nothing else needed: without a user in other databases the
-- login cannot connect there, and it holds no server-level permissions.
PRINT 'Done — econgrader_app is db_owner of EconGrader only.';
