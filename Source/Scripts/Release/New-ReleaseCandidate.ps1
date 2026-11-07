param(
    [switch]$Preview,
    [switch]$ApproveRelease,
    [switch]$PromoteToLatest
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ($PromoteToLatest -and -not $ApproveRelease) {
    throw "-PromoteToLatest requires -ApproveRelease."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$registryPath = Join-Path $repoRoot "VERSION.json"
$lockPath = "$registryPath.lock"
$appProject = Join-Path $repoRoot "EOEMS.PowerTransformersScadaUI.V5.vbproj"
$testProject = Join-Path $repoRoot "Tests\EOEMS.SmokeTests.vbproj"
$testOutput = Join-Path $repoRoot "Tests\bin\Release\net10.0-windows"

function Read-Registry {
    if (-not (Test-Path -LiteralPath $registryPath)) {
        throw "Version registry not found: $registryPath"
    }
    return Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
}

function Write-RegistryAtomic($registry) {
    $temporaryPath = "$registryPath.$([guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$registryPath.$([guid]::NewGuid().ToString('N')).bak"
    $json = ConvertTo-Json -InputObject $registry -Depth 20
    [System.IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    try {
        if (Test-Path -LiteralPath $registryPath) {
            [System.IO.File]::Replace($temporaryPath, $registryPath, $backupPath)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $registryPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force
        }
    }
}

function Invoke-WithRegistryLock([scriptblock]$Action) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $lockStream = $null
    while ($null -eq $lockStream) {
        try {
            $lockStream = [System.IO.File]::Open(
                $lockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Timed out acquiring version registry lock: $lockPath"
            }
            Start-Sleep -Milliseconds 100
        }
    }
    try {
        & $Action
    }
    finally {
        $lockStream.Dispose()
    }
}

function Parse-Version([string]$value) {
    $match = [regex]::Match($value, '^(?<major>[0-9]+)(?:\.(?<minor>[0-9]+))?$')
    if (-not $match.Success) {
        throw "Unsupported version '$value'; expected Vn or Vn.m integer format."
    }
    $major = [int]$match.Groups["major"].Value
    $minor = if ($match.Groups["minor"].Success) { [int]$match.Groups["minor"].Value } else { 0 }
    return [pscustomobject]@{ Major = $major; Minor = $minor }
}

function Get-Next-Version([string]$value) {
    $parsed = Parse-Version $value
    return "$($parsed.Major).$($parsed.Minor + 1)"
}

function Get-SourceTreeHash {
    $excludedDirectories = '\\(bin|obj|publish|Archive|Tested)\\'
    $files = Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Force |
        Where-Object {
            $_.FullName -notmatch $excludedDirectories -and
            $_.Name -notin @("VERSION.json", "eoems.db", "eoems.seed.db", "historian.db") -and
            $_.FullName -notmatch '\\data\\MeterLogs\\'
        } |
        Sort-Object { $_.FullName.Substring($repoRoot.Length).Replace("\", "/").ToLowerInvariant() }
    $lines = foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($repoRoot.Length).TrimStart("\").Replace("\", "/")
        $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relativePath`t$fileHash"
    }
    $payload = [System.Text.Encoding]::UTF8.GetBytes(([string]::Join("`n", $lines)) + "`n")
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($payload)).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Save-CandidateState([string]$candidate, [string]$status, [string]$buildStatus, [string]$testStatus, [string]$message, [string]$sourceHash) {
    Invoke-WithRegistryLock {
        $registry = Read-Registry
        $records = @($registry.candidateHistory)
        $record = $records | Where-Object { $_.version -eq $candidate } | Select-Object -Last 1
        if ($null -eq $record) {
            throw "Reserved candidate V$candidate is missing from VERSION.json."
        }
        $record.status = $status
        $record.buildStatus = $buildStatus
        $record.testStatus = $testStatus
        $record.sourceHash = "sha256:$sourceHash"
        $record.updatedAtUtc = [DateTime]::UtcNow.ToString("o")
        $record.message = $message
        $registry.latestBuildStatus = $buildStatus
        $registry.latestTestStatus = $testStatus
        $registry.lastBuildIdentifier = "V$candidate-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
        $registry.lastSourceHash = "sha256:$sourceHash"
        Write-RegistryAtomic $registry
    }
}

function Invoke-DotNet([string[]]$Arguments, [string]$Label) {
    Write-Output "== $Label =="
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

$registry = Read-Registry
$candidate = [string]$registry.nextCandidateVersion
Parse-Version $candidate | Out-Null
if ($Preview) {
    Write-Output "Next candidate: V$candidate"
    Write-Output "Current release: V$($registry.currentReleasedVersion)"
    Write-Output "No registry reservation, build, package, or promotion was performed."
    return
}

$reservation = Invoke-WithRegistryLock {
    $current = Read-Registry
    $candidate = [string]$current.nextCandidateVersion
    Parse-Version $candidate | Out-Null
    $used = @($current.reservedCandidates)
    if ($used -contains $candidate) {
        throw "Candidate V$candidate is already reserved; refusing to reuse it."
    }
    $candidateRecord = [pscustomobject]@{
        version = $candidate
        status = "Reserved"
        reservedAtUtc = [DateTime]::UtcNow.ToString("o")
        updatedAtUtc = [DateTime]::UtcNow.ToString("o")
        buildStatus = "pending"
        testStatus = "pending"
        sourceHash = ""
        message = ""
        releaseZip = ""
        packageSha256 = ""
    }
    $current.reservedCandidates = @($used + $candidate)
    $current.candidateHistory = @(@($current.candidateHistory) + $candidateRecord)
    $current.nextCandidateVersion = Get-Next-Version $candidate
    Write-RegistryAtomic $current
    return [pscustomobject]@{ Version = $candidate }
}
$candidate = [string]$reservation.Version
$reserved = $true

$sourceHash = ""
$buildStatus = "pending"
$testStatus = "pending"
$buildId = "V$candidate-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
$parsedCandidate = Parse-Version $candidate
$assemblyVersion = "$($parsedCandidate.Major).$($parsedCandidate.Minor).0"

try {
    $sourceHash = Get-SourceTreeHash
    $versionProperties = @(
        "-p:Version=$assemblyVersion",
        "-p:AssemblyVersion=$assemblyVersion",
        "-p:FileVersion=$assemblyVersion",
        "-p:InformationalVersion=$candidate",
        "-p:BuildIdentifier=$buildId",
        "-p:SourceHash=sha256:$sourceHash",
        "-p:BuildStatus=Passed",
        "-p:TestStatus=Pending"
    )
    Invoke-DotNet (@("build", $appProject, "-c", "Release") + $versionProperties) "Application Release build"
    $buildStatus = "passed (0 warnings/errors)"
    Invoke-DotNet (@("build", $testProject, "-c", "Release") + $versionProperties) "Smoke-test Release build"

    if (-not (Test-Path -LiteralPath $testOutput)) {
        throw "Smoke-test output not found: $testOutput"
    }
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("eoems-smoke-" + [guid]::NewGuid().ToString("N"))
    $temporaryTestOutput = Join-Path $temporaryRoot "Tests\bin\Release\net10.0-windows"
    try {
        New-Item -ItemType Directory -Path $temporaryTestOutput -Force | Out-Null
        Copy-Item -Path (Join-Path $testOutput "*") -Destination $temporaryTestOutput -Recurse -Force
        $temporaryData = Join-Path $temporaryRoot "data"
        New-Item -ItemType Directory -Path $temporaryData -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot "data\eoems.seed.db") -Destination (Join-Path $temporaryData "eoems.db")
        $testDll = Join-Path $temporaryTestOutput "EOEMS.SmokeTests.dll"
        Push-Location $temporaryRoot
        try {
            $testOutputLines = & dotnet $testDll (Join-Path $temporaryRoot "transformers.png") (Join-Path $temporaryRoot "analytics.png") 2>&1
            $testExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
        $testOutputLines | ForEach-Object { Write-Output $_ }
        if ($testExitCode -ne 0) {
            throw "Smoke suite failed with exit code $testExitCode."
        }
        $passLine = @($testOutputLines | Where-Object { "$_".Trim() -match '^PASS: \d+ assertions$' } | Select-Object -Last 1)
        if ($passLine.Count -eq 0) {
            throw "Smoke suite exited successfully but did not report its assertion count."
        }
        $testStatus = "passed ($($passLine[0].Trim()))"
    }
    finally {
        if ($temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }

    $versionProperties[-1] = "-p:TestStatus=$testStatus"
    Invoke-DotNet (@("build", $appProject, "-c", "Release") + $versionProperties) "Metadata-stamped application build"
    Save-CandidateState $candidate "VerifiedAwaitingReleaseApproval" $buildStatus $testStatus "Build and smoke tests passed; no package created without -ApproveRelease." $sourceHash

    if (-not $ApproveRelease) {
        Write-Output "Candidate V$candidate passed build/tests and is awaiting explicit release approval."
        return
    }

    $publish = Join-Path $repoRoot "bin\Staging\V$candidate\win-x64"
    $releaseDirectory = Join-Path $repoRoot "Tested\Releases"
    $zipPath = Join-Path $releaseDirectory "EOEMS_SCADA_V${candidate}_win-x64.zip"
    if (Test-Path -LiteralPath $publish) { throw "Publish staging path already exists: $publish" }
    if (Test-Path -LiteralPath $zipPath) { throw "Release ZIP already exists; refusing to overwrite: $zipPath" }
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    Invoke-DotNet (@(
        "publish", $appProject, "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-p:PublishSingleFile=false", "-o", $publish
    ) + $versionProperties) "Self-contained Windows x64 publish"

    foreach ($required in @(
        "EOEMS.PowerTransformersScadaUI.V5.exe",
        "appsettings.json",
        "data\README.txt",
        "data\eoems.seed.db",
        "Assets\IndustrialComplexAtSunset.png",
        "Assets\TwelliumLogo.png",
        "Assets\ElectricalOperationCenter.ico",
        "coreclr.dll",
        "hostfxr.dll"
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) {
            throw "Published folder is missing required item '$required'."
        }
    }

    $settings = Get-Content -LiteralPath (Join-Path $publish "appsettings.json") -Raw | ConvertFrom-Json
    if ($settings.Sql.StorageMode -ne "PortableSqlite" -or
        $settings.Sql.PortableDatabasePath -ne "data\eoems.db" -or
        -not [string]::IsNullOrEmpty($settings.Sql.ConnectionString) -or
        $settings.Sql.EnableLegacyPlaintextAuthentication) {
        throw "Published appsettings.json failed the portable authentication configuration checks."
    }

    $manifest = [ordered]@{
        product = "Electrical Operation Center"
        version = "V$candidate"
        buildIdentifier = $buildId
        releaseTimestampUtc = [DateTime]::UtcNow.ToString("o")
        sourceCommitOrHash = "sha256:$sourceHash"
        buildStatus = $buildStatus
        testStatus = $testStatus
        runtime = "win-x64"
        selfContained = $true
        singleFile = $false
        releaseApproval = "Approved by explicit -ApproveRelease invocation"
        livePlcTestPerformed = $false
        releaseZip = "EOEMS_SCADA_V${candidate}_win-x64.zip"
        packageSha256 = "Recorded in external release manifest after ZIP creation"
    }
    $manifestPath = Join-Path $publish "RELEASE_MANIFEST.json"
    [System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 10) + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $packageHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entryNames = @($zip.Entries | ForEach-Object { $_.FullName.Replace("\", "/") })
        foreach ($required in @("EOEMS.PowerTransformersScadaUI.V5.exe", "appsettings.json", "Assets/ElectricalOperationCenter.ico", "data/README.txt", "data/eoems.seed.db", "RELEASE_MANIFEST.json")) {
            if ($entryNames -notcontains $required) {
                throw "Release ZIP failed integrity check; missing '$required'."
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    $externalManifest = [ordered]@{
        product = $manifest.product
        version = $manifest.version
        buildIdentifier = $buildId
        releaseTimestampUtc = $manifest.releaseTimestampUtc
        sourceCommitOrHash = $manifest.sourceCommitOrHash
        buildStatus = $buildStatus
        testStatus = $testStatus
        releaseZip = "EOEMS_SCADA_V${candidate}_win-x64.zip"
        packageSha256 = $packageHash
        livePlcTestPerformed = $false
    }
    $externalManifestPath = Join-Path $releaseDirectory "RELEASE_MANIFEST_V${candidate}.json"
    [System.IO.File]::WriteAllText($externalManifestPath, (ConvertTo-Json -InputObject $externalManifest -Depth 10) + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

    Invoke-WithRegistryLock {
        $current = Read-Registry
        $record = @($current.candidateHistory | Where-Object { $_.version -eq $candidate } | Select-Object -Last 1)
        if ($record.Count -eq 0) { throw "Candidate record disappeared before promotion." }
        $current.previousReleasedVersion = [string]$current.currentReleasedVersion
        $current.currentReleasedVersion = $candidate
        $current.buildIdentifier = $buildId
        $current.releaseTimestampUtc = $manifest.releaseTimestampUtc
        $current.sourceCommitOrHash = $manifest.sourceCommitOrHash
        $current.buildStatus = $buildStatus
        $current.testStatus = $testStatus
        $current.releaseZipLocation = "Tested\Releases\EOEMS_SCADA_V${candidate}_win-x64.zip"
        $current.packageSha256 = $packageHash
        $record[0].status = "Released"
        $record[0].releaseZip = $current.releaseZipLocation
        $record[0].packageSha256 = $packageHash
        $record[0].updatedAtUtc = [DateTime]::UtcNow.ToString("o")
        Write-RegistryAtomic $current
    }

    if ($PromoteToLatest) {
        $latest = Join-Path $repoRoot "bin\Latest"
        $promotion = Join-Path $repoRoot ("bin\Latest.staging-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $promotion -Force | Out-Null
        Copy-Item -Path (Join-Path $publish "*") -Destination $promotion -Recurse -Force
        if (Test-Path -LiteralPath $latest) {
            $rollback = Join-Path $repoRoot ("Archive\OldBuilds\Latest-" + [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss"))
            Move-Item -LiteralPath $latest -Destination $rollback
        }
        Move-Item -LiteralPath $promotion -Destination $latest
    }

    Write-Output "Released V$candidate; package SHA-256: $packageHash"
}
catch {
    if ($reserved) {
        $status = if ($buildStatus -eq "pending") { "FailedBuild" } else { "FailedVerification" }
        try {
            Save-CandidateState $candidate $status $buildStatus $testStatus $_.Exception.Message $sourceHash
        }
        catch {
            Write-Error "Unable to record failed candidate state: $($_.Exception.Message)"
        }
    }
    throw
}
