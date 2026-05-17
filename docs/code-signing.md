# TDPdf code signing — current state & roadmap

Last reviewed: 2026-05-14

## Current state — Phase 0: Self-signed (Intune-trusted)

TDPdf.exe is signed with a self-signed code-signing certificate issued to
`CN=The Doodle Project (Self-Signed)`. The public part of the cert is
deployed to all corporate Windows devices via Intune, so on those devices:

- Authenticode signature validates (cert is in `LocalMachine\Root`).
- SmartScreen / Defender treat it as known publisher (cert is in
  `LocalMachine\TrustedPublisher`).

**This trust does NOT extend to the public.** On any device outside the
Intune-managed fleet, the cert is untrusted and SmartScreen will still
warn "Unknown publisher". Self-signing is a stopgap so corporate devices
stop blocking the EXE — nothing more.

### Steps (Phase 0)

All of these run in **elevated PowerShell on the Windows release box**.
`signtool` and `LocalMachine\*` cert stores are Windows-only — do not
attempt on macOS.

#### Step 1 — Generate the self-signed code-signing cert

```powershell
$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=The Doodle Project (Self-Signed), O=The Doodle Project LLC, L=Frisco, S=TX, C=US" `
  -KeyAlgorithm RSA `
  -KeyLength 4096 `
  -HashAlgorithm SHA256 `
  -KeyUsage DigitalSignature `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3") `
  -NotAfter (Get-Date).AddYears(5) `
  -CertStoreLocation Cert:\CurrentUser\My `
  -FriendlyName "TDPdf Self-Sign"

$cert.Thumbprint   # write this down — referenced below
```

The `TextExtension` line forces the Code Signing EKU
(`1.3.6.1.5.5.7.3.3`), which Authenticode requires.

#### Step 2 — Export PFX (private, keep secret) and CER (public, deploy)

```powershell
$workDir = "$env:USERPROFILE\Documents\TDPdf-CodeSign"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

# Public cert — goes to Intune, safe to share
Export-Certificate -Cert $cert -FilePath "$workDir\TDPdf-CodeSign.cer" -Type CERT

# Private key bundle — store in 1Password / Bitwarden, never commit
$pfxPwd = Read-Host -AsSecureString -Prompt "PFX export password"
Export-PfxCertificate -Cert $cert -FilePath "$workDir\TDPdf-CodeSign.pfx" -Password $pfxPwd
```

Stash the `.pfx` + password in the password manager. Lose it = re-issue
and re-deploy via Intune. The `.cer` is the public half — only that goes
to Intune.

#### Step 3 — Sign the EXE

`release.ps1` already accepts `-CertName`. After publishing once:

```powershell
.\release.ps1 -CertName "The Doodle Project (Self-Signed)"
```

The script prints a 10-second yellow "SimplySign Desktop not detected"
warning — fine for self-sign, ignore it. `signtool` picks the cert from
`Cert:\CurrentUser\My` by CN match.

Manual signing (no `release.ps1`):

```powershell
$signtool = (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
             Sort-Object FullName -Descending | Select-Object -First 1).FullName

& $signtool sign `
    /fd sha256 /td sha256 `
    /tr http://timestamp.digicert.com `
    /n "The Doodle Project (Self-Signed)" `
    /d "TDPdf" /du "https://thedoodleproject.com/tdpdf" `
    /v "bin\Release\net9.0-windows\win-x64\publish\TDPdf.exe"

& $signtool verify /pa /v "bin\Release\net9.0-windows\win-x64\publish\TDPdf.exe"
```

#### Step 4 — Build the Intune deployment script

Generate the base64 blob (same Windows box, after Step 2):

```powershell
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("$workDir\TDPdf-CodeSign.cer"))
$b64 | Set-Content -Encoding ASCII "$workDir\cert.b64.txt"
notepad "$workDir\cert.b64.txt"
```

Copy that single-line base64. Save the following as
**`Deploy-TDPdfCodeSignTrust.ps1`**, pasting the base64 into `$CertB64`:

```powershell
<#
.SYNOPSIS
    Trusts the TDPdf self-signed code-signing cert on this device.
.DESCRIPTION
    Installs the embedded cert into:
      - LocalMachine\Root              (chain validates)
      - LocalMachine\TrustedPublisher  (Authenticode auto-trusted,
                                       SmartScreen treats as known publisher)
    Idempotent: skips import if thumbprint already present.
    Deployed via Intune > Devices > Scripts and remediations > Platform scripts.
    Run as SYSTEM, 64-bit, NOT as logged-on user.
#>
$ErrorActionPreference = 'Stop'

$CertB64 = '<<<PASTE THE BASE64 STRING FROM cert.b64.txt HERE>>>'

$tmp = Join-Path $env:ProgramData 'TDPdf-CodeSign.cer'
[IO.File]::WriteAllBytes($tmp, [Convert]::FromBase64String($CertB64))

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $tmp
$thumb = $cert.Thumbprint
Write-Output "Cert thumbprint: $thumb"

