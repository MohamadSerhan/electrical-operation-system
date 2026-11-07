# Sets up the EOEMS PostgreSQL service role automatically. Must run elevated (the app requests this).
# PostgreSQL local administrator access is trusted for a few seconds, the app creates the dedicated
# 'eoems_app' role with a random password, and the original pg_hba.conf is always restored.
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [int]$Port = 5432,
    [string]$ServiceName = '',
    [string]$DataDir = '',
    [string]$PgCtl = ''
)

$ErrorActionPreference = 'Stop'

function Write-Result([string]$text) {
    [IO.File]::WriteAllText($ResultPath, $text)
}

function Restart-Database {
    if ($ServiceName) {
        Restart-Service -Name $ServiceName -Force
    } else {
        & $PgCtl reload -D $DataDir | Out-Null
        Start-Sleep -Seconds 2
    }
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        try {
            $client = New-Object Net.Sockets.TcpClient
            $client.Connect('127.0.0.1', $Port)
            $client.Close()
            return
        } catch { Start-Sleep -Milliseconds 500 }
    }
    throw 'PostgreSQL did not come back in time.'
}

$restoreNeeded = $false
$hba = $null
$backup = $null
try {
    if (-not (Test-Path $ExePath)) { throw 'The application file was not found.' }
    if ($ServiceName -eq '' -and $DataDir -eq '') {
        $service = Get-CimInstance Win32_Service | Where-Object { $_.Name -like 'postgresql*' } | Select-Object -First 1
        if (-not $service) { throw 'No PostgreSQL Windows service was found on this computer.' }
        $ServiceName = $service.Name
        if ($service.PathName -match '-D\s+"([^"]+)"') { $DataDir = $Matches[1].Trim() }
        elseif ($service.PathName -match '-D\s+(\S+)') { $DataDir = $Matches[1].Trim() }
    }
    if (-not $DataDir) { throw 'The PostgreSQL data folder could not be located.' }
    $hba = Join-Path $DataDir 'pg_hba.conf'
    if (-not (Test-Path $hba)) { throw 'pg_hba.conf was not found.' }

    $backup = "$hba.eoems-backup"
    Copy-Item $hba $backup -Force
    $original = [IO.File]::ReadAllText($hba)
    $rules = "# EOEMS temporary setup rules - removed automatically`r`n" +
             "host all postgres 127.0.0.1/32 trust`r`n" +
             "host all postgres ::1/128 trust`r`n"
    $restoreNeeded = $true
    [IO.File]::WriteAllText($hba, $rules + $original, (New-Object Text.UTF8Encoding($false)))
    Restart-Database

    Remove-Item $ResultPath -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $ExePath -ArgumentList '--provision-db', "`"$ResultPath`"" -Wait -PassThru
    if (-not (Test-Path $ResultPath)) { throw "The setup step produced no result (exit code $($process.ExitCode))." }
}
catch {
    Write-Result ("FAIL|" + $_.Exception.Message)
}
finally {
    if ($restoreNeeded) {
        try {
            Copy-Item $backup $hba -Force
            Restart-Database
            Remove-Item $backup -ErrorAction SilentlyContinue
        } catch {
            Write-Result ("FAIL|Setup finished but the original pg_hba.conf could not be restored automatically. Restore " + $backup + " manually.")
        }
    }
}
