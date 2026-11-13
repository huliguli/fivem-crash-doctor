# Erzeugt das selbstsignierte Code-Signing-Zertifikat für die Auto-Update-Prüfung.
# Ergebnis in cert\ (gitignored): FiveMCrashDoctor.pfx, FiveMCrashDoctor.cer, pfx-password.txt.
# Das Zertifikat liegt zusätzlich im persönlichen Speicher (Cert:\CurrentUser\My) – KEIN Root-Import.
# Danach: Thumbprint in Platform/Updater.cs (TrustedThumbprints) pinnen und die GitHub-Secrets
# CODESIGN_PFX_BASE64 und CODESIGN_PFX_PASSWORD setzen.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\cert'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$pfxPath = Join-Path $OutDir 'FiveMCrashDoctor.pfx'
if (Test-Path $pfxPath) { throw "In $OutDir liegt schon ein Zertifikat. Nicht überschreiben – ein neuer Schlüssel bricht die Updates aller Nutzer." }

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=FiveM Crash Doctor' `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(3) -CertStoreLocation Cert:\CurrentUser\My

# Passwort: 32 Zeichen aus einem eindeutigen Alphabet (ohne 0/O, 1/l/I), ohne Modulo-Verzerrung
$alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$chars = New-Object System.Collections.Generic.List[char]
$buf = New-Object byte[] 1
while ($chars.Count -lt 32) {
    $rng.GetBytes($buf)
    if ($buf[0] -lt ([Math]::Floor(256 / $alphabet.Length) * $alphabet.Length)) { $chars.Add($alphabet[$buf[0] % $alphabet.Length]) }
}
$pw = -join $chars
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password (ConvertTo-SecureString $pw -AsPlainText -Force) | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'FiveMCrashDoctor.cer') | Out-Null
Set-Content -Path (Join-Path $OutDir 'pfx-password.txt') -Value $pw -NoNewline -Encoding ascii
Write-Output "Thumbprint: $($cert.Thumbprint)"
Write-Output "Gültig bis: $($cert.NotAfter.ToString('yyyy-MM-dd'))"
