# ADR 0001: Solution and Package Architecture, Target Frameworks, and Central Package Management

- **Status**: Accepted
- **Date**: 2026-09-10
- **Deciders**: Architecture Team, Engineering Lead

---

## Context

WebhookKit is designed as a reusable, production-ready webhook infrastructure library for ASP.NET Core applications. Consuming webhooks involves cryptographic verification, replay mitigation, atomic deduplication, payload buffering, and state persistence.

A monolithic package would force unwanted dependencies (e.g., Redis drivers, EF Core packages, MVC assemblies) onto consumers who might only need Minimal APIs or in-memory queues. Furthermore, dependency version drift between projects in a multi-project repository can introduce subtle runtime bugs.

---

## Decisions

### 1. Multi-Project Decoupled Architecture

The repository is divided into six source packages:
- **`WebhookKit.Abstractions`**: Pure contracts, status enums, and domain representations. Strictly zero ASP.NET Core, database, or cache dependencies.
- **`WebhookKit.Core`**: Cryptographic HMAC signature engine, deterministic clock abstractions, in-memory store, options system, retry engine, and handler registry.
- **`WebhookKit.AspNetCore`**: Minimal API (`MapWebhook`) mappings, MVC filters/attributes, raw request body capture, and HTTP response writers.
- **`WebhookKit.Redis`**: Distributed deduplication and store provider using `StackExchange.Redis`.
- **`WebhookKit.EntityFrameworkCore`**: Relational store provider and EF Core model configurations.
- **`WebhookKit.Testing`**: Mock request creators, test fixtures, fake clock, and signature generators for consumer unit/integration testing.

### 2. Multi-Targeting Strategy (`net8.0;net9.0;net10.0`)

All core library projects multi-target `net8.0;net9.0;net10.0`:
- **`net8.0`**: Provides broad compatibility with the long-term support (LTS) release used in production environments.
- **`net9.0` & `net10.0`**: Take immediate advantage of JIT improvements, modern hardware intrinsics, and new cryptographic APIs without runtime bridging.
- Test and Sample projects target `net8.0` baseline.

### 3. Central Package Management (CPM)

Enabled globally via `Directory.Packages.props`:
- All NuGet dependency versions are centrally declared in `Directory.Packages.props`.
- Individual `.csproj` files declare `<PackageReference Include="PackageName" />` without specifying versions.
- Eliminates version skew and ensures predictable diamond dependency resolution.

### 4. Solution File Format

The solution uses the modern XML-based `.slnx` format (`Ehsan.Webhook.Kit.slnx`), categorizing projects into `/src/`, `/tests/`, `/samples/`, `/benchmarks/`, and `/solution-items/`.

---

## Consequences

### Positive
- Consumers only pay for what they use; a Minimal API user using Redis doesn't pull in EF Core or MVC.
- Eliminates transitive version conflicts across test and sample suites.
- High testability; core and abstractions can be verified in isolation from web hosting pipelines.

### Neutral / Trade-offs
- Multiple `.csproj` files require maintenance of package definitions in `Directory.Packages.props`.
- Release pipelines must orchestrate packaging and publishing of multiple NuGet artifacts.
