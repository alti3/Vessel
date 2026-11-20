# .NET SDK Status

Last checked: 2026-11-20

.NET 11 reached general availability on 2026-11-10 (runtime `11.0.0`, SDK `11.0.100`, STS support phase). The repository now targets the stable release:

- `global.json` pins SDK `11.0.100` with `rollForward: latestFeature` and `allowPrerelease: false`.
- `Directory.Build.props` uses `LangVersion` `latest` (no preview language features).
- All `Microsoft.*`, EF Core, and Npgsql packages are on their `11.0.0` stable releases.

## Preview-to-GA Migration Notes

- EF Core 11 previews generated a `LatestMigrationId` override in `VesselDbContextModelSnapshot`. The GA `ModelSnapshot` type no longer exposes that member, so the generated override was removed. New migrations regenerate the snapshot with the GA tooling.
- `SSH.NET` moved from `2025.1.0` to `2026.0.1` because the older version has two published high-severity advisories (`GHSA-mggc-4xg6-vcxf`, `GHSA-q939-rpr3-3284`) reported by `dotnet restore` (`NU1903`).

## Local Verification

The SDK can be installed per-user without root:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --version 11.0.100 --install-dir "$HOME/.dotnet"
```

Verification commands:

- `dotnet restore Vessel.slnx`
- `dotnet build Vessel.slnx --no-restore`
- `dotnet test Vessel.slnx --no-build`
- `dotnet format Vessel.slnx --verify-no-changes --no-restore`
- `tools/validate-project-references.ps1`
