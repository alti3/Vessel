# AGENTS.md

## Project: Vessel (Coolify-to-.NET Rewrite)

This repository is a ground-up C#/.NET rewrite of the Coolify project.

The target runtime is **.NET 11 after its official stable release**. Do not begin production implementation on .NET 11 preview bits unless explicitly instructed. This project assumes that the final .NET 11 SDK, ASP.NET Core 11, EF Core 11, Npgsql versions compatible with .NET 11, and the final .NET 11 Process API surface are available.

The architecture is a **modular monolith first**, with clean internal boundaries so that selected runtime processes can be split out later if required.

The primary production distribution is a **host-native, self-contained Vessel executable** managed by the host OS. Containerized deployment is supported as a secondary compatibility mode, not the default control-plane architecture.

---

# 1. Core Decision

## 1.1 Technology Stack

Use the following stack:

```text
.NET 11
ASP.NET Core 11
Blazor Web App
Razor components
Controllers
SignalR
Hangfire
EF Core
Npgsql
PostgreSQL
Redis
Docker
Podman-compatible runtime abstraction later
SSH
Git
S3-compatible object storage
OpenTelemetry
Serilog
```

The app is initially one deployable product, not a microservices system.

The default production install shape is:

```text
Host OS
├─ vesseld / vessel server
│  ├─ API
│  ├─ Blazor Web UI
│  ├─ SignalR
│  ├─ Hangfire scheduler/workers
│  ├─ deployment orchestration
│  ├─ reverse proxy management
│  └─ runtime adapters
├─ Docker / Podman / future OCI runtime
└─ application containers
```

Do not make the Vessel control plane depend on running inside Docker. The orchestrator should normally live outside the containers it creates and manages.

The first source tree shape is:

```text
CoolifyDotnet/
├─ src/
│  ├─ Vessel.Web
│  ├─ Vessel.Application
│  ├─ Vessel.Domain
│  ├─ Vessel.Infrastructure
│  └─ Vessel.Shared
├─ tests/
│  ├─ Vessel.UnitTests
│  ├─ Vessel.IntegrationTests
│  └─ Vessel.E2ETests
└─ deploy/
```

Future optional deployables may be added later:

```text
Vessel.Worker
Vessel.Agent
Vessel.Gateway
Vessel.Notifications
```

Do **not** start with separate services unless explicitly instructed. Start as a modular monolith.

## 1.2 Distribution Decision

Vessel should be designed executable-first:

```text
single self-contained binary
Linux x64 and Linux arm64 release artifacts
systemd service by default on Linux
install/update script for common host installs
optional Docker Compose deployment for demos, development, and compatibility
```

This means production design should optimize for:

- Running as a host-native daemon.
- Managing Docker/Podman as external host runtimes.
- Integrating cleanly with systemd and journald.
- Atomic self-update and rollback.
- Clear filesystem ownership and predictable host paths.
- Simple debugging from the host without container-in-container assumptions.

The Dockerized control-plane mode is allowed, but it must not become the only supported or architecturally privileged path.

---

# 2. Runtime Target

## 2.1 .NET Version

Target:

```xml
<TargetFramework>net11.0</TargetFramework>
```

Use .NET 11 only after stable GA.

Before using any .NET 11-specific API, agents must verify that the API exists in the stable SDK being used by the repository. If an API existed in preview but changed before GA, follow the stable SDK, not preview blog syntax.

## 2.2 STS/LTS Reality

.NET 11 is expected to be a Standard Term Support release. The project accepts using .NET 11 because the user explicitly wants to start from .NET 11 after the official stable release.

Agents must not downgrade the project to .NET 10 unless explicitly instructed.

## 2.3 Upgrade Policy

The repository should keep a `global.json`:

```json
{
  "sdk": {
    "version": "11.0.100",
    "rollForward": "latestFeature"
  }
}
```

Update the exact SDK version after .NET 11 GA.

## 2.4 Publish Targets

Release engineering should support self-contained publishing for at least:

```text
linux-x64
linux-arm64
```

Native AOT, single-file publishing, and trimming may be used where they are compatible with ASP.NET Core, Blazor Server, EF Core, reflection needs, diagnostics, and operational support. Do not enable aggressive trimming or AOT in a way that breaks runtime behavior, migrations, serialization, configuration binding, SignalR, Hangfire, or provider loading.

## 2.5 Installation and Service Model

The preferred install model is:

```text
curl -fsSL https://example.com/install.sh | sh
vessel server
```

Production Linux installs should create:

```text
/usr/local/bin/vessel
/etc/vessel/
/var/lib/vessel/
/var/log/vessel/ when not using journald only
vessel.service
```

The installer must be deterministic and auditable. It should:

- Detect CPU architecture and OS.
- Download a versioned artifact.
- Verify checksum and, when available, signature.
- Install with restrictive permissions.
- Create or update the systemd unit.
- Avoid overwriting user configuration.
- Support non-interactive execution for automation.

Reference install scripts worth studying:

```text
Astral uv Bash installer:
https://releases.astral.sh/installers/uv/latest/uv-installer.sh

Bun Bash installer for Linux and macOS:
https://bun.sh/install

Bun PowerShell installer for Windows:
https://bun.sh/install.ps1
```

## 2.6 Self-Update Model

Vessel should support an explicit self-update command:

```text
vessel self-update
```

Self-update must:

- Download a versioned binary.
- Verify checksum and, when available, signature.
- Place the new binary beside the current installation.
- Swap atomically through a symlink or equivalent platform-safe mechanism.
- Restart the service through the service manager.
- Run a post-update health check.
- Roll back if startup or health checks fail.
- Leave audit metadata for the update attempt.

Do not implement self-update as an unmanaged detached process. It must have a clear lifecycle, safe standard handles, failure reporting, and rollback behavior.

---

# 3. Architectural Style

## 3.1 Modular Monolith

This is a modular monolith:

```text
one main product
one primary database
one source tree
one deployment by default
multiple internal modules
strict boundaries
```

Do not design it as a distributed system from day one.

Do design it so the worker, agent, gateway, and notification components can be extracted later.

## 3.2 Clean Architecture Direction

Dependency flow:

```text
Vessel.Web
    ↓
Vessel.Application
    ↓
Vessel.Domain

Vessel.Infrastructure
    implements interfaces owned by Application/Domain
```

Allowed references:

```text
Vessel.Web -> Vessel.Application
Vessel.Web -> Vessel.Infrastructure
Vessel.Web -> Vessel.Shared

Vessel.Application -> Vessel.Domain
Vessel.Application -> Vessel.Shared

Vessel.Infrastructure -> Vessel.Application
Vessel.Infrastructure -> Vessel.Domain
Vessel.Infrastructure -> Vessel.Shared

Vessel.Domain -> no project dependencies except approved base abstractions
Vessel.Shared -> minimal dependencies only
```

Forbidden:

```text
Vessel.Domain -> Vessel.Infrastructure
Vessel.Domain -> Vessel.Web
Vessel.Application -> Vessel.Web
Vessel.Infrastructure -> Vessel.Web
```

## 3.3 Feature-First Organization

Prefer feature-first folders over technical-only folders.

Good:

```text
Deployments/
├─ StartDeployment/
├─ CancelDeployment/
├─ DeploymentLogs/
├─ DeploymentStatus/
└─ RunDeployment/
```

Avoid dumping everything into:

