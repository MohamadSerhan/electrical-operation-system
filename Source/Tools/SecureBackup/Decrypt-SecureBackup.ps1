<#
.SYNOPSIS
  Decrypts a .enc backup created by Encrypt-SecureBackup.ps1 back into its original
  folder contents.

.PARAMETER InputFile
  The .enc file to decrypt.

.PARAMETER DestinationPath
  Folder to extract the recovered contents into. Created if it does not exist.

.PARAMETER Password
  The exact password used when the backup was created. There is no password
  recovery - an incorrect password produces garbage output or an error.

.EXAMPLE
  .\Decrypt-SecureBackup.ps1 -InputFile "C:\Backups\keygen.enc" -DestinationPath "C:\Restored\Keygen" -Password "correct horse battery staple"
#>
param(
    [Parameter(Mandatory = $true)][string]$InputFile,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [Parameter(Mandatory = $true)][securestring]$Password
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $InputFile)) {
    throw "Encrypted backup not found: $InputFile"
}

$allBytes = [System.IO.File]::ReadAllBytes($InputFile)
$offset = 0

$magic = [System.Text.Encoding]::ASCII.GetString($allBytes, $offset, 4); $offset += 4
if ($magic -ne "EOEB") { throw "Not a recognized secure backup file." }

$version = $allBytes[$offset]; $offset += 1
if ($version -ne 1) { throw "Unsupported backup format version: $version" }

$iterations = [System.BitConverter]::ToInt32($allBytes, $offset); $offset += 4
$salt = $allBytes[$offset..($offset + 15)]; $offset += 16
$iv = $allBytes[$offset..($offset + 15)]; $offset += 16
$cipherBytes = $allBytes[$offset..($allBytes.Length - 1)]

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
try {
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$deriveBytes = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($plainPassword, $salt, $iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
$key = $deriveBytes.GetBytes(32)
$plainPassword = $null

$aes = [System.Security.Cryptography.Aes]::Create()
$aes.KeySize = 256
$aes.Key = $key
$aes.IV = $iv
$aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
$aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7

$decryptor = $aes.CreateDecryptor()
try {
    $zipBytes = $decryptor.TransformFinalBlock($cipherBytes, 0, $cipherBytes.Length)
} catch {
    throw "Decryption failed - the password is likely incorrect."
}

$tempZip = [System.IO.Path]::GetTempFileName() + ".zip"
[System.IO.File]::WriteAllBytes($tempZip, $zipBytes)

if (-not (Test-Path $DestinationPath)) {
    New-Item -ItemType Directory -Force -Path $DestinationPath | Out-Null
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($tempZip, $DestinationPath)
Remove-Item $tempZip -Force

Write-Output "Restored to: $DestinationPath"
