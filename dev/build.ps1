param()

$ErrorActionPreference = 'Stop'
Write-Host "[dev] Starting build steps..."

Write-Host "[dev] .NET SDK info:"
dotnet --info

Write-Host "[dev] Restoring packages..."
dotnet restore

Write-Host "[dev] Building solution (Debug)..."
dotnet build .\M1Scan.sln -c Debug

Write-Host "[dev] Build finished."