```text
Services/
Dtos/
Managers/
Helpers/
Utils/
```

Technical folders are acceptable inside feature folders.

---

# 4. Solution Structure

## 4.1 Top-Level Layout

```text
CoolifyDotnet/
├─ AGENTS.md
├─ README.md
├─ Directory.Build.props
├─ Directory.Packages.props
├─ global.json
├─ CoolifyDotnet.slnx
│
├─ src/
│  ├─ Vessel.Web/
│  ├─ Vessel.Application/
│  ├─ Vessel.Domain/
│  ├─ Vessel.Infrastructure/
│  └─ Vessel.Shared/
│
├─ tests/
│  ├─ Vessel.UnitTests/
│  ├─ Vessel.IntegrationTests/
│  └─ Vessel.E2ETests/
│
├─ deploy/
│  ├─ docker-compose.yml
│  ├─ docker-compose.dev.yml
│  ├─ docker-compose.prod.yml
│  ├─ systemd/
│  ├─ install/
│  ├─ nginx/
│  ├─ caddy/
│  └─ scripts/
│
├─ docs/
│  ├─ architecture/
│  ├─ decisions/
│  ├─ deployment/
│  ├─ security/
│  └─ operations/
│
└─ tools/
```

## 4.2 Vessel.Web

Purpose:

```text
HTTP host
Blazor Web App
Controllers
SignalR hubs
Hangfire dashboard
Authentication wiring
Authorization policies
Middleware
Endpoint mapping
Static assets
```

Suggested layout:

```text
src/Vessel.Web/
├─ Program.cs
├─ appsettings.json
├─ appsettings.Development.json
│
├─ Components/
│  ├─ App.razor
│  ├─ Routes.razor
│  ├─ Layout/
│  ├─ Pages/
│  └─ Shared/
│
├─ Features/
│  ├─ Dashboard/
│  ├─ Projects/
│  ├─ Servers/
│  ├─ Applications/
│  ├─ Databases/
│  ├─ Deployments/
│  ├─ Logs/
│  ├─ Terminals/
│  ├─ EnvironmentVariables/
│  ├─ Teams/
│  ├─ Auth/
│  ├─ Notifications/
│  └─ Settings/
│
├─ Controllers/
│  ├─ Api/
│  └─ Webhooks/
│
├─ Hubs/
│  ├─ DeploymentLogHub.cs
│  ├─ TerminalHub.cs
│  ├─ ServerStatusHub.cs
│  └─ NotificationHub.cs
│
├─ Middleware/
├─ Filters/
├─ Authorization/
├─ HealthChecks/
└─ Extensions/
```

Rules:

- Blazor components must not contain deployment orchestration.
- Controllers must not contain deployment orchestration.
- SignalR hubs must not contain deployment orchestration.
- Web layer calls Application layer.
- Web layer may map DTOs to UI models.
- Web layer may validate HTTP-level concerns.
- Web layer must not call Docker, Git, SSH, or Process APIs directly.

## 4.3 Vessel.Application

Purpose:

```text
Use cases
Commands
Queries
Application services
Orchestration
Transaction boundaries
Background job entry points
Interfaces for infrastructure services
```

Suggested layout:

```text
src/Vessel.Application/
├─ Abstractions/
│  ├─ Persistence/
│  ├─ Docker/
│  ├─ Git/
│  ├─ Ssh/
│  ├─ Processes/
│  ├─ Realtime/
│  ├─ Storage/
│  ├─ Notifications/
│  ├─ Jobs/
│  └─ Security/
│
├─ Projects/
├─ Servers/
├─ Applications/
├─ Databases/
├─ Deployments/
├─ Logs/
├─ Terminals/
├─ Teams/
├─ Auth/
├─ Notifications/
├─ Settings/
└─ Common/
```

Application layer owns interfaces such as:

```csharp
public interface IProcessRunner { }
public interface IDockerService { }
public interface ISshClient { }
public interface IGitService { }
public interface IDeploymentRunner { }
public interface IDeploymentLogWriter { }
public interface IRealtimeNotifier { }
public interface IBackgroundJobClient { }
public interface ICurrentUser { }
public interface IClock { }
```

## 4.4 Vessel.Domain

Purpose:

```text
Domain entities
Value objects
Domain events
Business invariants
Pure rules
Enums
Strongly typed IDs
```

Suggested layout:

```text
src/Vessel.Domain/
├─ Projects/
├─ Servers/
├─ Applications/
├─ Databases/
├─ Deployments/
├─ Resources/
├─ Teams/
├─ Users/
├─ Notifications/
└─ Common/
```

Domain rules:

- No EF Core attributes unless approved.
- No infrastructure dependencies.
- No HTTP concepts.
- No Hangfire concepts.
- No SignalR concepts.
- No Docker SDK references.
- No SSH library references.
- No direct time access through `DateTime.UtcNow`; use abstractions in Application.
- Prefer value objects for IDs, names, ports, domains, image tags, versions, and server addresses.

## 4.5 Vessel.Infrastructure

Purpose:

```text
EF Core
Npgsql
PostgreSQL
Redis
Docker implementation
SSH implementation
Git implementation
Process execution
Storage providers
Email providers
Notification providers
Hangfire implementation
SignalR notifier implementation
External integrations
```

Suggested layout:

```text
src/Vessel.Infrastructure/
├─ Persistence/
│  ├─ CoolifyDbContext.cs
│  ├─ Configurations/
│  ├─ Migrations/
│  ├─ Repositories/
│  └─ Interceptors/
│
├─ Processes/
│  ├─ DotNetProcessRunner.cs
│  ├─ ProcessCommand.cs
│  ├─ ProcessResult.cs
│  ├─ ProcessBinaryResult.cs
│  ├─ ProcessExitInfo.cs
│  ├─ ProcessOutputLine.cs
│  ├─ ProcessStreamPolicy.cs
│  └─ ProcessTerminationPolicy.cs
│
├─ Docker/
├─ Ssh/
├─ Git/
├─ Redis/
├─ Hangfire/
├─ Realtime/
├─ Storage/
├─ Notifications/
├─ Security/
├─ External/
└─ Extensions/
```

## 4.6 Vessel.Shared

Purpose:

```text
DTOs
Contracts
Public API models
Small shared primitives
Serialization-safe models
```

Rules:

- Keep this project small.
- Do not place business logic here.
- Do not create a dumping ground.
- Avoid referencing heavy packages.

---

# 5. UI Architecture

## 5.1 Primary UI Choice

Use:

```text
Blazor Web App
Interactive Server-first
Static SSR where possible
Optional WebAssembly islands later
Optional Web Workers for isolated browser CPU work
```

Do not use MVC Views as the main dashboard.

Do not use Razor Pages as the main dashboard.

Controllers are for API/webhooks, not primary UI rendering.

## 5.2 Render Mode Guidance

Default:

```text
Interactive Server
```

Use Static SSR for:

```text
public landing pages
documentation pages
legal pages
simple unauthenticated pages
non-interactive status pages
```

Use Interactive Server for:

```text
dashboard
forms
deployments
logs
terminal
settings
server management
project management
resource management
```

Use WebAssembly/Web Workers only for isolated browser-side computation:

```text
large log filtering
YAML/JSON validation
diff viewer
local config generation
client-side search indexes
visual graph layout
```

Do not run deployments, Docker operations, SSH, or secrets processing in the browser.

## 5.3 Component Rules

Blazor components should:

