# Phase 6 UI Foundation

Phase 6 selects Tailwind CSS v4 with Bun as the frontend asset toolchain.

```powershell
bun install
bun run css:build
```

The source stylesheet is `src/Vessel.Web/Styles/app.css`; the generated static asset is `src/Vessel.Web/wwwroot/css/app.css`.

The Blazor shell follows Coolify's operational layout concepts from upstream commit `49656aa`: dark-first authenticated sidebar navigation, command-center affordance, team context, resource entry points for projects and servers, source/destination/security/storage navigation, deployment visibility, notifications, profile, and settings. Vessel keeps those behaviors in Blazor and application-facing query services rather than Livewire/Laravel structures.

Dashboard design choices:

- Dense operational navigation instead of a marketing landing page.
- Coolify-style dashboard sections for Projects and Servers, with flat resource cards and compact add actions.
- System signals are retained as a secondary operational panel rather than dominating the first screen.
- Tables for log/deployment-style data and cards only for repeated resource entries.
- Secrets remain masked by default through `SecretInput`.
- Reusable primitives live under `src/Vessel.Web/Components/Shared`.
