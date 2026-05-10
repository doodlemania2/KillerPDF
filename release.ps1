#Requires -Version 5.1
<#
.SYNOPSIS
    KillerPDF release script: build → sign → SHA256 → print summary.
.DESCRIPTION
    1. Publishes for net9.0-windows/win-x64 — also runs bundle-source.ps1 to zip the source.
    2. Signs KillerPDF.exe with your Certum cert via signtool.
    3. Computes and prints the SHA256 for pasting into the landing pages.

.PARAMETER CertName
    CN (Subject) of your Certum certificate as it appears in the Windows cert store.
    Run: Get-ChildItem Cert:\CurrentUser\My | Select Subject
    to find it. Defaults to the placeholder below.

.PARAMETER SkipSign
    Skip signing (useful for a test build).

.EXAMPLE
    .\release.ps1 -CertName "Open Source Developer, Stephen ..."
#>
param(
    [string]$CertName   = "Open Source Developer Stephen Riley",
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$proj      = Join-Path $PSScriptRoot "KillerPDF.csproj"
$publishDir = Join-Path $PSScriptRoot "bin\Release\net9.0-windows\win-x64\publish"
$exe       = Join-Path $publishDir "KillerPDF.exe"

# ── 1. Build / Publish ──────────────────────────────────────────────────────
Write-Host "`n==> Building (Release, net9.0-windows, win-x64)..." -ForegroundColor Cyan

& dotnet publish $proj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true

if ($LASTEXITCODE -ne 0) { throw "Build failed." }
if (-not (Test-Path $exe)) { throw "EXE not found at: $exe" }
Write-Host "    EXE: $exe" -ForegroundColor Green

# ── 2. Sign ─────────────────────────────────────────────────────────────────
if (-not $SkipSign) {
    Write-Host "`n==> Signing with Certum cert: $CertName..." -ForegroundColor Cyan

    # Find signtool
    $signtool = $null
    $kitBase  = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kitBase) {
        $signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw "signtool.exe not found. Install Windows SDK." }
    Write-Host "    signtool: $signtool"

    & $signtool sign `
        /fd  sha256 `
        /tr  "http://timestamp.digicert.com" `
        /td  sha256 `
        /n   $CertName `
        /d   "KillerPDF" `
        /du  "https://pdf.killertools.net" `
        /v   $exe

    if ($LASTEXITCODE -ne 0) { throw "Signing failed. Is Certum SimplySign Desktop running?" }

    & $signtool verify /pa /v $exe
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed." }
    Write-Host "    Signed and verified OK" -ForegroundColor Green
} else {
    Write-Host "`n==> Skipping signing (-SkipSign)" -ForegroundColor Yellow
}

# ── 3. SHA256 ────────────────────────────────────────────────────────────────
Write-Host "`n==> Computing SHA256..." -ForegroundColor Cyan
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host "    SHA256: $hash" -ForegroundColor Green

# ── 4. Source zip ────────────────────────────────────────────────────────────
$srcZip = Get-ChildItem $publishDir -Filter "*-src.zip" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($srcZip) {
    Write-Host "`n==> Source zip: $($srcZip.FullName)" -ForegroundColor Green
} else {
    Write-Host "`n    (No source zip found — did bundle-source.ps1 run?)" -ForegroundColor Yellow
}

# ── 5. Summary ───────────────────────────────────────────────────────────────
Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "  KillerPDF v1.3.0 release artifacts" -ForegroundColor White
Write-Host   "  EXE  : $exe"
if ($srcZip) { Write-Host "  SRC  : $($srcZip.FullName)" }
Write-Host   "  SHA256: $hash" -ForegroundColor Green
Write-Host   ""
Write-Host   "  Paste SHA256 into:"
Write-Host   "    KillerPDF\pdf-landing\index.html (line ~183)"
Write-Host   "    killer-tools-site\src\tools\killer-pdf\killer-pdf.vue (line ~90)"
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
