param()

$ErrorActionPreference = 'Stop'
Write-Host "[dev] Running M1Scan..."

dotnet run --project .\M1Scan.csproj
