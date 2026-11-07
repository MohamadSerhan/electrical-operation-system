Imports Npgsql

Public NotInheritable Class PostgresConnectionSettings
    Public Property Host As String = "localhost"
    Public Property Port As Integer = 5432
    Public Property Database As String = "eoems"
    Public Property Username As String = PostgresServiceAccount.RoleName
    Public Property Password As String = String.Empty

    Public Function ToBuilder() As NpgsqlConnectionStringBuilder
        Return New NpgsqlConnectionStringBuilder With {
            .Host = Host, .Port = Port, .Database = Database,
            .Username = Username, .Password = Password, .Timeout = 5}
    End Function
End Class
