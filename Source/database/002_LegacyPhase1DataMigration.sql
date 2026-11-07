/*
   EOEMS V5.1 Phase 1 legacy-data migration.

   Purpose
   -------
   Preserve useful identity metadata and historical operation logs from the
   original Phase 1 database without carrying forward its plaintext passwords.

   Preconditions
   -------------
   1. Take a tested backup.
   2. Run 001_SecureAuthentication.sql first.
   3. Ensure dbo.User_Registration_Table and dbo.Logs_Table are present in the
      same target database, either because this is an approved migration copy
      or because their contents were staged there by the database administrator.
   4. Review LegacyUserMigrationQueue and provision every SecureUsers account
      manually with a fresh PBKDF2 password and an approved application role.

   This script deliberately does NOT select, copy, compare or transform
   dbo.User_Registration_Table.User_Password.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.SecureUsers', N'U') IS NULL
    THROW 51000, 'Run 001_SecureAuthentication.sql before this migration.', 1;

IF OBJECT_ID(N'dbo.User_Registration_Table', N'U') IS NULL
    THROW 51001, 'Legacy dbo.User_Registration_Table was not found in this database.', 1;

IF OBJECT_ID(N'dbo.Logs_Table', N'U') IS NULL
    THROW 51002, 'Legacy dbo.Logs_Table was not found in this database.', 1;

IF OBJECT_ID(N'dbo.LegacyUserMigrationQueue', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LegacyUserMigrationQueue
    (
        LegacyUserNo INT NOT NULL CONSTRAINT PK_LegacyUserMigrationQueue PRIMARY KEY,
        LoginName NVARCHAR(100) NOT NULL,
        DisplayName NVARCHAR(200) NULL,
        LegacyRoleName NVARCHAR(100) NULL,
        WasLegacyActive BIT NOT NULL,
        LegacyRegisteredDate DATETIME2(0) NULL,
        RequiresPasswordReset BIT NOT NULL CONSTRAINT DF_LegacyUserMigrationQueue_RequiresPasswordReset DEFAULT (1),
        ImportedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_LegacyUserMigrationQueue_ImportedUtc DEFAULT SYSUTCDATETIME()
    );
END;

IF OBJECT_ID(N'dbo.LegacyOperationLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LegacyOperationLog
    (
        LegacyOperationLogId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LegacyOperationLog PRIMARY KEY,
        SourceLogId INT NOT NULL,
        LegacyUserId NVARCHAR(100) NULL,
        OccurredUtc DATETIME2(0) NULL,
        Operation NVARCHAR(100) NULL,
        ImportedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_LegacyOperationLog_ImportedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_LegacyOperationLog_SourceLogId UNIQUE (SourceLogId)
    );
    CREATE INDEX IX_LegacyOperationLog_OccurredUtc ON dbo.LegacyOperationLog (OccurredUtc DESC);
END;

;WITH SourceUsers AS
(
    SELECT
        User_No AS LegacyUserNo,
        NULLIF(LTRIM(RTRIM(User_ID)), N'') AS LoginName,
        NULLIF(LTRIM(RTRIM(CONCAT(COALESCE(User_Name, N''), N' ', COALESCE(User_Lastname, N'')))), N'') AS DisplayName,
        NULLIF(LTRIM(RTRIM(User_Type)), N'') AS LegacyRoleName,
        CONVERT(BIT, CASE WHEN LOWER(LTRIM(RTRIM(COALESCE(User_Active, N'')))) IN (N'yes', N'active', N'1', N'true') THEN 1 ELSE 0 END) AS WasLegacyActive,
        CONVERT(DATETIME2(0), User_RegisteredDate) AS LegacyRegisteredDate
    FROM dbo.User_Registration_Table
)
UPDATE target
SET
    LoginName = source.LoginName,
    DisplayName = source.DisplayName,
    LegacyRoleName = source.LegacyRoleName,
    WasLegacyActive = source.WasLegacyActive,
    LegacyRegisteredDate = source.LegacyRegisteredDate
FROM dbo.LegacyUserMigrationQueue AS target
INNER JOIN SourceUsers AS source ON source.LegacyUserNo = target.LegacyUserNo
WHERE source.LoginName IS NOT NULL;

;WITH SourceUsers AS
(
    SELECT
        User_No AS LegacyUserNo,
        NULLIF(LTRIM(RTRIM(User_ID)), N'') AS LoginName,
        NULLIF(LTRIM(RTRIM(CONCAT(COALESCE(User_Name, N''), N' ', COALESCE(User_Lastname, N'')))), N'') AS DisplayName,
        NULLIF(LTRIM(RTRIM(User_Type)), N'') AS LegacyRoleName,
        CONVERT(BIT, CASE WHEN LOWER(LTRIM(RTRIM(COALESCE(User_Active, N'')))) IN (N'yes', N'active', N'1', N'true') THEN 1 ELSE 0 END) AS WasLegacyActive,
        CONVERT(DATETIME2(0), User_RegisteredDate) AS LegacyRegisteredDate
    FROM dbo.User_Registration_Table
)
INSERT INTO dbo.LegacyUserMigrationQueue
    (LegacyUserNo, LoginName, DisplayName, LegacyRoleName, WasLegacyActive, LegacyRegisteredDate)
SELECT
    source.LegacyUserNo,
    source.LoginName,
    source.DisplayName,
    source.LegacyRoleName,
    source.WasLegacyActive,
    source.LegacyRegisteredDate
FROM SourceUsers AS source
WHERE source.LoginName IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.LegacyUserMigrationQueue AS target
      WHERE target.LegacyUserNo = source.LegacyUserNo
  );

INSERT INTO dbo.LegacyOperationLog (SourceLogId, LegacyUserId, OccurredUtc, Operation)
SELECT
    source.Log_ID,
    source.User_ID,
    CONVERT(DATETIME2(0), source.Log_Date),
    source.Operation
FROM dbo.Logs_Table AS source
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.LegacyOperationLog AS target
    WHERE target.SourceLogId = source.Log_ID
);

COMMIT TRANSACTION;

SELECT
    (SELECT COUNT_BIG(*) FROM dbo.LegacyUserMigrationQueue) AS QueuedLegacyUsers,
    (SELECT COUNT_BIG(*) FROM dbo.LegacyOperationLog) AS ImportedLegacyOperationLogs;
