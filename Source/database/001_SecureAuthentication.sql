/*
   EOEMS V5 secure authentication schema.
   Run this on the production SQL Server before setting Sql:ConnectionString.
   It does not create a user or a default password.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.SecureUsers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SecureUsers
    (
        UserId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_SecureUsers PRIMARY KEY DEFAULT NEWID(),
        LoginName NVARCHAR(100) NOT NULL,
        DisplayName NVARCHAR(200) NOT NULL,
        RoleName NVARCHAR(80) NOT NULL,
        PasswordHash NVARCHAR(500) NOT NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_SecureUsers_IsActive DEFAULT (1),
        CreatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_SecureUsers_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ChangedUtc DATETIME2(0) NULL,
        CONSTRAINT UQ_SecureUsers_LoginName UNIQUE (LoginName),
        CONSTRAINT CK_SecureUsers_PasswordHash CHECK (PasswordHash LIKE N'PBKDF2$%')
    );
END;

IF OBJECT_ID(N'dbo.AuthenticationAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuthenticationAudit
    (
        AuditId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuthenticationAudit PRIMARY KEY,
        LoginName NVARCHAR(100) NOT NULL,
        Succeeded BIT NOT NULL,
        Detail NVARCHAR(200) NOT NULL,
        OccurredUtc DATETIME2(0) NOT NULL CONSTRAINT DF_AuthenticationAudit_OccurredUtc DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_AuthenticationAudit_OccurredUtc ON dbo.AuthenticationAudit (OccurredUtc DESC);
END;

COMMIT TRANSACTION;
