# CLAUDE.md — shop133

Guidance for Claude Code when working in this repository.

## Project overview

**shop133** is a learning-oriented e-commerce backend built as .NET microservices. The pedagogical core is the **order saga with compensations**: an order flows through stock reservation and payment, and a failed payment must automatically release previously reserved stock — no manual intervention.

The roadmap lives in [plan-desarrollo-shop133.md](plan-desarrollo-shop133.md) (Spanish). It is the source of truth for *what* gets built and in what order. This file is the source of truth for *how*.

This is a side project optimized for understanding distributed-systems tradeoffs, not for shipping to production. When a choice is between "clever" and "explains itself", pick the one that explains itself.

## Current status

**Phase 0 complete.** The solution (`shop133.slnx`) and the full project layout exist and build clean on .NET 10; every service project is still empty scaffolding. The local infrastructure is up: `docker-compose.yml` + `docker-compose.override.yml` bring up SQL Server, RabbitMQ and Jaeger. `Shop133.Contracts` now holds the 9 message types (7 events + 2 commands) and the `OrderLine` DTO — see [docs/fase_0_3.md](docs/fase_0_3.md) — but read decisions 4 and 6 **with the revision notes attached to them**: `OrderId` is a `Guid` and `ProductId` an `int` since `1.1`, and `OrderLine` carries `ProductSku` and `ProductName` since 2026-08-19. The four databases and **one SQL login per service** are created by the `db-init` compose service — see [docs/fase_0_4.md](docs/fase_0_4.md). The branch model (`main` / `develop` / `feature/*`) is fixed and live on `origin` — see [docs/fase_0_5.md](docs/fase_0_5.md) and the "Git workflow" section below. `tests/Shop133.ArchitectureTests` makes rules 1, 3, 4 and 5 below executable — 11 tests at the time, all `Category=Fast` (12 since `1.2`) — see [docs/fase_0_6.md](docs/fase_0_6.md); it also moved the whole repo onto **Microsoft.Testing.Platform** (opt-in in `global.json`), which changes the `dotnet test` filter syntax. Phase 0 is **closed**: both PRs (`feature/fase-0 → develop`, then `develop → main`) were merged as merge commits, `feature/fase-0` is deleted, and `main` carries the annotated `fase-0` tag. Work continues on `feature/fase-1-catalog`, cut from `develop`.

