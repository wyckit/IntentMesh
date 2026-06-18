# IntentMesh build script. Authors + compiles + verifies the agentic TLM bundle, builds the
# solution, and runs the test suite. ASCII-only (PassGen convention).
# Exit any running IntentMesh.Web / IntentMesh.Cli before running (a live process locks the DLLs).

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "==> Authoring + compiling the im-* TLM bundle"
dotnet run --project src/IntentMesh.Tlm.Cli -- author      --root dataset
dotnet run --project src/IntentMesh.Tlm.Cli -- compile all --root dataset
dotnet run --project src/IntentMesh.Tlm.Cli -- verify      --root dataset

Write-Host "==> Building the solution"
dotnet build IntentMesh.slnx -v q --nologo

Write-Host "==> Running tests"
dotnet test tests/IntentMesh.Tests/IntentMesh.Tests.csproj --nologo

Write-Host ""
Write-Host "Done. Try:"
Write-Host "  dotnet run --project src/IntentMesh.Cli              # all three demo traces"
Write-Host "  dotnet run --project src/IntentMesh.Cli -- --demo 3  # the injection defense"
Write-Host "  dotnet run --project src/IntentMesh.Web              # the Control Room"
