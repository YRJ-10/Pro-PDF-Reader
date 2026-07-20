# Release Checklist

1. Confirm `git status --short` is clean.
2. Run `dotnet build .\ProPdfReader\ProPdfReader.csproj -c Release`.
3. Run `dotnet format .\ProPdfReader\ProPdfReader.csproj --verify-no-changes --no-restore`.
4. Build artifacts with `powershell -ExecutionPolicy Bypass -File .\build\Publish.ps1 -Version 1.0.0`.
5. Verify artifacts with `powershell -ExecutionPolicy Bypass -File .\build\Verify-Release.ps1 -Version 1.0.0`.
6. Smoke-test a normal, password-protected, and intentionally damaged PDF.
7. Install and uninstall the setup build; verify Open with registration is added and removed.
8. For a public release, code-sign the published executable before creating the final ZIP and installer, then sign the installer.
9. Recreate `SHA256SUMS.txt`, rerun the verifier, then create and push the version tag.
