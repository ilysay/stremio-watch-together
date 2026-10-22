# Builds StremioWT.exe (needs the .NET SDK) and runs the tests.
Set-Location (Split-Path $PSScriptRoot)
dotnet test tests\StremioWT.Tests --nologo -v q
if (-not $?) { exit 1 }
dotnet build src\StremioWT.csproj -c Release -o build --nologo -v q
if ($?) { Copy-Item build\StremioWT.exe StremioWT.exe -Force; Write-Host "OK: StremioWT.exe" }