- Render UI.
- Hold short-lived UI state.
- Call Application-facing services or API clients.
- Subscribe to SignalR where appropriate.
- Dispose subscriptions correctly.
- Avoid long-running work.

Blazor components should not:

- Run shell commands.
- Talk directly to Docker.
- Talk directly to SSH.
- Talk directly to EF DbContext.
- Enqueue Hangfire jobs directly unless wrapped through an Application service.
- Hold secrets longer than necessary.
- Contain business rules.

## 5.4 UI Feature Layout

Example:

```text
src/Vessel.Web/Features/Deployments/
├─ Pages/
│  ├─ DeploymentListPage.razor
│  └─ DeploymentDetailsPage.razor
├─ Components/
│  ├─ DeploymentStatusBadge.razor
│  ├─ DeploymentTimeline.razor
│  ├─ DeploymentLogViewer.razor
│  └─ StartDeploymentButton.razor
├─ Models/
│  └─ DeploymentViewModel.cs
└─ Services/
   └─ DeploymentUiService.cs
```

## 5.5 Styling

Use a consistent design system.

Recommended:

```text
Tailwind CSS or a mature Blazor component library
CSS isolation where useful
Razor components for reusable primitives
```

Do not create one-off styling for every page.

Use reusable components:

```text
StatusBadge
DangerButton
ConfirmDialog
ResourceCard
PageHeader
EmptyState
LogViewer
TerminalPanel
KeyValueEditor
EnvironmentVariableEditor
SecretInput
```

---

# 6. API Architecture

## 6.1 Controllers

Use Controllers for:

```text
REST API
webhooks
GitHub/GitLab callbacks
public API
CLI API
integration endpoints
file upload/download
```

Use versioned routes:

```text
/api/v1/projects
/api/v1/servers
/api/v1/applications
/api/v1/deployments
/api/v1/databases
/api/v1/notifications
/api/v1/settings
```

Webhook routes:

```text
/webhooks/github
/webhooks/gitlab
/webhooks/gitea
/webhooks/bitbucket
```

## 6.2 Controller Rules

Controllers must:

- Be thin.
- Validate HTTP-specific input.
- Call Application commands/queries.
- Return typed responses.
- Never contain business orchestration.
- Never call EF DbContext directly.
- Never call Docker/Git/SSH/Process directly.

Good pattern:

```csharp
[ApiController]
[Route("api/v1/deployments")]
public sealed class DeploymentsController : ControllerBase
{
    [HttpPost("{applicationId:guid}")]
    public async Task<ActionResult<StartDeploymentResponse>> Start(
        Guid applicationId,
        StartDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new StartDeploymentCommand(applicationId, request.ForceRebuild),
            cancellationToken);

        return Accepted(result);
    }
}
```

## 6.3 Minimal APIs

Minimal APIs may be used for:

```text
health checks
small internal endpoints
simple read-only endpoints
metrics endpoints
```

Controllers are preferred for larger public API surfaces.

---

# 7. Realtime Architecture

## 7.1 SignalR

Use SignalR for:

```text
deployment logs
terminal output
deployment status
server status
resource events
notifications
background job progress
```

SignalR is a transport, not the source of truth.

Source of truth:

```text
PostgreSQL
Redis when appropriate for ephemeral state
```

## 7.2 Hub Rules

Hubs should:

- Authenticate user.
- Authorize resource access.
- Join/leave groups.
- Stream messages from Application services.
- Forward user input to Application services for terminal sessions.

Hubs should not:

- Execute shell commands directly.
- Read/write DbContext directly.
- Contain deployment logic.
- Contain terminal process logic.
- Contain Docker/SSH implementation.

## 7.3 Group Naming

Use deterministic group names:

```text
tenant:{tenantId}
project:{projectId}
server:{serverId}
application:{applicationId}
deployment:{deploymentId}
terminal:{terminalSessionId}
user:{userId}
```

## 7.4 SignalR Scale-Out

Initial monolith can run without scale-out.

Add Redis backplane when running multiple web instances.

Use sticky sessions when required by the selected hosting mode.

---

# 8. Background Jobs

## 8.1 Hangfire

Use Hangfire for:

```text
deployments
scheduled health checks
server polling
database backups
cleanup jobs
certificate renewals
resource monitoring
notification dispatch
webhook processing
long-running Git/Docker workflows
```

Use PostgreSQL storage for Hangfire unless there is a strong reason not to.

## 8.2 Job Rules

Hangfire job classes must be thin.

Good:

```csharp
public sealed class RunDeploymentJob
{
    private readonly IDeploymentRunner _runner;

    public RunDeploymentJob(IDeploymentRunner runner)
    {
        _runner = runner;
    }

    public Task RunAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        return _runner.RunAsync(deploymentId, cancellationToken);
    }
}
```

Bad:

```csharp
public sealed class RunDeploymentJob
{
    public async Task RunAsync(Guid deploymentId)
    {
        // 700 lines of Docker, Git, SSH, DB, SignalR, retry logic
    }
}
```

## 8.3 Job Idempotency

All jobs must be designed to be safely retried.

Every long-running job must:

- Record state transitions.
- Have a cancellation path.
- Be idempotent where possible.
- Use distributed locks where needed.
- Persist logs incrementally.
- Emit realtime updates.
- Respect timeouts.
- Avoid infinite retries for deterministic failures.

## 8.4 Job States

Deployment jobs should use explicit states:

```text
Queued
Preparing
PullingSource
Building
PublishingImage
CreatingNetwork
StartingContainers
RunningHealthChecks
SwitchingProxy
Completed
Failed
Canceled
TimedOut
RollbackStarted
RolledBack
RollbackFailed
```

Do not represent complex job status as free text.

## 8.5 Future Worker Extraction

Initially Hangfire can run inside `Vessel.Web`.

Design so later:

```text
Vessel.Worker
```

can own job execution.

When split:

```text
Vessel.Web
    UI/API/SignalR

Vessel.Worker
    Hangfire workers
    scheduled jobs
    deployment execution
```

Both reference:

```text
Vessel.Application
Vessel.Infrastructure
Vessel.Domain
```

No business logic should need to move.

---

# 9. Persistence

## 9.1 Database

Use:

```text
PostgreSQL
EF Core
Npgsql
```

PostgreSQL is the source of truth.

## 9.2 EF Core Rules

Use EF Core for:

```text
normal transactional persistence
aggregate persistence
application queries
migrations
schema management
```

Use raw SQL or Dapper only for:

```text
hot paths
large log queries
reporting queries
batch operations
special PostgreSQL features
```

Do not prematurely optimize.

## 9.3 DbContext

Use one primary DbContext initially:

```csharp
public sealed class CoolifyDbContext : DbContext
{
}
```

Use schemas to separate module ownership if useful:

```text
identity
projects
servers
resources
deployments
logs
notifications
settings
```

## 9.4 Migrations

Rules:

- All schema changes require migrations.
- Migrations must be reviewed.
- Do not edit applied production migrations.
- Use explicit indexes.
- Use PostgreSQL constraints where useful.
- Do not rely only on application validation.

## 9.5 Strongly Typed IDs

Prefer strongly typed IDs:

```csharp
public readonly record struct ProjectId(Guid Value);
public readonly record struct ServerId(Guid Value);
public readonly record struct ApplicationId(Guid Value);
public readonly record struct DeploymentId(Guid Value);
```

