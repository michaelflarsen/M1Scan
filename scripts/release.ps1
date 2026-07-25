<#
.SYNOPSIS
    Laver en ny release af M1Scan uden Claude Code - kør direkte i PowerShell.

.DESCRIPTION
    Gør nøjagtig det samme som .claude/commands/release.md, trin for trin:
    1. Bumper patch-nummeret i M1Scan.csproj
    2. Stopper en evt. kørende M1Scan-instans
    3. Publisher en self-contained single-file .exe
    4. Committer og pusher til origin/main
    5. Opretter en GitHub Release med .exe'en som asset

.PARAMETER Summary
    Kort beskrivelse af hvad der er nyt - bruges i commit-beskeden.
    Bliver du ikke bedt om den, spørger scriptet interaktivt.

.PARAMETER NotesFile
    Sti til en markdown-fil med de fulde release notes (nye features,
    bugfixes, breaking changes). Angives den ikke, åbner scriptet Notepad
    med en tom skabelon du kan udfylde, gemme og lukke for at fortsætte.

.PARAMETER DryRun
    Kør version-bump og publish, men spring commit/push/gh release create
    over. Til at teste at build og version-bump virker uden at ændre noget
    delt (git/GitHub).

.EXAMPLE
    .\scripts\release.ps1 -Summary "Tilføjet CSV-eksport af scanresultater"

.EXAMPLE
    .\scripts\release.ps1 -Summary "Bugfix" -NotesFile .\release-notes.md
#>
param(
    [string]$Summary,
    [string]$NotesFile,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# Repo-roden er altid én mappe over scripts/, uanset hvorfra scriptet køres fra.
$RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot "..")
$CsprojPath = Join-Path $RepoRoot "M1Scan.csproj"
$RepoOwner  = "michaelflarsen"
$RepoName   = "M1Scan"

function Fail($msg) {
    Write-Host ""
    Write-Host "FEJL: $msg" -ForegroundColor Red
    exit 1
}

Write-Host "== M1Scan release-script ==" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# Trin 1 - Bump version
# ---------------------------------------------------------------------------
$csprojContent = Get-Content -Path $CsprojPath -Raw
if ($csprojContent -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
    Fail "Kunne ikke finde <Version>X.Y.Z</Version> i $CsprojPath"
}
$oldVersion = $Matches[0] -replace '</?Version>', ''
$major, $minor, $patch = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
$patch++
$newVersion = "$major.$minor.$patch"

$csprojContent = $csprojContent -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$newVersion</Version>"
Set-Content -Path $CsprojPath -Value $csprojContent -NoNewline
Write-Host "Version: $oldVersion -> $newVersion" -ForegroundColor Green

