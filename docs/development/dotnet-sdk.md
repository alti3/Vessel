# .NET SDK Status

Last checked: 2026-05-17

The repository is configured for .NET 11 preview because the user explicitly approved beginning work against the preview SDK before stable GA.

The configured SDK in `global.json` is:

- `11.0.100-preview.4.26230.115`

This should be replaced with the stable .NET 11 SDK after official GA.

## Local SDK Visibility

The local development environment does not currently expose .NET 11 through `dotnet --info`.

Installed SDKs reported by `dotnet --info`:

- `10.0.204`
- `10.0.300`

Installed runtimes reported by `dotnet --info`:

- `Microsoft.AspNetCore.App 10.0.8`
- `Microsoft.NETCore.App 10.0.8`
- `Microsoft.WindowsDesktop.App 10.0.8`

Restore, build, and test are blocked locally until the .NET 11 SDK is visible to the `dotnet` executable used by this repository. Do not target .NET 10 as a substitute.