Ensure EF value converters are configured centrally.

## 9.6 Concurrency

Use optimistic concurrency where appropriate:

```text
resource settings
server records
deployment configuration
environment variable sets
team membership
```

Use PostgreSQL advisory locks or Redis locks for:

```text
only one deployment per application
only one server mutation at a time
exclusive backup/restore
exclusive proxy reload
certificate renewal
```

---

# 10. Process Execution

## 10.1 Critical Rule

Do not call `Process.Start`, `new Process()`, `Process.Run`, `Process.RunAsync`, `Process.RunAndCaptureTextAsync`, `Process.StartAndForget`, or `SafeProcessHandle.Start` directly outside the infrastructure process layer.

All external command execution must go through:

```csharp
IProcessRunner
```

This is mandatory because the project will execute many external tools:

```text
docker
docker compose
git
ssh
scp
rsync
tar
gzip
openssl
bash
sh
systemctl
caddy
nginx
```

The process layer is responsible for security, cancellation, timeout handling, output capture, streaming, redaction, audit metadata, child-process lifetime, and safe standard-handle behavior.

## 10.2 .NET 11 Process API

Use the stable .NET 11 Process API improvements where appropriate.

https://devblogs.microsoft.com/dotnet/process-api-improvements-in-dotnet-11/

Before implementation, check the final stable .NET 11 API names and signatures in the SDK actually used by the repository. If an API existed in preview but changed before GA, follow the stable SDK, not preview blog syntax.

`DotNetProcessRunner` must use the .NET 11 Process APIs as follows when the stable API surface supports them:

```text
Use Process.RunAndCaptureTextAsync for normal text capture.
Use Process.ReadAllLinesAsync for live stdout/stderr streaming.
Use Process.RunAsync for commands where output is intentionally ignored.
Use Process.ReadAllBytesAsync or file-handle redirection for binary output.
Set ProcessStartInfo.InheritedHandles = [] by default.
Use StandardInputHandle, StandardOutputHandle, and StandardErrorHandle for null, file, pipe, and advanced redirection scenarios.
Use File.OpenNullHandle for commands with no stdin or intentionally discarded output.
Use KillOnParentExit by default where supported.
Use ProcessExitStatus information to distinguish exit code, cancellation, timeout, and Unix signal termination.
Use SafeProcessHandle termination APIs where needed for graceful cancel/timeout handling.
Do not use StartDetached or StartAndForget except through an explicitly reviewed, auditable lifecycle policy.
```

Expected useful capabilities include:

```text
high-level process execution APIs
deadlock-safe stdout/stderr capture
async output reading
line-based output handling
binary output handling
standard handle redirection
null standard handles
controlled handle inheritance
KillOnParentExit or equivalent child-process lifetime behavior
StartDetached and StartAndForget restrictions
lighter process handle APIs where needed
process exit status with cancellation/signal details
```

Do not rely blindly on preview blog syntax.

## 10.3 ProcessRunner Contract

Create a stable internal abstraction:

```csharp
public interface IProcessRunner
{
    Task<ProcessResult> RunAndCaptureTextAsync(
        ProcessCommand command,
        CancellationToken cancellationToken);

    Task<ProcessBinaryResult> RunAndCaptureBytesAsync(
        ProcessCommand command,
        CancellationToken cancellationToken);

    Task<ProcessExitInfo> RunAsync(
        ProcessCommand command,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProcessOutputLine> RunStreamingAsync(
        ProcessCommand command,
        CancellationToken cancellationToken);
}
```

Guidance:

- Use `RunAndCaptureTextAsync` for short or moderate text output that is needed after the process exits.
- Use `RunAndCaptureBytesAsync` for binary output only when the output is expected to be small enough to hold safely in memory.
- Use `RunAsync` when output is intentionally ignored.
- Use `RunStreamingAsync` for deployments, Docker builds, Docker Compose operations, Git operations with progress, SSH commands, terminals, and any command whose output may be large or user-visible in realtime.
- Prefer file redirection or object storage for large binary artifacts instead of buffering them in memory.

## 10.4 ProcessCommand

```csharp
public sealed record ProcessCommand
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>();

    public TimeSpan? Timeout { get; init; }

    public ProcessStreamPolicy StandardInput { get; init; }
        = ProcessStreamPolicy.Null;

    public ProcessStreamPolicy StandardOutput { get; init; }
        = ProcessStreamPolicy.Capture;

    public ProcessStreamPolicy StandardError { get; init; }
        = ProcessStreamPolicy.Capture;

    public string? StandardInputFile { get; init; }
    public string? StandardOutputFile { get; init; }
    public string? StandardErrorFile { get; init; }

    public bool RedactSecrets { get; init; } = true;
    public bool KillOnParentExit { get; init; } = true;
    public bool AllowHandleInheritance { get; init; }
    public bool StartDetached { get; init; }

    public ProcessTerminationPolicy TerminationPolicy { get; init; }
        = ProcessTerminationPolicy.GracefulThenKill;
}
```

Rules:

- `FileName` must be an executable name or absolute path chosen by trusted code.
- `Arguments` must be structured arguments. Do not build command strings through untrusted concatenation.
- `WorkingDirectory` must be validated and must not allow path traversal.
- `Environment` values must be redacted from logs and audit metadata.
- `StandardInputFile`, `StandardOutputFile`, and `StandardErrorFile` must only point to safe, owned paths.
- `AllowHandleInheritance` is `false` by default. Infrastructure must translate this to no inherited handles, such as `InheritedHandles = []`, unless an explicit advanced scenario requires otherwise.
- `StartDetached` is `false` by default and requires security review.

## 10.5 ProcessStreamPolicy

```csharp
public enum ProcessStreamPolicy
{
    Null,
    Capture,
    Stream,
    Inherit,
    File
}
```

Rules:

- Use `Null` for unused stdin and intentionally discarded output.
- Use `Capture` only when output is expected to be bounded.
- Use `Stream` for live logs and long-running operations.
- Use `File` for large output, binary artifacts, archives, backups, generated config snapshots, and anything that may exceed safe in-memory limits.
- Use `Inherit` only for explicitly reviewed diagnostics or terminal-like scenarios.

## 10.6 ProcessTerminationPolicy

```csharp
public enum ProcessTerminationPolicy
{
    KillImmediately,
    GracefulThenKill,
    DoNotKill
}
```

Rules:

- Default to `GracefulThenKill`.
- On cancellation or timeout, try graceful termination where supported, wait for a short configured grace period, then force kill.
- Record whether termination was caused by cancellation, timeout, parent exit, explicit kill, or external signal where the platform exposes that detail.
- `DoNotKill` is forbidden for deployment, Docker, Git, SSH, and terminal commands unless explicitly reviewed.

## 10.7 ProcessExitInfo

```csharp
public sealed record ProcessExitInfo
{
    public required int ProcessId { get; init; }
    public required int ExitCode { get; init; }
    public int? TerminatingSignal { get; init; }
    public bool Canceled { get; init; }
    public bool TimedOut { get; init; }
    public required TimeSpan Duration { get; init; }
}
```

`ProcessExitInfo` is the application-facing abstraction over .NET process exit details. Infrastructure may map from .NET 11 `ProcessExitStatus` or lower-level process-handle APIs, but Application code should not depend on preview-only API shapes.

## 10.8 ProcessResult

```csharp
public sealed record ProcessResult
{
    public required ProcessExitInfo Exit { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
}
```

