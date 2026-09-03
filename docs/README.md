# Documentación por subfase

Un documento por cada punto completado del roadmap ([plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)).

**Convención de nombres:** `fase_<fase>_<punto>.md` — el punto del número se sustituye por guion bajo. El punto `0.2` produce `fase_0_2.md`.

Estos documentos existen para dejar por escrito **por qué** se hizo algo de una forma y no de otra: las decisiones, las alternativas descartadas y los detalles que costaron tiempo descubrir. El código dice *qué* hay; esto dice *por qué*.

La regla que obliga a generarlos está en [CLAUDE.md](../CLAUDE.md), sección *Sub-phase documentation*.

## Guías

Documentos de procedimiento, no ligados a un punto del roadmap. No siguen la convención de nombres ni la plantilla de arriba.

| Documento | Contenido |
|---|---|
| [git.md](git.md) | Cómo nombrar ramas, escribir commits, hacer push y cerrar una fase con Pull Requests. El *cómo* de lo que [fase_0_5.md](fase_0_5.md) justifica. |

## Índice

| Punto | Título | Fecha | Documento |
|---|---|---|---|
| 0.2 | Configurar `docker-compose.yml` con SQL Server, RabbitMQ y Jaeger | 2026-08-17 | [fase_0_2.md](fase_0_2.md) |
| 0.3 | Crear proyecto `Shop133.Contracts` con eventos base | 2026-08-17 | [fase_0_3.md](fase_0_3.md) |
| 0.4 | Configurar SQL Server con una base de datos por servicio | 2026-08-17 | [fase_0_4.md](fase_0_4.md) |
| 0.5 | Repositorio Git con convención de branches | 2026-08-17 | [fase_0_5.md](fase_0_5.md) |
| 0.6 | Tests de arquitectura con NetArchTest | 2026-08-18 | [fase_0_6.md](fase_0_6.md) |
| 1.1 | Modelo `Product` | 2026-08-18 | [fase_1_1.md](fase_1_1.md) |
| 1.2 | EF Core + migraciones contra SQL Server (`CatalogDb`) | 2026-08-18 | [fase_1_2.md](fase_1_2.md) |
| 1.3 | Endpoints CRUD de `Catalog.API` | 2026-08-19 | [fase_1_3.md](fase_1_3.md) |
| 1.4 | Seed de datos de prueba y catálogo de categorías | 2026-08-19 | [fase_1_4.md](fase_1_4.md) |
| 1.5 | Swagger/OpenAPI habilitado | 2026-08-20 | [fase_1_5.md](fase_1_5.md) |
| 1.6 | Dockerfile del servicio | 2026-08-20 | [fase_1_6.md](fase_1_6.md) |
| 1.7 | Tests de componente con `WebApplicationFactory` + Testcontainers | 2026-08-20 | [fase_1_7.md](fase_1_7.md) |
| 2.1 | Modelo `Order`, `OrderItem` | 2026-08-20 | [fase_2_1.md](fase_2_1.md) |
| 2.2 | EF Core contra `OrdersDb` | 2026-08-24 | [fase_2_2.md](fase_2_2.md) |
| 2.3 | `POST /orders` con llamada síncrona a Catalog.API | 2026-08-24 | [fase_2_3.md](fase_2_3.md) |
| 2.4 | `Orders.Tests`: el acoplamiento síncrono con WireMock.Net | 2026-08-25 | [fase_2_4.md](fase_2_4.md) |
| 3.1 | MassTransit + RabbitMQ transport en Orders, Inventory y Payments | 2026-08-25 | [fase_3_1.md](fase_3_1.md) |
| 3.2 | Revisión de los eventos de `Shop133.Contracts` | 2026-08-25 | [fase_3_2.md](fase_3_2.md) |
| 3.3 | Orders.API publica `OrderCreated` en lugar de llamar síncronamente | 2026-08-27 | [fase_3_3.md](fase_3_3.md) |
| 3.4 | Inventory.API consume `OrderCreated` y reserva stock contra `InventoryDb` | 2026-08-28 | [fase_3_4.md](fase_3_4.md) |
| 3.5 | Payments.API consume `StockReserved` y simula el cobro contra `PaymentsDb` | 2026-08-31 | [fase_3_5.md](fase_3_5.md) |
| 3.6 | Idempotencia por `MessageId` del sobre en Inventory y Payments | 2026-08-31 | [fase_3_6.md](fase_3_6.md) |
| 3.7 | Tests de consumers con el harness en memoria de MassTransit | 2026-08-31 | [fase_3_7.md](fase_3_7.md) |
| 4.1 | `OrderStateMachine` en Orders.Domain con MassTransit Saga | 2026-09-01 | [fase_4_1.md](fase_4_1.md) |
| 4.2 | Estados de la saga: la cadena feliz completa y `OrderConfirmed` | 2026-09-01 | [fase_4_2.md](fase_4_2.md) |
| 4.3 | Caminos de error de la saga y los dos primeros consumers de Orders.API | 2026-09-02 | [fase_4_3.md](fase_4_3.md) |
| 4.4 | La compensación: `ReleaseStock`, `StockReleased` y `CompensatingStock` | 2026-09-02 | [fase_4_4.md](fase_4_4.md) |
| 4.5 | La saga persistida en `OrdersDb` y el outbox transaccional | 2026-09-02 | [fase_4_5.md](fase_4_5.md) |

El punto **0.1** (crear solución y estructura de carpetas) se completó antes de que existiera esta convención y no se documentó retroactivamente.
