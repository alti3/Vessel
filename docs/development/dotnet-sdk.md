# .NET SDK Status

Last checked: 2026-05-18

The repository is configured for .NET 11 preview because the user explicitly approved beginning work against the preview SDK before stable GA.

The configured SDK in `global.json` is:

- `11.0.100-preview.4.26230.115`

This should be replaced with the stable .NET 11 SDK after official GA.

## Local SDK Visibility

The local development environment now exposes .NET 11 through `dotnet --info`.

Installed SDKs reported by `dotnet --info`:

- `10.0.204`
- `10.0.300`
- `11.0.100-preview.4.26230.115`

Installed runtimes reported by `dotnet --info`:

- `Microsoft.AspNetCore.App 10.0.8`
- `Microsoft.AspNetCore.App 11.0.0-preview.4.26230.115`
- `Microsoft.NETCore.App 10.0.8`
- `Microsoft.NETCore.App 11.0.0-preview.4.26230.115`
- `Microsoft.WindowsDesktop.App 10.0.8`
- `Microsoft.WindowsDesktop.App 11.0.0-preview.4.26230.115`

Local verification now succeeds with the configured preview SDK:

- `dotnet restore Vessel.slnx`
- `dotnet build Vessel.slnx --no-restore`
- `dotnet test Vessel.slnx --no-build`
- `dotnet format Vessel.slnx --verify-no-changes --no-restore`
- `tools/validate-project-references.ps1`