Use this for text output only. Standard output and standard error must be captured separately and redacted before persistence or broadcast.

## 10.9 ProcessBinaryResult

```csharp
public sealed record ProcessBinaryResult
{
    public required ProcessExitInfo Exit { get; init; }
    public required byte[] StandardOutput { get; init; }
    public required byte[] StandardError { get; init; }
}
```

Use this sparingly. Prefer file redirection for large binary data such as archives, compressed backups, certificates, deployment bundles, or exported artifacts.

## 10.10 ProcessOutputLine

```csharp
public enum ProcessOutputKind
{
    StandardOutput,
    StandardError
}

public sealed record ProcessOutputLine
{
    public required int ProcessId { get; init; }
    public required long Sequence { get; init; }
    public required ProcessOutputKind Kind { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

Rules:

- `Sequence` must be monotonic for a single process execution and must preserve the observed stream order as closely as possible.
- `Text` must be redacted before it leaves the infrastructure process layer.
- Deployment-specific metadata, such as deployment ID and step ID, should be attached by the deployment/logging layer rather than the low-level process runner.

## 10.11 StartDetached and StartAndForget Policy

Detached and fire-and-forget processes are dangerous because they can escape Hangfire lifecycle control, deployment cancellation, audit visibility, and cleanup policies.

Forbidden by default:

```text
Process.StartAndForget
ProcessStartInfo.StartDetached = true
DoNotKill termination for unmanaged deployment commands
```

They may only be used when all of the following are true:

```text
the use case is explicitly modeled as a supervisor-style lifecycle
the process owner is recorded
the PID or platform-specific identity is persisted when useful
stdout/stderr/stdin are explicitly set to safe handles or null
cleanup and reconciliation are implemented
security review is completed
audit metadata is written
```

Do not use detached processes for normal deployments, Docker operations, Git operations, SSH commands, proxy reloads, backups, or terminal sessions. Durable work should be owned by Hangfire or a future `Vessel.Worker`, not by detached orphanable processes.

## 10.12 Process Security

Process execution must:

- Avoid shell interpolation unless absolutely required.
- Pass arguments as structured arguments.
- Escape/quote safely only at the final boundary where the operating system requires it.
- Redact secrets from logs, persisted output, realtime output, audit metadata, and exception messages.
- Set timeouts.
- Support cancellation.
- Capture exit codes.
- Capture stdout and stderr separately.
- Distinguish failure, cancellation, timeout, and signal termination where possible.
- Avoid deadlocks by using the .NET 11 simultaneous stdout/stderr reading APIs.
- Avoid orphaned child processes with `KillOnParentExit` where supported.
- Avoid inherited handles by default.
- Set standard handles explicitly to capture, stream, file, inherit, or null.
- Record command metadata for auditing without leaking secrets.
- Avoid buffering unbounded output in memory.
- Prefer streaming or file redirection for large output.

Forbidden:

```csharp
await processRunner.RunAndCaptureTextAsync(new ProcessCommand
{
    FileName = "bash",
    Arguments = ["-c", userProvidedString]
}, cancellationToken);
```

unless the command is intentionally shell-based, validated, reviewed, and the input is not attacker-controlled.

## 10.13 Shell Scripts

Shell scripts are allowed for operational glue, but:

- Keep them versioned.
- Keep them short.
- Keep them deterministic.
- Prefer typed C# orchestration for complex logic.
- Never build shell commands with untrusted string concatenation.
- Prefer passing data through structured arguments, environment variables, files with safe paths, or stdin.
- Capture or stream output through `IProcessRunner`; do not let scripts write secrets to untracked files or inherited handles.

---

# 11. Docker Architecture

## 11.1 Docker Access

Do not call Docker directly from UI, controllers, hubs, or jobs.

Use:

```csharp
IDockerService
IDockerComposeService
IContainerRegistryService
```

Infrastructure implements these using:

```text
Docker CLI
Docker Compose CLI
Docker Engine API
Podman-compatible APIs or CLI where supported later
containerd or nerdctl adapters later if justified
```

Prefer stable runtime APIs when practical:

```text
Docker Engine API
Podman API
containerd gRPC APIs
```

Use CLI execution through `IProcessRunner` strategically for:

```text
docker compose compatibility
build workflows
log/progress streaming
runtime feature gaps
fallback compatibility
operator diagnostics
```

Do not shell out everywhere blindly. The process layer is mandatory for CLI execution, but an API-first runtime adapter design will age better than binding all orchestration to command-line text behavior.

## 11.2 Host-Native Runtime Access

The primary production model is a host-native Vessel daemon controlling a host container runtime.

For Docker on Linux, the local adapter may use:

```text
/var/run/docker.sock
```

This socket belongs to the host Docker daemon. Access to it is effectively host-root-equivalent and must be treated as a privileged capability.

Rules:

- The default install should not require mounting `docker.sock` into a Vessel container.
- Any process with Docker socket access must be considered highly trusted.
- Docker socket access must be documented as a dangerous capability.
- Prefer dedicated deployment hosts for untrusted workloads.
- Support rootless Docker or remote runtime endpoints where practical.
- Runtime access checks should fail clearly when the daemon is unavailable or permissions are insufficient.

## 11.3 Optional Containerized Control Plane

Containerized Vessel deployment is allowed for:

```text
development
quick demos
compatibility installs
cloud templates
operators who explicitly prefer Docker Compose
```

It is not the primary production architecture.

If a containerized control plane mounts the host Docker socket:

```yaml
volumes:
  - /var/run/docker.sock:/var/run/docker.sock