foreach ($storeName in 'Root', 'TrustedPublisher') {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        $storeName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
    )
    $store.Open('ReadWrite')
    $existing = $store.Certificates | Where-Object { $_.Thumbprint -eq $thumb }
    if ($existing) {
        Write-Output "Already present in LocalMachine\$storeName"
    } else {
        $store.Add($cert)
        Write-Output "Imported into LocalMachine\$storeName"
    }
    $store.Close()
}

Remove-Item $tmp -Force
exit 0
```

#### Step 5 — Push to Intune

In **Intune Admin Center**:

1. **Devices → Scripts and remediations → Platform scripts → Add →
   Windows 10 and later**.
2. **Basics**: Name `TDPdf-CodeSign-Trust`, Description "Trusts
   self-signed code cert for TDPdf releases".
3. **Script settings**:
   - **Script location**: upload `Deploy-TDPdfCodeSignTrust.ps1`.
   - **Run this script using the logged on credentials**: **No** (must
     be SYSTEM to write `LocalMachine` stores).
   - **Enforce script signature check**: **No** (the script itself is
     unsigned).
   - **Run script in 64 bit PowerShell host**: **Yes**.
4. **Assignments**: target the device groups that should trust TDPdf
   (corporate employee Windows devices).
5. **Review + add**.

Intune retries failed runs, and the script is idempotent, so reruns
are safe.

#### Step 6 — Verify on a target device

After the device picks up the policy (usually within an hour; can
force via **Settings → Accounts → Access work or school → Info → Sync**):

```powershell
# Cert present in both stores
Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher |
    Where-Object Subject -like "*The Doodle Project (Self-Signed)*"

# Authenticode validates
Get-AuthenticodeSignature "C:\path\to\TDPdf.exe" | Format-List
# Expect: Status = Valid, SignerCertificate.Subject = your CN
```

If `Status` is `UnknownError` or `NotTrusted` on the target device,
the cert didn't make it into `LocalMachine\Root` — recheck Intune script
run state under **Devices → Monitor → Platform script run state**.

### What Phase 0 gets you (and what it doesn't)

On Intune-managed devices:

- `Get-AuthenticodeSignature` → `Valid`.
- No "Unknown publisher" string in any UAC / SmartScreen / installer prompt.
- Defender treats the publisher as known.
- AppLocker / WDAC rules of the form "allow if signed by [thumbprint]"
  work.

Outside the Intune tenant:

- Still "Unknown publisher".
- No SmartScreen reputation accrual that transfers to the eventual Certum
  cert — reputation is per-cert and resets at each phase transition.
- No free pass on Defender false-positives — still submit via the
  Defender portal if one happens.

### Files / locations

- Cert (private key, PFX): kept off-repo. Recommended: 1Password / Bitwarden
  vault entry `TDPdf self-sign code cert` containing the PFX + password.
  Also keep an unencrypted copy of the `.cer` (public only) for re-issuing
  Intune deployments.
- Intune deployment: **Devices > Scripts and remediations > Platform
  scripts**, script name `TDPdf-CodeSign-Trust`. Runs as system, 64-bit.
- Signing command (manual, on the Windows release box):
  `signtool sign /n "The Doodle Project (Self-Signed)" /fd sha256 /tr http://timestamp.digicert.com /td sha256 /v TDPdf.exe`
- `release.ps1` defaults already use `/n "The Doodle Project"`; pass
  `-CertName "The Doodle Project (Self-Signed)"` until the public cert
  lands, then drop the override.

### When to re-issue the self-signed cert

- It expires (default 5 yr).
- Private key compromised.
- The public Certum cert lands (Phase 1 below) → retire self-signed cert,
  remove from Intune deployment.

---

## Phase 1: Certum Open Source Code Signing (free, individual)

**Goal:** get a real CA-chained Authenticode signature on TDPdf releases so
SmartScreen reputation starts accruing for public downloads. Free. Issued
to **Derek Martin** as an individual (NOT The Doodle Project LLC) — the
publisher line on Windows download warnings will say "Derek Martin".

### Eligibility

- Project must be open source under an OSI-approved license. TDPdf is
  GPLv3 → qualifies.
- One free cert per individual.

### Steps (Phase 1)