**Phase 1 in progress.** `1.4` is done; `1.5`–`1.7` remain. `1.1` landed the first business type of the project: `Product` in `Catalog.Infrastructure/Entities/` — a `sealed class` with private setters and a validating constructor, plus a private parameterless one for EF Core. `1.2` gave it a database — see [docs/fase_1_2.md](docs/fase_1_2.md). `1.3` exposed it over HTTP — see [docs/fase_1_3.md](docs/fase_1_3.md). `CatalogDbContext` and `ProductConfiguration` live in `Catalog.Infrastructure/Persistence/`, `Catalog.Infrastructure` carries `Microsoft.EntityFrameworkCore.SqlServer` **10.0.8** (pinned to match the installed `dotnet-ef` global tool, deliberately behind the `10.0.11` of `Microsoft.AspNetCore.OpenApi`), and `Catalog.API` carries `Microsoft.EntityFrameworkCore.Design` — the one place EF Core is allowed outside an `.Infrastructure` project, because `dotnet ef` looks for it in the startup project. The `InitialCreate` migration is **applied**: `CatalogDb.dbo.Products` exists, with the **unique index on `Sku`** that `1.1` could not enforce from the entity. The connection string lives in User Secrets of `Catalog.API` under `ConnectionStrings:CatalogDb` and connects as `catalog_user`; `Program.cs` guards the key and names it in the failure message. Nothing migrates at startup — `dotnet ef database update` is the only path (`1.7`'s Testcontainers fixture will call `MigrateAsync()` itself). `LayeringRulesTests` gained `EfCorePackages_LiveOnlyIn_InfrastructureProjects`, so the architecture suite is **12** tests.

`1.3` added `ProductsController` — the five CRUD endpoints — and with them the service's first HTTP surface. It **injects `CatalogDbContext` straight into the controller**: on a CRUD a repository would be a passthrough, and the thin-controller rule still holds because the invariants live in the entity's constructor and the uniqueness in the index. HTTP DTOs (`CreateProductRequest`, `UpdateProductRequest`, `ProductResponse`) live in `Catalog.API/Models/` and **never** in `Shop133.Contracts` — that project is for messages that travel over RabbitMQ, and rule 4 bars validation attributes from it. Their `[MaxLength]` values come from the `Product.*MaxLength` constants, never literals. `Product` gained `Update(...)`, which **can change the `Sku`** (decision 9 of `1.1`: the business code gets corrected and renumbered, the surrogate key never changes) — so `409 Conflict` applies to `PUT` as well as `POST`. That 409 is detected by catching `DbUpdateException`, with the SQL Server error numbers kept out of the API layer in `Catalog.Infrastructure/Persistence/DbUpdateExceptionExtensions.cs`. `DELETE` is a hard delete: from Phase 3 on, an `OrderLine.ProductId` can point at a product that no longer exists, and no FK can prevent that across separate databases — the answer is `OrderLine` freezing what it needs, not a foreign key. **No NuGet package was added and no `.csproj` was touched**, so the suite stays at 12 tests.

`1.4` filled the catalog and, doing so, added the **first relationship in the model** — see [docs/fase_1_4.md](docs/fase_1_4.md). `Category` (`Catalog.Infrastructure/Entities/Category.cs`) is a **database lookup table, not an `enum`**: adding a category must not require recompiling and redeploying Catalog.API, and the display name must not be trapped inside a C# identifier. `Product` gained a required `CategoryId` — the parameter sits **before** `imageUrl` in the constructor and in `Update(...)` so the optional one stays last — plus a nullable `Category` navigation, where `null` means *this query did not load it*, never *this product has no category*. Two migrations, deliberately split: `AddProductCategories` (schema) and `SeedSouvenirCatalog` (55 `InsertData` rows), so the seed can be rolled back without dismantling the table. The seed is **50 souvenir products across 5 categories** — Tazas, Llaveros, Playeras, Pines, Libretas, 10 each — living in `Catalog.Infrastructure/Persistence/Seed/CatalogSeedData.cs`. `Catalog.API` gained `CategoriesController` (a single read-only `GET /categories`) and `CategoryResponse`; `ProductResponse` now carries flat `CategoryId` + `CategoryName`. **No NuGet package was added and no `.csproj` was touched**, so the architecture suite stays at 12 tests.

**`HasData` does not run the entity's constructor.** EF materializes seeded rows by reflection, so none of `Product.Apply(...)`'s guards execute over the 50 seed rows — their `Sku`s must already be trimmed and upper-cased in `CatalogSeedData`, because nothing will fix them. And `HasData` needs **fixed explicit ids**, which it inserts under `SET IDENTITY_INSERT`; that does **not** move the identity counter, so the seed occupies ids 1–50 while new `POST`s still get four-digit ids (the first one measured after the seed was `1007`). The rule that **nothing may assume `Product.Id` starts at 1** is unchanged — it now simply coexists with a seed that does use low ids, because `HasData` has no other option.

**The `Sku` format is fixed since `1.4`: `<4 letters>-<3 digits>`** — `TAZA-001`, `LLAV-001`, `PLAY-001`, `PINS-001`, `LIBR-001`. It is a **convention, still not a regex**: decision 9 of `1.1` refused to impose a pattern without real cases, and 50 rows written by the project itself are not evidence that an imported catalog uses the same shape. The prefix is **not** tied to the category by the schema either — nothing stops a `TAZA-011` in `Pines`, and `Category` deliberately has no `Code` column that would imply otherwise.

**Invalid `CategoryId` on `POST`/`PUT` is checked explicitly and returns `400`**, not a translated FK violation. This is *not* inconsistent with how duplicate `Sku` is handled: uniqueness can only be answered by the whole row set at `INSERT` time, so there the exception is the only correct path, whereas "does this category exist" is an ordinary query against five fixed rows — and SQL Server's error 547 does not say *which* foreign key failed. `DbUpdateExceptionExtensions` therefore still handles only 2601/2627. The lookup returns the **entity, not a `bool`**, on purpose: the tracked `Category` is what lets EF fix up `product.Category` so the `201` can include the category name without a second query. On `PUT`, the `404` is checked **before** the category `400` — the URL's resource losing is decisive over the body.

**`ToUpperInvariant()` never lengthens a string in .NET** — `1.1` justified normalizing the `Sku` before validating its length with a `ß → SS` case that does not happen (`ToUpper*` uses *simple* case mapping; measured across the whole BMP in `1.3`, zero characters change length). The ordering stays because it is right for another reason — you validate the value you persist — but do not repeat the `ß` argument. The validation gap that *is* real: `ImageUrl` is optional, so its DTO has no `[Required]`, and `"   "` reaches the entity's guard — which is what the controller's `catch (ArgumentException)` exists for.

**`OrderLine` is a snapshot, not a lookup.** `ProductSku`, `ProductName` and `UnitPrice` are all **frozen at the moment of the order** and nothing ever re-reads them. That is not an optimization to save calls — it is what makes the order *correct*: there is no foreign key to lean on (SQL Server has no cross-database FKs, and `orders_user` cannot even see `CatalogDb`), and Catalog deletes products **physically**, so an order must be able to say what was bought after the product is gone. `ProductId` is a weak pointer, not a foreign key: it tells Inventory what to reserve and links to the product page, and a `404` there is an accepted outcome, not a bug. The rule to carry forward: **whoever builds an `OrderLine` fills all five fields, and three of them come from Catalog.** In `2.3` the planned `HttpClient` call supplies them; when `3.3` deletes that call, something else must still fill the snapshot — that question is open and recorded in the revision note on decision 6 of [docs/fase_0_3.md](docs/fase_0_3.md). Inventory ignores 3 of the 5 fields; whether `ReserveStock`/`ReleaseStock` get their own `StockLine` is **decided in `3.4`**, with the consumer in front of you, not before.

**Nothing may assume `Product.Id` starts at 1.** SQL Server's identity cache restarts numbering in a fresh block of 1000 after a service restart — the first row inserted into the empty table got `Id = 1002` — and `IDENTITY` is not transactional, so an `INSERT` rolled back by the unique index still burns its number. Read the id from the `201` response.

**`Product.Id` is an `int`, assigned by SQL Server via `IDENTITY`** — this reverses half of the decision in `0.3`, which had bound it to `Guid`; `OrderLine.ProductId` changed with it. **The ids in this system are deliberately asymmetric and this is not an oversight**: `OrderId` stays a `Guid` because it is the saga's correlation key and Orders.API must mint it before touching the database, while a product is created by a synchronous `POST` against the only database that writes it. The rule to carry forward is *the id type is decided by who mints it and when* — do not "restore consistency" by making them match. See decision 2 in [docs/fase_1_1.md](docs/fase_1_1.md).

That document also keeps a measured finding that no longer affects `Product` but still applies to `OrderId` when it is persisted in `4.5`: SQL Server compares `uniqueidentifier` starting from the *last* six bytes, so a UUID v7 arrives inside the database as unordered as a random one, and the usual "v7 fixes clustered-index fragmentation" argument is false.

`Product.Sku` is the product's business code — required, stored `Trim()`-ed and upper-cased so `lap-14` and `LAP-14` cannot become two products. Uniqueness is **not** enforced by the entity; it is the unique index added in `1.2`. And `Product.Stock` is the number the catalog *displays*: the reservable stock belongs to `InventoryDb` from `3.4` on, and nothing may decrement this column when an order is placed.

**Compose layout:** `docker-compose.yml` defines the services and publishes **no** host ports — that file is the container-to-container view (`Server=sqlserver`). `docker-compose.override.yml` holds every host port mapping (`Server=localhost,1433`) and is merged automatically. Credentials come from a gitignored `.env`; `.env.example` is the versioned template.

Roadmap items are numbered (`0.1` … `8.6`). From 0.2 onward every completed sub-phase leaves a document in [docs/](docs/) — see "Sub-phase documentation" below.

| Phase | Scope | Status |
|---|---|---|
| 0 | Solution scaffolding, docker-compose, Contracts, architecture tests | **Closed** — merged to `main`, tagged `fase-0` |
| 1 | Catalog.API | **In progress** — 1.1–1.4 done, 1.5–1.7 pending |
| 2 | Orders.API (synchronous) | Not started |
| 3 | MassTransit + RabbitMQ messaging | Not started |
| 4 | Saga + compensations | Not started |
| 5 | YARP Gateway | Not started |
| 6 | Frontend (MVC + Bootstrap 5) | Not started |
| 7 | Observability | Not started |
| 8 | Optional extras (auth, real-infra integration tests, CI/CD, E2E) | Not started |

**Rule:** never reference a project path as if it exists. Verify with Glob first. Paths in the "Solution layout" section below are the *target* structure, not the current one. Update this table when a phase closes.

## Stack

| Concern | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10** (`net10.0`) | SDK 10.0.303 installed. 9.0.306 also present — do not target it. |
| APIs | ASP.NET Core **controller-based APIs** | `ControllerBase` + `[ApiController]`. Not Minimal APIs — decided in Phase 0. |
| ORM | EF Core 10 | SQL Server provider |
| Database | SQL Server 2022 (Docker) | One database per service |
| Messaging | **MassTransit 8.x** + RabbitMQ | Pin the major — see below |
| Gateway | YARP 2.x | |
| Frontend | ASP.NET Core MVC + Bootstrap 5 | |
| Observability | OpenTelemetry → Jaeger, Serilog | |
| Containers | Docker + Docker Compose | |
| Testing | xUnit + MassTransit test harness + Testcontainers | xUnit's own `Assert` — no FluentAssertions, see "Testing" below |

**MassTransit must stay on 8.x.** v8 is Apache 2.0 and receives fixes through at least end of 2026. v9 moved to a commercial license. Never upgrade to 9.x without asking.

## Solution layout (target)

```
shop133/
├── src/
│   ├── Services/
│   │   ├── Catalog/       Catalog.API, Catalog.Infrastructure
│   │   ├── Orders/        Orders.API, Orders.Domain (saga), Orders.Infrastructure
│   │   ├── Payments/      Payments.API
│   │   ├── Inventory/     Inventory.API
│   │   └── Notifications/ Notifications.API
│   ├── Gateway/           Shop133.Gateway
│   ├── Frontend/          Shop133.Web
│   └── Shared/            Shop133.Contracts
├── tests/
│   ├── Shop133.ArchitectureTests/   The rules in this file, made executable
│   └── Services/
│       ├── Catalog/       Catalog.Tests
│       ├── Orders/        Orders.Tests (saga + consumers)
│       ├── Inventory/     Inventory.Tests
│       └── Payments/      Payments.Tests
├── db/init/               SQL run by the db-init compose service (databases + per-service logins)
├── docs/                  One .md per completed sub-phase + README.md index
├── docker-compose.yml
└── docker-compose.override.yml
```

Do not create projects outside this structure without asking.

## Architecture rules

These are the rules the project exists to teach. Breaking one silently defeats the purpose.

**1. One database per service.** No service opens a connection to another service's database. Not for a "quick read", not for a join, not for a report. If a service needs another's data, it gets it through an event or an API call. `CatalogDb`, `OrdersDb`, `InventoryDb`, `PaymentsDb` each have exactly one owner. Since Phase 0.4 this is enforced by SQL Server, not by convention: each service connects with its own login (`catalog_user`, `orders_user`, …) that has `db_owner` on its own database and no access at all to the others. Reaching for a neighbour's database fails with `Msg 916`. Never "fix" that by switching a service to `sa`.

**2. Services communicate through events.** From Phase 3 onward, cross-service communication goes through RabbitMQ. The synchronous `HttpClient` call from Orders → Catalog in Phase 2 is *deliberate technical debt* meant to make the coupling painful; mark it `// PHASE-2 DEBT: replaced by OrderCreated event in Phase 3` and delete it in Phase 3.

**3. The Frontend talks only to the Gateway.** `Shop133.Web` never holds a base URL of an individual service. CORS, rate limiting, and (later) auth are centralized at the Gateway.

**4. `Shop133.Contracts` stays thin.** Immutable `record` types for events and DTOs only. No business logic, no EF Core, no MassTransit dependency, no validation attributes. Every service references it; it references nothing. Changing a contract is a breaking change across the system — treat it as such.

**5. Dependency direction inside a service:** `.API` → `.Infrastructure` → `.Domain`. The domain layer references no project other than `Shop133.Contracts` — that single exception exists because saga state machines live in `Orders.Domain` and consume the shared messages; duplicating them inside the domain would defeat rule 4. Enforced by `LayeringRulesTests`.

**6. Every message consumer is idempotent.** RabbitMQ guarantees at-least-once delivery, so duplicates *will* happen. Persist processed `MessageId`s and skip repeats. This is not optional polish — "duplicate event" is one of the four mandatory test scenarios.

**7. Compensation is explicit.** When payment fails after stock was reserved, the saga publishes `ReleaseStock`. There is no path where reserved stock leaks.

## Conventions

- **All code identifiers in English**, PascalCase for types and properties: `Product.Name`, `Order.Status` — not `Producto.Nombre`. Comments and docs may be Spanish; code may not be mixed.
- **Events are past tense** and are `record` types: `OrderCreated`, `StockReserved`, `StockRejected`, `PaymentCompleted`, `PaymentFailed`, `OrderConfirmed`, `OrderCancelled`.
- **Commands are imperative**: `ReserveStock`, `ReleaseStock`.
- **Assembly naming**: shared/infrastructure projects take the `Shop133.` prefix (`Shop133.Contracts`, `Shop133.Gateway`, `Shop133.Web`); service projects do not (`Catalog.API`, `Orders.Domain`).
- **Databases**: `CatalogDb`, `OrdersDb`, `InventoryDb`, `PaymentsDb`. The saga state is persisted in `OrdersDb`.
- **Secrets** go in User Secrets or environment variables, never in `appsettings.json`.
- **Controllers** live in `Controllers/`, are named `<Plural>Controller`, and carry `[ApiController]` + `[Route("[controller]")]`. Keep them thin: bind, delegate, return `ActionResult<T>`. Business logic belongs in `.Infrastructure`/`.Domain`, not in the action. MassTransit consumers are *not* controllers — they live in `Consumers/` from Phase 3 on.

## Git workflow

Three long-lived rules, decided in `0.5` — see [docs/fase_0_5.md](docs/fase_0_5.md) for the *why*, and [docs/git.md](docs/git.md) for the step-by-step *how* (branching, committing, closing a phase, recovering from mistakes).

**Branches:**

| Branch | Role |
|---|---|
| `main` | Stable. Advances **only when a phase closes**, via `--no-ff` merge from `develop`, and gets a tag. Never commit to it directly. |
| `develop` | Integration branch. Every sub-phase lands here. This is the branch you branch *from* and merge *back into*. |
| `feature/*` | One branch per phase: `feature/fase-1-catalog`. Cut from `develop`, merged back into `develop` with `--no-ff`, then deleted. |

- **Branch names use slashes and hyphens, never underscores**: `feature/fase-1-catalog`, not `feature_fase_1`. The slash is what makes `feature/*` a namespace that Git tooling can filter.
- **A phase gets one branch, not a branch per sub-phase.** Sub-phases are commits inside it; the branch closes when the phase does.
- **`--no-ff` on both merges.** A fast-forward erases the fact that a set of commits belonged to one phase, which is exactly the history worth keeping here.
- **Both merges are executed as Pull Requests on github.com**, using the **"Create a merge commit"** button — which produces exactly the `--no-ff` merge commit the rule above requires. *Squash and merge* and *Rebase and merge* are forbidden: the first collapses the per-sub-phase commits that link roadmap ↔ commit ↔ `docs/`, the second erases the phase boundary. Watch the `base:` dropdown — GitHub defaults it to `main`, so a `feature/* → develop` PR needs it changed by hand.
- **The phase tag stays local.** A merged PR does not create an annotated tag, so after the `develop → main` PR: `git switch main; git pull; git tag -a fase-N -m "..."; git push origin fase-N`.
- **Tags on `main`, one per closed phase**: `fase-0`, `fase-1`, … Annotated (`git tag -a`), so the tag carries a message and a date.

**Commits:** one line, `<sub-phase> <what changed, in English, past tense>` — `0.5 git branching convention defined`. The sub-phase number is the link between a commit, its roadmap item and its `docs/` document. English like every other identifier; the prose in `docs/` is where Spanish lives.

**Never rewrite published history.** `develop` and `main` are pushed; `--force` on either is off the table. A mistake gets a follow-up commit or a `git revert`.

## Testing

Tests are not a phase. They are numbered items spread across the roadmap — `0.6`, `1.7`, `2.4`, `3.7`, `4.7`, `5.4`, plus `8.2`/`8.6` — landing where the code they cover starts to exist. See "Estrategia de tests" in [plan-desarrollo-shop133.md](plan-desarrollo-shop133.md) for the full mapping.

**1. Never the EF Core InMemory provider.** It enforces no relational constraints and has no real transactions, so it green-lights code that fails against SQL Server. Real SQL Server through Testcontainers, or the data layer goes untested — there is no third option.

**2. Saga and consumer logic is tested with the MassTransit test harness** (`AddMassTransitTestHarness`, in-memory transport), not against a real broker. Milliseconds instead of seconds, which is what makes it viable to have dozens of saga cases. Real RabbitMQ appears only in `8.2`, and only for what the harness cannot cover: the exchange topology MassTransit creates by convention.

**3. Every consumer gets an idempotency test** — deliver the same `MessageId` twice, assert a single effect. This is the only reliable check of architecture rule 6; by-hand verification does not survive a refactor.

**4. Architecture tests are rules 1, 3, 4 and 5 in executable form.** Live since `0.6` — see [docs/fase_0_6.md](docs/fase_0_6.md). When adding a new architecture rule to this file, consider whether `Shop133.ArchitectureTests` can enforce it. A rule that only lives in prose gets broken silently — which is the exact failure this project is meant to avoid.

The reference rules read the **`.csproj` files**, not the compiled assemblies: Roslyn prunes unused references from the manifest, so with service projects still empty an assembly-level check would pass vacuously. `ProjectGraph.cs` is that reader; add new reference rules on top of it. Rules about *types* (records, immutability) use plain reflection, and `NetArchTest` covers the one namespace-dependency assertion.

**5. Categories via `[Trait("Category", ...)]`**: `Fast` (no Docker) and `Docker` (Testcontainers). Keeps the development loop fast while CI (`8.3`) runs both. The first `Docker` test arrives in `1.7`; everything today is `Fast`.

**5b. Test projects run on Microsoft.Testing.Platform, not VSTest.** The .NET 10 SDK dropped the VSTest bridge that `xunit.v3` used to run through, so the opt-in lives in `global.json` (`"test": { "runner": "Microsoft.Testing.Platform" }`) and every test project needs `<OutputType>Exe</OutputType>` — it launches itself. Consequences: `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are VSTest infrastructure and must **not** be added, and the filter syntax changed (see "Commands").

**6. Naming**: project `<Project>.Tests`, class `<SUT>Tests`, method `Method_Scenario_ExpectedResult`. English identifiers, like all other code.

**7. Assertions use xUnit's own `Assert`.** Deliberate: **FluentAssertions 8.x moved to a commercial license** for non-open-source use — the same trap as MassTransit 9. If fluent syntax ever becomes worth a package, prefer Shouldly (BSD) over pinning FluentAssertions to 7.x.

Already in use: `xunit.v3` 4.0.0, `NetArchTest.Rules` 1.3.2. Packages the remaining items will need, all still subject to "ask before adding a NuGet package": `MassTransit.TestFramework`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.MsSql`, `Testcontainers.RabbitMq`, `WireMock.Net`, `Respawn`.

## Sub-phase documentation

Every checklist item in [plan-desarrollo-shop133.md](plan-desarrollo-shop133.md) carries a stable number (`0.1`, `0.2`, … `8.5`). **Completing one produces a document in `docs/`.** This is not a wrap-up step to do if there is time left — it is part of the definition of done.

**A sub-phase is not closed until all three of these are true:**

1. `docs/fase_<phase>_<item>.md` exists (the dot becomes an underscore: `0.2` → `fase_0_2.md`).
2. The checkbox in the roadmap is ticked **and** links to it: `- [x] **0.2** <título> — [doc](docs/fase_0_2.md)`.
3. `docs/README.md` has a row for it in the index table.

Do not report a sub-phase as finished with any of the three missing.

**Documents are written in Spanish** (code identifiers stay English, as everywhere else). Required sections, in this order:

| Sección | Contenido |
|---|---|
| `# Fase X.Y — <título>` | Con una línea de fecha, estado y link al roadmap. |
| **Objetivo** | Qué resuelve el punto y por qué está en esa posición del roadmap. Incluye lo que queda deliberadamente fuera de alcance. |
| **Decisiones** | Cada decisión no obvia, con **la alternativa que se descartó y el motivo**. |
| **Cambios** | Archivos creados o modificados, con ruta relativa y el rol de cada uno. |
| **Detalles que cuestan tiempo** | Los gotchas concretos: lo que no es evidente y costaría volver a descubrir. |
| **Verificación** | Los comandos que se ejecutaron **con su salida real**. |
| **Pendiente** | Lo que queda fuera y en qué punto o fase entra. |

Dos reglas sobre el contenido:

- **La sección de Decisiones es la que da valor al documento.** Si solo describe *qué* se hizo y no *por qué* se eligió eso sobre la alternativa, no está haciendo su trabajo — el código ya dice qué hay.
- **Se documenta lo que se ejecutó y se observó, no lo que se pretendía hacer.** Si una verificación falló, se saltó o dio un resultado inesperado, va en el documento. Un problema encontrado por el camino (y cómo se resolvió) vale más que una lista de comandos que salieron bien.

[docs/fase_0_2.md](docs/fase_0_2.md) es la referencia de formato.

## Commands

Available now:

```powershell
# No service list: naming services explicitly would skip db-init, which creates
# the four databases and their per-service logins. It exits 0 once done, so it
# only shows up under `ps -a`.
docker compose up -d
docker compose ps -a
docker compose down
```

Available after Phase 0 scaffolding:

```powershell
dotnet build
dotnet run --project src/Services/Catalog/Catalog.API

# Tests — see the "Testing" section for what each category covers.
# Filter options come from the xunit adapter, so they go AFTER `--`.
dotnet test                                        # everything (needs Docker from 1.7 on)
dotnet test -- --filter-trait "Category=Fast"      # development loop, no Docker needed
dotnet test -- --filter-class "*ContractsRulesTests"
dotnet test tests/Shop133.ArchitectureTests        # a single test project

# Gotchas: `--filter Category=Fast` (VSTest style, before `--`) is gone; `--trait` is not
# the option name under MTP, and passing it yields a silent "Zero tests ran".
# `dotnet test -- --help` crashes the CLI — use `dotnet test --project <path> --help`.

# EF Core migrations — DbContext lives in Infrastructure, host in API
dotnet ef migrations add <Name> `
  --project src/Services/Catalog/Catalog.Infrastructure `
  --startup-project src/Services/Catalog/Catalog.API
dotnet ef database update `
  --project src/Services/Catalog/Catalog.Infrastructure `
  --startup-project src/Services/Catalog/Catalog.API

# Gotchas (both measured in 1.2):
# - Never `--no-build` right after `migrations add`: that command compiles BEFORE writing the
#   migration files, so the assembly in bin/ has no migration and `migrations script --no-build`
#   silently emits only the __EFMigrationsHistory table. No error, no warning.
# - `dotnet ef` sets ASPNETCORE_ENVIRONMENT=Development on its own, which is what makes User
#   Secrets load. If something else forces another environment, the connection string vanishes;
#   pass `--environment Development`.
```

Local UIs: RabbitMQ management `http://localhost:15672` (guest/guest) · Jaeger `http://localhost:16686`

## Environment gotchas

- **PowerShell 5.1 has no `&&`.** Chain with `;` or run commands separately. Backtick (`` ` ``) is the line-continuation character, not backslash.
- **A new `.cs` file needs no `.csproj` edit — but Visual Studio needs a refresh.** SDK-style projects glob every `**/*.cs` under the project folder implicitly (`Microsoft.NET.Sdk.DefaultItems.props`), so a file created from outside the IDE is already compiled. Verify with `dotnet msbuild <project>.csproj -getItem:Compile`, which lists it. **Never add `<Compile Include="..." />`** to make a file "appear": it is redundant, and alongside the default glob it produces duplicate-item errors (`NETSDK1022`). When Solution Explorer does not show the file, the stale thing is VS's own cache (`.vs/ProjectEvaluation/`), not the project — refresh Solution Explorer, then Unload/Reload the project, then reopen the solution, and as a last resort delete `.vs/` with VS closed (it is gitignored IDE state and regenerates).
- **SQL Server 2022 image**: use `MSSQL_SA_PASSWORD`, not the deprecated `SA_PASSWORD`.
- **OpenAPI on .NET 10**: use the built-in `Microsoft.AspNetCore.OpenApi` package (`AddOpenApi()` / `MapOpenApi()`) with Scalar for the UI. Swashbuckle was dropped from the templates in .NET 9 — do not reintroduce it out of habit.
- **Jaeger all-in-one** needs `COLLECTOR_OTLP_ENABLED=true` to accept OTLP directly from the OpenTelemetry SDK.
- **Connection strings**: container-to-container uses the compose service name (`Server=sqlserver`), host-to-container uses `localhost,1433`. Mixing these up is the most common Phase 0 failure. Both need `TrustServerCertificate=True` (self-signed cert — same reason `sqlcmd` needs `-C`), and both use the service's own login, never `sa`.
- **MassTransit 8.x** ships `net8.0`/`net9.0` targets; that is fine on a `net10.0` host. Do not "fix" this by upgrading to v9.

## Working agreements

- **Never run git write commands.** `git add`, `git commit`, `git push`, `git tag`, branch creation and PRs are the user's job — always. Suggest the commit message if it helps, but do not stage or commit anything, not even after finishing a sub-phase.
- **After creating or deleting `.cs` files, build the affected project and list the paths.** Run `dotnet build <project>` — that is what proves the implicit glob picked the file up and that it compiles — and end the reply with the created/deleted paths so they can be refreshed in Visual Studio without hunting for them. A file written but never compiled is not delivered. See the globbing gotcha above for why the fix is never a `.csproj` edit.
- **Ask before adding a NuGet package.** The dependency list is part of the learning exercise.
- **Ask before adding a project** not in the target layout above.
- **Never close a sub-phase without its `docs/` document**, the roadmap link and the index row — see "Sub-phase documentation" above.
- **Update the status table** in this file when a phase closes.
- Prefer boring, explicit code over abstraction. This codebase is meant to be read.
- When a change touches a service boundary (a new event, a changed contract, a new cross-service call), call it out explicitly rather than folding it into a larger diff.
