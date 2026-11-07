# Legacy database review

The supplied `dbScadaUI.mdf` was inspected on a disposable LocalDB copy. Its schema contains:

- `dbo.User_Registration_Table`: user number, user ID, type, password, name, last name, active state and registration date.
- `dbo.Logs_Table`: log ID, user ID, date and operation.

`User_Password` is an `nvarchar(100)` field. This indicates a legacy plain-text password design; its contents were intentionally not copied into this project or this document.

The application therefore defaults to `dbo.SecureUsers` plus `dbo.AuthenticationAudit`, created by `001_SecureAuthentication.sql`. The compatibility path for `User_Registration_Table` exists only for a controlled migration and is disabled in `appsettings.json`.

Do not ship the supplied MDF/LDF files with the app or use them as a production credential store without an approved migration and backup plan.