```

the documentation and deployment templates must clearly state that this grants broad control over the host Docker daemon and can be equivalent to root-level host access.

Containerized mode must not introduce alternate business logic paths. It should run the same application and infrastructure abstractions as the host-native daemon.

## 11.4 Docker Rules

All Docker operations must:

- Be logged.
- Be cancellable where possible.
- Have timeouts.
- Capture stdout/stderr.
- Emit progress.
- Redact registry credentials.
- Validate image names.
- Validate container names.
- Avoid unsafe shell interpolation.
- Be idempotent where possible.

## 11.5 Runtime Adapter Boundaries

Vessel should avoid becoming a container runtime.

Do not:

- Reimplement Docker, Podman, containerd, namespaces, or cgroups.
- Manage kernel isolation primitives directly.
- Couple application use cases to one runtime's CLI syntax.

Do:

- Keep runtime-specific code in Infrastructure.
- Expose Application-owned runtime abstractions.
- Model capabilities explicitly when runtimes differ.
- Keep command construction structured and auditable.
- Preserve a path to Docker, Podman, remote Docker endpoints, and future OCI adapters.

## 11.6 Docker Abstractions

Example:

```csharp
public interface IDockerService
{
    Task<ContainerInfo?> GetContainerAsync(
        ServerId serverId,
        string containerName,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProcessOutputLine> ComposeUpAsync(
        ServerId serverId,
        ComposeUpRequest request,
        CancellationToken cancellationToken);

    Task<ProcessResult> ComposeDownAsync(
        ServerId serverId,
        ComposeDownRequest request,
        CancellationToken cancellationToken);
}
```

---

# 12. SSH Architecture

## 12.1 SSH Access

Remote server management must go through:

```csharp
IRemoteCommandRunner
ISshClient
ISshKeyStore
```

Do not scatter SSH logic throughout the app.

## 12.2 SSH Security

SSH keys must:

- Be encrypted at rest.
- Never be logged.
- Be loaded only when needed.
- Be rotated when possible.
- Be scoped to the server/team/resource.
- Be protected by authorization checks.

## 12.3 Remote Execution

Remote commands must use the same safety principles as local process execution:

- Structured commands where possible.
- No unvalidated shell interpolation.
- Timeouts.
- Cancellation.
- Output streaming.
- Secret redaction.
- Exit code capture.
- Audit trail.

---

# 13. Git Architecture

## 13.1 Git Operations

Use:

```csharp
IGitService
```

for:

```text
clone
fetch
checkout
detect branch
read commit hash
submodule update
archive
generate deployment context
```

## 13.2 Git Security

Rules:

- Never log access tokens.
- Never put tokens in command text.
- Redact remote URLs.
- Validate repository URLs.
- Support GitHub, GitLab, Bitbucket, Gitea, and generic Git.
- Prefer short-lived tokens when possible.

---

# 14. Deployment Architecture

## 14.1 Deployment Flow

Canonical deployment flow:

```text
User clicks Deploy
    ↓
Blazor component calls Application service or API
    ↓
Application validates permissions
    ↓
Application creates Deployment record
    ↓
Application enqueues Hangfire job
    ↓
Hangfire invokes deployment runner
    ↓
Deployment runner performs Git/Docker/SSH operations
    ↓
Logs are persisted incrementally
    ↓
Status is persisted incrementally
    ↓
Realtime updates are broadcast with SignalR
    ↓
UI updates live
```

## 14.2 Deployment Runner

Create:

```csharp
public interface IDeploymentRunner
{
    Task RunAsync(DeploymentId deploymentId, CancellationToken cancellationToken);
}
```

Deployment runner must:

- Load deployment configuration.
- Validate target server.
- Acquire deployment lock.
- Prepare workspace.
- Pull source.
- Build or prepare image.
- Generate Docker Compose config.
- Start containers.
- Run health checks.
- Switch proxy routing.
- Persist result.
- Emit logs and events.
- Handle cancellation.
- Handle rollback where possible.

## 14.3 Deployment Locks

Only one deployment should run per application unless explicitly allowed.

Use:

```text
PostgreSQL advisory lock
or Redis distributed lock
```

Lock key pattern:

```text
deployment:application:{applicationId}
```

## 14.4 Deployment Logs

Deployment logs must be:

- Persisted.
- Streamed live.
- Redacted.
- Ordered.
- Timestamped.
- Associated with deployment ID and step ID.
- Queryable after completion.

Do not rely only on SignalR messages.

## 14.5 Rollback

Rollback support should be explicit.

Do not pretend rollback exists if it has not been implemented.

Minimum rollback metadata:

```text
previous image
previous compose file
previous environment version
previous proxy route
previous health status
```

---

# 15. Terminal Architecture

## 15.1 Terminal Sessions

Terminal sessions are high-risk.

Use:

```csharp
ITerminalSessionManager
```

Terminal execution must:

- Authenticate user.
- Authorize server/resource.
- Create auditable session.
- Stream input/output through SignalR.
- Support cancellation/disconnect.
- Enforce idle timeout.
- Enforce max lifetime.
- Optionally record sessions depending on settings.
- Redact secrets where feasible.
- Restrict dangerous usage if policy requires.

## 15.2 Terminal Hubs

SignalR hub handles:

```text
connect
authorize
join session group
send input
receive output
disconnect
resize terminal
terminate session
```

The hub must not implement shell execution directly.

---

# 16. Logging

## 16.1 Application Logging

Use structured logging.

Recommended:

```text
Serilog
OpenTelemetry logs
JSON output in production
human-readable output in development
```

## 16.2 Log Rules

Logs must include:

```text
correlation id
tenant id when available
user id when available
server id when available
application id when available
deployment id when available
job id when available
```

Never log:

```text
passwords
tokens
SSH private keys
database connection strings
full environment variable values
registry credentials
OAuth secrets
webhook secrets
TLS private keys
```

## 16.3 Redaction

Create a central redaction service:

```csharp
public interface ISecretRedactor
{
    string Redact(string input);
}
```

All command output must pass through redaction before persistence or broadcast.

---

# 17. Observability

## 17.1 OpenTelemetry

Use OpenTelemetry for:

```text
traces
metrics
logs correlation
HTTP requests
EF Core
Npgsql
Redis
Hangfire jobs
external process execution
deployment operations
```

## 17.2 Metrics

Expose metrics for:

```text
deployment count
deployment duration
deployment failure rate
active deployments
queue length
job failure rate
server health checks
SignalR connections
terminal sessions
process execution duration
Docker command failures
database query duration
```

## 17.3 Tracing

Create spans for:

```text
StartDeployment
RunDeployment
GitClone
DockerBuild
ComposeUp
HealthCheck
ProxySwitch
SendNotification
```

---

# 18. Authentication and Authorization

## 18.1 Auth

Use ASP.NET Core Identity or a custom identity model if needed.

Support:

```text
email/password
OIDC
OAuth providers
GitHub auth
GitLab auth
2FA
recovery codes
personal access tokens
API tokens
```

Do not build a weak custom auth system unless explicitly required.

## 18.2 Authorization

Use policy-based authorization.

Resource authorization must check:

```text
tenant
team
project
server
application
database
environment
role
permission
```

Do not rely only on route IDs.

## 18.3 Permissions

Use explicit permissions:

```text
projects.read
projects.write
servers.read
servers.write
applications.read
applications.write
deployments.start
deployments.cancel
deployments.readLogs
terminals.open
secrets.read
secrets.write
settings.manage
teams.manage
```

## 18.4 Secrets Access

Reading secrets should require explicit permission.

Most UI should show masked values.

---

# 19. Secrets Management

## 19.1 Secret Storage

Secrets must be encrypted at rest.

Secrets include:

```text
environment variables
SSH private keys
OAuth tokens
webhook secrets
registry credentials
database passwords
TLS private keys
backup credentials
S3 credentials
```

## 19.2 Secret Rules

- Never store plaintext secrets unnecessarily.
- Never log secrets.
- Never return secrets from APIs unless explicitly required and authorized.
- Prefer write-only secret update flows.
- Use envelope encryption where possible.
- Support key rotation eventually.

## 19.3 Environment Variables

Environment variable handling must support:

```text
plain variables
secret variables
build-time variables
runtime variables
per-environment overrides
preview deployment overrides
```

---

# 20. Notifications

## 20.1 Initial Implementation

Implement notifications inside the monolith.

Channels:

```text
database/in-app
email
webhook
Discord
Telegram
Slack-compatible webhook
```

## 20.2 Future Split

Only later consider:

```text
Vessel.Notifications
```

Do not start with a separate notifications service.

## 20.3 Notification Reliability

Notifications should be queued.

Failed notifications should be retryable.

Store delivery attempts.

---

# 21. Reverse Proxy and Routing

## 21.1 Proxy Support

The system should support one or more proxy providers:

```text
Traefik
Caddy
Nginx
custom
```

Start with one provider if necessary.

Use abstraction:

```csharp
IProxyProvider
```

## 21.2 Proxy Rules

Proxy config generation must be:

- Deterministic.
- Versioned.
- Validated before apply.
- Reversible when possible.
- Audited.
- Protected by locks.

---

# 22. Storage

## 22.1 Object Storage

Use S3-compatible object storage abstraction for:

```text
backups
artifacts
large logs if needed
compose snapshots
deployment bundles
export files
```

Use:

```csharp
IObjectStorage
```

## 22.2 Local Storage

Local filesystem storage may be used in development.

Production should prefer durable object storage when appropriate.

---

# 23. Caching and Redis

## 23.1 Redis Usage

Use Redis for:

```text
distributed locks
cache
SignalR backplane later
ephemeral status
rate limiting counters
short-lived coordination
```

Do not use Redis as source of truth for critical records.

## 23.2 Cache Rules

All cache entries must have:

```text
clear key naming
expiration
invalidation strategy
ownership
```

---

# 24. Testing Strategy

## 24.1 Unit Tests

Unit tests cover:

```text
domain rules
value objects
application services
command handlers
permission logic
redaction
config generation
deployment state transitions
```

Use fast tests without Docker/PostgreSQL unless required.

## 24.2 Integration Tests

Integration tests cover:

```text
EF Core mappings
PostgreSQL queries
repositories
Hangfire job wiring
SignalR hub behavior
API endpoints
Docker abstraction with test doubles
process runner behavior
```

Use Testcontainers where appropriate.

## 24.3 E2E Tests

E2E tests cover:

```text
login
create project
add server
create application
start deployment
view logs
open terminal where safe
configure environment variables
rollback where implemented
```

Use Playwright.

## 24.4 Golden Tests

Use golden/snapshot tests for generated files:

```text
docker-compose.yml
.env
proxy config
nginx config
caddy config
traefik labels
deployment scripts
```

## 24.5 Safety Tests

Add tests verifying secrets are redacted from:

```text
logs
command output
API responses
SignalR messages
deployment logs
exception messages
```

---

# 25. AI Coding Agent Rules

## 25.1 General Agent Behavior

Agents must:

- Read AGENTS.md before editing.
- Keep changes small and coherent.
- Prefer feature-first organization.
- Preserve architecture boundaries.
- Add tests for non-trivial logic.
- Avoid introducing unnecessary packages.
- Avoid large rewrites without instruction.
- Update docs when architecture changes.
- Keep code buildable after every meaningful change.
- Run relevant tests when possible.

## 25.2 No Business Logic in UI

Agents must not place business logic in:

```text
.razor components
controllers
SignalR hubs
Hangfire job classes
Program.cs
```

Business logic belongs in:

```text
Vessel.Application
Vessel.Domain
```

Infrastructure details belong in:

```text
Vessel.Infrastructure
```

## 25.3 No Direct Process Calls

Agents must not use:

```csharp
Process.Start(...)
new Process()
```

outside:

```text
Vessel.Infrastructure/Processes
```

Any need to execute a command must go through `IProcessRunner`.

## 25.4 No Direct Docker Calls

Agents must not run Docker from:

```text
Web
Application
Domain
Tests except controlled integration tests
```

Use Docker abstractions.

## 25.5 No Direct DbContext in UI

Agents must not inject DbContext into:

```text
Blazor components
controllers
SignalR hubs
```

except in intentionally simple admin diagnostics approved by maintainers.

Use Application services/queries.

## 25.6 Avoid Premature Microservices

Agents must not create:

```text
Vessel.Worker
Vessel.Agent
Vessel.Gateway
Vessel.Notifications
```

unless explicitly instructed.

They are future extraction points, not initial requirements.

## 25.7 Dependency Rules

Agents must avoid cyclic dependencies.

Agents must not add package references to Domain unless absolutely necessary.

Agents must not add infrastructure packages to Application except abstractions-only packages that are approved.

---

# 26. Coding Standards

## 26.1 C# Style

Use modern C#.

Prefer:

```csharp
file-scoped namespaces
nullable reference types
required properties where appropriate
records for immutable DTOs
readonly record structs for IDs
primary constructors when they improve clarity
async/await for IO
CancellationToken on IO operations
```

## 26.2 Nullability

Nullable reference types must be enabled.

Do not suppress null warnings casually.

Use validation at boundaries.

## 26.3 Cancellation

All IO methods must accept `CancellationToken`.

This includes:

```text
database calls
process execution
Docker operations
SSH operations
Git operations
HTTP calls
storage calls
SignalR streaming
Hangfire job execution
```

## 26.4 Time

Do not use:

```csharp
DateTime.UtcNow
DateTime.Now
```

directly in business logic.

Use:

```csharp
TimeProvider
```

or an application abstraction.

## 26.5 Result/Error Handling

Use clear result types for expected failures.

Expected failures:

```text
validation error
permission denied
resource not found
deployment already running
server unreachable
health check failed
invalid git repo
invalid compose config
```

Unexpected failures should use exceptions and be logged.

## 26.6 Validation

Use validation at boundaries:

```text
API request validation
command validation
domain invariant validation
database constraints
```

Never rely on UI validation alone.

---

# 27. Error Handling

## 27.1 Error Model

Use consistent error responses:

```json
{
  "error": {
    "code": "deployment_already_running",
    "message": "A deployment is already running for this application.",
    "details": {}
  }
}
```

## 27.2 User-Facing Errors

User-facing errors should be:

- Clear.
- Actionable.
- Safe.
- Not leak secrets.
- Not leak internal stack traces.

## 27.3 Logs vs UI

Detailed stack traces go to logs.

Safe summaries go to UI/API.

---

# 28. Package Policy

## 28.1 Preferred Packages

Likely acceptable:

```text
Npgsql.EntityFrameworkCore.PostgreSQL
Hangfire.AspNetCore
Hangfire.PostgreSql
StackExchange.Redis
Serilog.AspNetCore
OpenTelemetry.Extensions.Hosting
OpenTelemetry.Instrumentation.AspNetCore
OpenTelemetry.Instrumentation.EntityFrameworkCore
OpenTelemetry.Instrumentation.Http
FluentValidation
Testcontainers
xUnit
FluentAssertions
Playwright
```

## 28.2 Package Review

Before adding a package, agents must consider:

```text
maintenance
license
security posture
transitive dependencies
fit with architecture
whether .NET built-in APIs are enough
```

Do not add packages for trivial helpers.

---

# 29. Configuration

## 29.1 App Settings

Configuration sources:

```text
appsettings.json
appsettings.{Environment}.json
environment variables
secret store
database settings where appropriate
```

## 29.2 Options Pattern

Use strongly typed options:

```csharp
public sealed class DockerOptions
{
    public required TimeSpan DefaultTimeout { get; init; }
}
```

Validate options at startup.

## 29.3 Environment Names

Use:

```text
Development
Staging
Production
Testing
```

Do not invent environment names casually.

---

# 30. Multi-Tenancy

## 30.1 Tenant Model

Assume a team/tenant model.

Most resources belong to a team or owner:

```text
Project
Server
Application
Database
Environment
Deployment
Secret
NotificationTarget
```

## 30.2 Tenant Enforcement

All queries and commands must enforce tenant/team access.

Do not rely only on UI filtering.

---

# 31. Security Baseline

## 31.1 Security Requirements

Agents must consider security for every feature.

Required:

```text
authentication
authorization
CSRF protection where applicable
rate limiting
input validation
output encoding
secret redaction
audit logs
secure headers
safe file handling
safe process execution
safe SSH execution
```

## 31.2 Dangerous Features

Dangerous features require extra review:

```text
terminal access
file uploads
webhooks
remote command execution
Docker socket access
server provisioning
environment variable viewing
backup restore
proxy config editing
custom scripts
```

## 31.3 Audit Logs

Audit:

```text
login
logout
failed login
team changes
server changes
application changes
deployment start/cancel
terminal session start/end
secret create/update/delete
webhook create/delete
token create/revoke
settings changes
```

Do not store secret values in audit logs.

---

# 32. Webhooks

## 32.1 Webhook Rules

Webhook endpoints must:

- Verify signatures when provider supports it.
- Rate limit.
- Deduplicate events.
- Persist event receipt.
- Enqueue processing.
- Return quickly.
- Avoid long-running work inline.

## 32.2 Providers

Support over time:

```text
GitHub
GitLab
Gitea
Bitbucket
generic webhook
```

---

# 33. Health Checks

## 33.1 App Health

Expose:

```text
/live
/ready
/health
```

Readiness should check:

```text
PostgreSQL
Redis if required
Hangfire storage
object storage if required
```

Liveness should be lightweight.

## 33.2 Managed Server Health

Server health checks should collect:

```text
connectivity
Docker availability
disk usage
memory usage
CPU load
container status
proxy status
certificate status
```

Store historical snapshots where useful.

---

# 34. Performance

## 34.1 General Performance

Avoid premature optimization.

Do optimize:

```text
deployment logs
dashboard queries
large resource lists
SignalR fanout
background job throughput
process output streaming
database indexes
```

## 34.2 Log Storage

Deployment logs can become large.

Design for:

```text
pagination
streaming
retention
archiving
search
redaction
compression if needed
```

## 34.3 Query Performance

Use:

```text
projection queries
AsNoTracking
indexes
pagination
compiled queries where useful
raw SQL for hot paths
```

---

# 35. File and Artifact Generation

## 35.1 Generated Configs

Generated files should be deterministic.

Examples:

```text
docker-compose.yml
.env
proxy config
deployment script
backup script
health check script
```

Store snapshots for auditing/rollback when useful.

## 35.2 File Safety

When writing files:

- Use safe paths.
- Prevent path traversal.
- Do not write secrets to world-readable locations.
- Clean up temporary files.
- Use per-deployment work directories.

---

# 36. Documentation

## 36.1 Required Docs

Maintain:

```text
docs/architecture/overview.md
docs/architecture/modules.md
docs/architecture/deployment-flow.md
docs/security/secrets.md
docs/security/terminal.md
docs/operations/deploying-coolify-dotnet.md
docs/operations/host-native-install.md
docs/operations/containerized-install.md
docs/operations/backups.md
docs/decisions/
```

## 36.2 ADRs

Use Architecture Decision Records for major choices:

```text
ADR-0001-modular-monolith.md
ADR-0002-blazor-web-app.md
ADR-0003-hangfire.md
ADR-0004-process-runner.md
ADR-0005-postgresql.md
ADR-0006-signalr.md
ADR-0007-executable-first-distribution.md
```

---

# 37. Migration From PHP/Laravel

## 37.1 Rewrite Philosophy

This is not a line-by-line syntax port.

Do not mechanically translate PHP classes into C# classes.

Instead:

```text
extract behavior
define contracts
rewrite module by module
test behavior
preserve important user-facing semantics
```

## 37.2 What to Port Carefully

Pay special attention to:

```text
deployment lifecycle
resource model
server provisioning
Docker Compose generation
environment variable semantics
service templates
webhook behavior
notification behavior
authorization behavior
upgrade behavior
backup/restore behavior
proxy routing behavior
health checks
```

## 37.3 What Not to Preserve Blindly

Do not preserve:

```text
Laravel-specific folder structure
Eloquent-specific patterns
Livewire-specific component boundaries
PHP helper-style global functions
stringly typed state machines
implicit magic
framework-specific shortcuts
```

## 37.4 Porting Strategy

Recommended module order:

```text
1. Domain model
2. Authentication/team model
3. Server model
4. Project/application model
5. Environment variables/secrets
6. Git integration
7. Process runner
8. Docker abstraction
9. Deployment logs
10. Basic deployment runner
11. SignalR log streaming
12. Blazor dashboard
13. Health checks
14. Notifications
15. Backups
16. Terminal
17. Proxy integration
18. Service templates
19. Advanced deployment features
20. Import/migration tools
```

---

# 38. Agent Implementation Checklist

Before completing a feature, agents should verify:

```text
[ ] Feature belongs to correct module
[ ] No business logic in Web
[ ] No infrastructure logic in Domain
[ ] No direct Process.Start, new Process, Process.Run, Process.RunAsync, Process.RunAndCaptureTextAsync, Process.StartAndForget, or SafeProcessHandle.Start outside Infrastructure/Processes
[ ] No detached/fire-and-forget process usage without explicit lifecycle policy and security review
[ ] No direct Docker/SSH/Git calls outside Infrastructure
[ ] CancellationToken is supported
[ ] Authorization is enforced
[ ] Secrets are redacted
[ ] Logs are structured
[ ] Errors are safe
[ ] Tests are added or updated
[ ] Migrations are added if schema changed
[ ] Docs/ADR updated if architecture changed
[ ] Build passes
[ ] Relevant tests pass
```

---

# 39. Recommended Initial Milestones

## Milestone 1: Foundation

```text
solution structure
global.json
Directory.Build.props
Directory.Packages.props
basic Web host
basic Blazor layout
PostgreSQL DbContext
Identity/auth skeleton
Serilog
OpenTelemetry
health checks
test projects
basic host-native publish profile
systemd unit template
```

## Milestone 2: Domain Core

```text
teams
users
projects
servers
applications
databases
environments
strongly typed IDs
authorization primitives
audit logs
```

## Milestone 3: Infrastructure Core

```text
IProcessRunner with .NET 11 Process APIs
process exit status, streaming, binary capture, null/file handles, no inherited handles by default, KillOnParentExit, and graceful termination
secret redaction
Docker abstraction with API-first adapter design and CLI fallback through IProcessRunner
SSH abstraction
Git abstraction
Redis abstraction
object storage abstraction
```

## Milestone 4: Deployment MVP

```text
create project
add server
create application
connect git repo
configure environment variables
start deployment
stream logs through SignalR
persist logs
basic health check
show deployment status
```

## Milestone 5: Production Hardening

```text
authorization hardening
audit logs
distributed locks
retry policy
timeouts
rollback metadata
backup/restore
notification delivery
rate limiting
security review
E2E tests
self-update with checksum verification, atomic swap, health check, and rollback
installer hardening
```

---

# 40. Final Architectural Principle

The project should feel like this:

```text
Blazor renders the control panel.
Controllers expose the API.
SignalR streams live state.
Hangfire executes durable work.
Application orchestrates use cases.
Domain protects business rules.
Infrastructure talks to the outside world.
PostgreSQL is the source of truth.
Redis coordinates ephemeral state.
IProcessRunner owns external command execution.
Vessel runs host-native by default and treats containerized control-plane deployment as optional.
```

The most important rule:

```text
Do not let deployment orchestration leak into UI, controllers, hubs, or job classes.
```

The second most important rule:

```text
Do not let shell/process/Docker/SSH logic spread throughout the codebase.
```

The third most important rule:

```text
Start as a modular monolith, not microservices.
```

The fourth most important rule:

```text
Do not make the orchestrator depend on running inside the container runtime it manages.
```
