Friend Module Program
    <STAThread()>
    Friend Sub Main(args As String())
        If args.Length >= 2 AndAlso String.Equals(args(0), "--provision-db", StringComparison.OrdinalIgnoreCase) Then
            ProvisionDatabaseHeadless(args(1))
            Return
        End If
        Using instance As New Threading.Mutex(False, "Local\EOEMS.SCADA.Recording")
            Dim acquired As Boolean
            Try
                acquired = instance.WaitOne(0)
            Catch ex As Threading.AbandonedMutexException
                acquired = True
            End Try
            If Not acquired Then
                MessageBox.Show("SCADA is already running. A second instance cannot start another recorder.",
                                "SCADA already running", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            Try
                RunApplication()
            Finally
                instance.ReleaseMutex()
            End Try
        End Using
    End Sub

    Private Sub RunApplication()
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)
        AddHandler Application.ThreadException, AddressOf OnUiThreadException
        AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnFatalException
        Try
            Dim restoreMessage = BackupRecoveryService.ApplyPendingRestoreAtStartup()
            If Not String.IsNullOrWhiteSpace(restoreMessage) Then
                MessageBox.Show(restoreMessage, "Backup & Recovery", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
        Catch ex As Exception
            MessageBox.Show("A staged restore could not be safely completed. The application will not start so that live files are not opened in a partial state." &
                            Environment.NewLine & Environment.NewLine & ex.Message,
                            "Backup recovery required",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error)
            Return
        End Try
        If Not LicenseGate.EnsureLicensed() Then Return
        Dim settings = AppSettingsStore.Load()
        EnsureAuthenticationDatabaseSeeded(settings)
        Application.Run(New LoginForm(settings))
    End Sub

    ' A UI-thread fault is logged and reported, and monitoring keeps running instead of the process closing.
    Private Sub OnUiThreadException(sender As Object, e As Threading.ThreadExceptionEventArgs)
        WriteErrorLog("UI", e.Exception)
        Try
            MessageBox.Show("An unexpected error occurred. Monitoring continues; details were written to data\logs\ui-errors.log." &
                            Environment.NewLine & Environment.NewLine & e.Exception.Message,
                            "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        Catch
        End Try
    End Sub

    Private Sub OnFatalException(sender As Object, e As UnhandledExceptionEventArgs)
        WriteErrorLog("FATAL", TryCast(e.ExceptionObject, Exception))
    End Sub

    Private Sub WriteErrorLog(kind As String, ex As Exception)
        Try
            Dim directory = IO.Path.Combine(AppContext.BaseDirectory, "data", "logs")
            IO.Directory.CreateDirectory(directory)
            IO.File.AppendAllText(IO.Path.Combine(directory, "ui-errors.log"),
                                  DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", Globalization.CultureInfo.InvariantCulture) & " " & kind & " " &
                                  If(ex Is Nothing, "(unknown)", ex.ToString()) & Environment.NewLine)
        Catch
        End Try
    End Sub

    ' The shipped seed is only used when no live authentication database exists, so a rebuild or
    ' republish can never overwrite live users and audit history.
    Private Sub EnsureAuthenticationDatabaseSeeded(settings As AppSettings)
        Try
            Dim databasePath = settings.Sql.ResolveDatabasePath()
            If IO.File.Exists(databasePath) Then Return
            Dim seedPath = IO.Path.Combine(AppContext.BaseDirectory, "data", "eoems.seed.db")
            If Not IO.File.Exists(seedPath) Then Return
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(databasePath))
            IO.File.Copy(seedPath, databasePath, overwrite:=False)
        Catch ex As IO.IOException
            ' Another instance created the database first, or the seed is unavailable; the
            ' authentication service creates its schema if needed.
        Catch ex As IO.InvalidDataException
            ' Invalid SQL settings are reported by the login form.
        End Try
    End Sub

    ' Called by Tools\Setup-Database.ps1 while PostgreSQL temporarily trusts local administrator
    ' access. No password is passed, printed or stored; only a status line is written.
    Private Sub ProvisionDatabaseHeadless(resultPath As String)
        Dim line As String
        Try
            Dim current = PostgresDatabase.LoadSettings()
            Dim result = PostgresServiceAccount.Provision(current.Host, current.Port, current.Database, "postgres", String.Empty)
            line = If(result.Succeeded, "OK|", "FAIL|") & result.Message
        Catch ex As Exception
            line = "FAIL|" & ex.Message
        End Try
        IO.File.WriteAllText(resultPath, line)
    End Sub
End Module
