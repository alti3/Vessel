# Phase 6 UI Foundation

Phase 6 selects Tailwind CSS v4 with Bun as the frontend asset toolchain.

```powershell
bun install
bun run css:build
```

The source stylesheet is `src/Vessel.Web/Styles/app.css`; the generated static asset is `src/Vessel.Web/wwwroot/css/app.css`.

The Blazor shell follows Coolify's operational layout concepts from upstream commit `49656aa`: authenticated sidebar navigation, dashboard-first entry, team context, resource entry points for projects and servers, deployment visibility, notifications, profile, and settings. Vessel keeps those behaviors in Blazor and application-facing query services rather than Livewire/Laravel structures.

Dashboard design choices:

- Dense operational navigation instead of a marketing landing page.
- Safe aggregate status cards with fast drill-down links.
- Tables for log/deployment-style data and cards only for repeated resource entries.
- Secrets remain masked by default through `SecretInput`.
- Reusable primitives live under `src/Vessel.Web/Components/Shared`.
