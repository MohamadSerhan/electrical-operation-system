<#
.SYNOPSIS
  Encrypts a folder into a single password-protected file using AES-256-CBC with a
  PBKDF2 (SHA-256, 310,000 iterations) derived key. No external tools required -
  pure .NET cryptography built into Windows PowerShell / PowerShell 7.

.DESCRIPTION
  Used to make a safe, offline backup of secrets that must never be committed to
  source control (e.g. the Keygen tool, which embeds the private RSA signing key).
  The output .enc file is unreadable without the exact password used to create it.

.PARAMETER SourcePath
  Folder to back up.

.PARAMETER OutputFile
  Path to write the encrypted .enc file to.

.PARAMETER Password
  The password that protects the backup. Store this somewhere safe and separate
  from the backup file itself (e.g. a password manager) - if it is lost, the
  backup cannot be recovered by anyone, including the original author.

.EXAMPLE
  .\Encrypt-SecureBackup.ps1 -SourcePath "C:\Keygen" -OutputFile "C:\Backups\keygen.enc" -Password "correct horse battery staple"
#>
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$OutputFile,
    [Parameter(Mandatory = $true)][securestring]$Password
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SourcePath)) {
    throw "Source path not found: $SourcePath"
}

$outputDir = Split-Path -Parent $OutputFile
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

# 1) Zip the source folder into memory-backed temp file.
$tempZip = [System.IO.Path]::GetTempFileName() + ".zip"
if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($SourcePath, $tempZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$zipBytes = [System.IO.File]::ReadAllBytes($tempZip)
Remove-Item $tempZip -Force

# 2) Derive key material with PBKDF2 using a random salt.
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
try {
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$iterations = 310000
$salt = New-Object byte[] 16
$iv = New-Object byte[] 16
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($salt)
$rng.GetBytes($iv)

$deriveBytes = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($plainPassword, $salt, $iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
$key = $deriveBytes.GetBytes(32)
$plainPassword = $null

# 3) Encrypt with AES-256-CBC.
$aes = [System.Security.Cryptography.Aes]::Create()
$aes.KeySize = 256
$aes.Key = $key
$aes.IV = $iv
$aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
$aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7

$encryptor = $aes.CreateEncryptor()
$cipherBytes = $encryptor.TransformFinalBlock($zipBytes, 0, $zipBytes.Length)

# 4) Write header (magic + version + iterations + salt + iv) followed by ciphertext.
$magic = [System.Text.Encoding]::ASCII.GetBytes("EOEB")
$version = [byte[]]@(1)
$iterBytes = [System.BitConverter]::GetBytes([int32]$iterations)

$fs = [System.IO.File]::Create($OutputFile)
try {
    $fs.Write($magic, 0, $magic.Length)
    $fs.Write($version, 0, $version.Length)
    $fs.Write($iterBytes, 0, $iterBytes.Length)
    $fs.Write($salt, 0, $salt.Length)
    $fs.Write($iv, 0, $iv.Length)
    $fs.Write($cipherBytes, 0, $cipherBytes.Length)
} finally {
    $fs.Dispose()
}

Write-Output "Encrypted backup written: $OutputFile ($([math]::Round((Get-Item $OutputFile).Length / 1KB, 1)) KB)"
