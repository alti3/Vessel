# Vessel 🚢

**Vessel** is a ground-up C#/.NET rewrite of the [Coolify](https://Vessel.io) project. It aims to provide a robust, high-performance, and type-safe platform for managing and deploying applications, databases, and services.

> [!WARNING]
> **Project Status: Early Alpha / Work in Progress**
> This project is in active initial development. Many features are missing, and the architecture is subject to change. It is not yet suitable for production use.

---

## 🏗️ Architecture

Vessel is designed as a **Modular Monolith** following **Clean Architecture** principles. This approach ensures a simple deployment model initially while maintaining strict internal boundaries that allow for extracting services (Workers, Agents, Gateways) in the future.

### Project Structure
- **Vessel.Web**: The entry point, hosting the Blazor Web App, APIs, and SignalR Hubs.
- **Vessel.Application**: Contains use cases, orchestration logic, and infrastructure interfaces.
- **Vessel.Domain**: Core business logic, entities, and domain rules (dependency-free).
- **Vessel.Infrastructure**: Implementation of data persistence, Docker/SSH/Git clients, and background jobs.
- **Vessel.Shared**: Lightweight DTOs and common contracts.

---

## 🛠️ Technology Stack

- **Runtime**: .NET 11 (Targeting stable GA)
- **Frontend**: Blazor Web App (Interactive Server)
- **API/Realtime**: ASP.NET Core Controllers & SignalR
- **Database**: PostgreSQL with EF Core & Npgsql
- **Background Jobs**: Hangfire
- **Caching/State**: Redis
- **Infrastructure**: Docker, Docker Compose, SSH, Git
- **Observability**: OpenTelemetry & Serilog
- **Storage**: S3-compatible object storage

---

## 🚀 Key Features (In Development)

- **Application Management**: Automated deployments from Git (GitHub, GitLab, etc.).
- **Server Orchestration**: Remote server management via SSH.
- **Database Provisioning**: One-click PostgreSQL, Redis, and more.
- **Real-time Monitoring**: Live deployment logs and terminal access via SignalR.
- **Modular Design**: Extensible proxy support (Traefik, Caddy, Nginx).

---

## ⚙️ Requirements

- **.NET 11 SDK** (Stable)
- **Docker & Docker Compose**
- **PostgreSQL**
- **Redis**

---

## 📄 License

*License information to be added.*

---

*For detailed architectural guidelines and agent instructions, see [AGENTS.md](./AGENTS.md).*
