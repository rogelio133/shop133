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

El punto **0.1** (crear solución y estructura de carpetas) se completó antes de que existiera esta convención y no se documentó retroactivamente.