# README-badgen blev tidligere opdateret i hånden og var derfor bagud (stod 1.3.36
# mens csproj var på 1.3.49). Den udledes nu af csproj-versionen, så den ikke kan drifte.
$readmePath = Join-Path $RepoRoot "README.md"
if (Test-Path $readmePath) {
    $readme = Get-Content -Path $readmePath -Raw
    $updated = $readme -replace 'badge/version-\d+\.\d+\.\d+-blue', "badge/version-$newVersion-blue"
    if ($updated -ne $readme) {
        Set-Content -Path $readmePath -Value $updated -NoNewline
        Write-Host "README-badge opdateret til $newVersion" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# Trin 2 - Stop kørende instans (frigør .exe-filen så publish kan overskrive den)
# ---------------------------------------------------------------------------
Stop-Process -Name "M1Scan" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# ---------------------------------------------------------------------------
# Trin 3 - Publish self-contained single-file exe
# ---------------------------------------------------------------------------
Write-Host "Publisher (dette tager typisk 20-60s)..." -ForegroundColor Cyan
& dotnet publish $CsprojPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish fejlede (exit code $LASTEXITCODE). Version i csproj er bumpet til $newVersion, men intet er committet - ret evt. fejlen og kør scriptet igen, eller sæt versionen tilbage manuelt."
}

$exePath = Join-Path $RepoRoot "bin\Release\net8.0-windows\win-x64\publish\M1Scan.exe"
if (-not (Test-Path $exePath)) {
    Fail "Fandt ikke den publicerede exe på forventet sti: $exePath"
}
$exeSizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "Publish OK: $exePath ($exeSizeMb MB)" -ForegroundColor Green

if ($DryRun) {
    Write-Host ""
    Write-Host "DryRun: springer commit/push/gh release create over." -ForegroundColor Yellow
    Write-Host "Husk at $CsprojPath nu står med version $newVersion - sæt den tilbage manuelt hvis du ikke vil beholde bumpet." -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------------------
# Trin 4 - Commit og push
# ---------------------------------------------------------------------------
if (-not $Summary) {
    $Summary = Read-Host "Kort beskrivelse af hvad der er nyt i v$newVersion (bruges i commit-beskeden)"
}
if (-not $Summary) {
    Fail "En beskrivelse er påkrævet for commit-beskeden."
}

Push-Location $RepoRoot
try {
    git add -u
    Write-Host ""
    Write-Host "Staged filer:" -ForegroundColor Cyan
    git status --short

    $commitMessage = "v${newVersion}: $Summary"
    git commit -m $commitMessage
    if ($LASTEXITCODE -ne 0) { Fail "git commit fejlede." }

    git push origin main
    if ($LASTEXITCODE -ne 0) { Fail "git push fejlede. Commit'et er lavet lokalt - tjek 'git status' og push manuelt når problemet er løst." }
}
finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# Trin 5 - Opret GitHub Release med binær
# ---------------------------------------------------------------------------
if (-not $NotesFile) {
    $tmpNotes = Join-Path ([System.IO.Path]::GetTempPath()) "M1Scan-release-notes-v$newVersion.md"
    @"
## Nyt i denne version

$Summary

### Nye features
-

### Bugfixes
-

### Breaking changes
Ingen.
"@ | Set-Content -Path $tmpNotes -Encoding utf8

    Write-Host ""
    Write-Host "Åbner Notepad med release notes - udfyld, gem og luk Notepad for at fortsætte." -ForegroundColor Cyan
    Start-Process -FilePath "notepad.exe" -ArgumentList $tmpNotes -Wait
    $NotesFile = $tmpNotes
}

if (-not (Test-Path $NotesFile)) {
    Fail "Release notes-filen findes ikke: $NotesFile"
}

# ---------------------------------------------------------------------------
# SHA-256 tilføjes automatisk til release-noterne.
#
# Appens indbyggede opdatering NÆGTER at installere en release hvis noterne ikke
# indeholder en "SHA256:"-linje, og hvis den hentede exe ikke matcher hashen
# (se Services/UpdateService.cs). Uden hashen ser brugerne aldrig "Update now" —
# og de får ingen fejlbesked, opdateringen bliver bare aldrig tilbudt.
#
# Derfor beregnes og tilføjes den her i stedet for at bede mennesket om at huske
# det i en Notepad-dialog. Et krav der kan glemmes i stilhed er ikke et krav.
# ---------------------------------------------------------------------------
$sha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLower()

$notesContent = Get-Content -Path $NotesFile -Raw
if ($notesContent -match 'SHA-?256\s*[:=]\s*([0-9a-fA-F]{64})') {
    # Allerede en hash i noterne (fx en håndskrevet fra et tidligere forsøg) —
    # kontrollér at den passer til den exe vi rent faktisk uploader.
    if ($Matches[1].ToLower() -ne $sha256) {
        Fail "Release-noterne indeholder en SHA-256 der IKKE matcher den byggede exe.`n  I noterne: $($Matches[1].ToLower())`n  Faktisk:   $sha256`nRet eller fjern linjen og kør igen."
    }
    Write-Host "SHA-256 i noterne matcher den byggede exe." -ForegroundColor Green
}
else {
    $notesContent = $notesContent.TrimEnd() + "`n`n---`nSHA256: $sha256`n"
    Set-Content -Path $NotesFile -Value $notesContent -Encoding utf8 -NoNewline
    Write-Host "SHA-256 tilføjet til release-noterne: $sha256" -ForegroundColor Green
}

# --repo angives eksplicit, så gh ikke gætter forkert repo ud fra den mappe
# scriptet tilfældigvis køres fra.
& gh release create "v$newVersion" "${exePath}#M1Scan.exe" `
    --repo "$RepoOwner/$RepoName" `
    --title "M1Scan v$newVersion" `
    --notes-file "$NotesFile"
if ($LASTEXITCODE -ne 0) {
    Fail "gh release create fejlede. Commit/push er allerede gennemført - opret releasen manuelt med:`n  gh release create v$newVersion `"$exePath#M1Scan.exe`" --repo $RepoOwner/$RepoName --title `"M1Scan v$newVersion`" --notes-file `"$NotesFile`""
}

Write-Host ""
Write-Host "== Release v$newVersion fuldført ==" -ForegroundColor Green
Write-Host "https://github.com/$RepoOwner/$RepoName/releases/tag/v$newVersion"