1. Apply at **shop.certum.eu** → Code Signing → "Open Source Code Signing".
2. Submit to Certum:
   - Government photo ID (driver's license or passport).
   - Link to the open source project. Use:
     `https://github.com/doodlemania2/TDPdf` (must be public, with LICENSE
     and clear commit history under your name).
   - Notarized application form OR video identity verification.
3. Choose hardware delivery:
   - **simplySign cloud HSM** (recommended for CI use): cert lives in
     Certum cloud, signed via SimplySign Desktop. Requires SimplySign
     mobile app for 2FA.
   - **cryptoUSB physical token** (mailed from Poland): air-gapped, single
     machine.
4. Wait ~5–10 business days for Certum vetting.
5. After issuance:
   - Install SimplySign Desktop (or token drivers) on the Windows release box.
   - `Get-ChildItem Cert:\CurrentUser\My | Select Subject` → confirm CN.
   - Update `release.ps1` default `-CertName` to match the new CN.
   - Submit first signed release binary to Microsoft Defender at
     <https://www.microsoft.com/wdsi/filesubmission> to seed AV reputation.
6. Retire the self-signed cert:
   - Remove `TDPdf-CodeSign-Trust` script from Intune.
   - Optionally push a one-off cleanup script that removes the old cert
     from `LocalMachine\Root` and `LocalMachine\TrustedPublisher`.

### Reality check on Phase 1

- SmartScreen reputation starts at zero. Expect the "Unknown publisher"
  warning to be replaced by "Windows protected your PC — More info" for
  the first few hundred to few thousand downloads. Eventually clears.
- Publisher line shows your personal name, not LLC. If that's not
  acceptable, skip Phase 1 and go straight to Phase 2.

---

## Phase 2: Certum Standard OV (LLC, paid)

**Goal:** publisher line says **The Doodle Project LLC**. Real OV
validation of the Texas LLC.

### Prereqs (start in parallel with Phase 1)

- TX SOS file-stamped Certificate of Formation for The Doodle Project LLC.
- IRS EIN assignment letter (CP 575).
- LLC operating agreement.
- Verifiable LLC phone listing (Google Business profile or D&B / D-U-N-S).
  **Request D-U-N-S number now** at <https://dnb.com> — free, can take
  ~1 week.
- LLC address proof matching TX SOS filing (utility, bank, or lease).

### Steps (Phase 2)

1. Confirm Phase 1 is working end-to-end first (validates SimplySign /
   token workflow).
2. Buy at **shop.certum.eu** → Code Signing → "Standard Code Signing"
   (OV), 1 or 3 yr term.
3. Subject:
   - CN: `The Doodle Project LLC` (exact match to TX SOS filing — case,
     "LLC" vs "L.L.C.").
   - O: same.
   - L / ST / C: `Frisco / TX / US`.
4. Upload LLC docs to Certum portal.
5. Certum vetting (~5–10 business days):
   - Auto-checks TX SOS lookup.
   - Auto-checks D-U-N-S / Google Business listing.
   - Phone call to verified LLC number.
   - Video identity verification call with you.
6. After issuance:
   - Cert goes to same simplySign account or new token.
   - Update `release.ps1` default `-CertName` to `The Doodle Project LLC`.
   - Re-sign in-flight releases.
7. Update `app.manifest` / `AssemblyInfo.cs` `<assemblyIdentity>` and
   company strings if they reference the personal-name publisher from
   Phase 1.
8. Re-submit to Defender file submission portal under new publisher.

### SmartScreen reputation note

Each new cert (Phase 0 → 1 → 2 → 3) restarts SmartScreen reputation
**from zero**. There's no transfer between certs. Time the transitions
to minimize disruption: Phase 0 → 1 should happen as soon as Phase 1
is in hand; Phase 1 → 2 should wait until Phase 1 has banked a few
thousand clean downloads (worth keeping on a parallel signing path
during transition if feasible).

---

## Phase 3: Certum EV Code Signing (LLC, paid, optional)

**Goal:** instant SmartScreen reputation. Zero "Windows protected your
PC" warnings from day one of a new release.

Only do this once Phase 2 is comfortable and TDPdf has a non-trivial
user base that justifies the cost (roughly 3–4× Phase 2).

### Steps (Phase 3)

1. EV business validation by Certum — stricter than OV (operational
   existence, physical address site visit or equivalent, principals
   verification). Plan ~2–4 weeks.
2. EV requires hardware (cryptoUSB or HSM). simplySign cloud also works.
3. Re-sign next release with EV cert. SmartScreen reputation is instant.
4. Keep OV cert active for the term as a backup signing path.

---

## Parallel distribution paths (bypass SmartScreen)

These deliver TDPdf without users seeing any SmartScreen friction,
regardless of cert phase:

- **winget**: submit manifest to `microsoft/winget-pkgs` after Phase 1.
  `winget install TDPdf` bypasses SmartScreen because winget is the
  trusted host.
- **Microsoft Store**: requires reserving the "TDPdf" name in Partner
  Center, MSIX packaging (see roadmap items in repo), and Store cert
  signed by Microsoft (Store packaging cert, not the Certum cert).
  Store-installed apps bypass SmartScreen entirely.

---

## Defender / SmartScreen recourse

If a release triggers Defender false-positives despite a valid signature:

1. Submit the binary at <https://www.microsoft.com/wdsi/filesubmission>
   (logged in with the Microsoft account that owns the publisher
   relationship).
2. Choose "Software developer" track. Reference the cert thumbprint.
3. Microsoft typically clears clean files within 24–72 hours.

For SmartScreen "Unknown publisher" specifically, no submission portal
exists — only reputation accrual fixes it, except via the winget /
Store paths above.

---

## S/MIME (email signing) — sidecar item

Certum also issues S/MIME certs. Once Phase 2 is in motion, ask Certum
support to issue an Organization-Validated S/MIME cert against the same
LLC validation (waiver of re-validation typically granted within 398 days).
Use it for signing email from `@thedoodleproject.com`.

S/MIME certs still ship as `.pfx` (no mandatory hardware), unlike code
signing.
