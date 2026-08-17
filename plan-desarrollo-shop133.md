# Plan de Desarrollo — shop133 (Microservicios .NET)

## Stack definido

| Capa | Tecnología |
|---|---|
| Backend | .NET 10, ASP.NET Core Web API (controllers) |
| Frontend | ASP.NET Core MVC / Razor Pages + Bootstrap 5 |
| Mensajería | RabbitMQ + MassTransit (Saga State Machine) |
| Base de datos | SQL Server (una instancia/DB por microservicio) |
| ORM | EF Core |
| Gateway | YARP |
| Contenedores | Docker + Docker Compose |
| Observabilidad | OpenTelemetry + Jaeger + Serilog |

**Nota de arquitectura:** el Frontend NO es un microservicio más de negocio — es un cliente web (BFF opcional) que consume el API Gateway. No debe acceder directamente a las bases de datos de los servicios.

---

## Estructura de solución

```
shop133/
├── src/
│   ├── Services/
│   │   ├── Catalog/
│   │   │   ├── Catalog.API/
│   │   │   └── Catalog.Infrastructure/
│   │   ├── Orders/
│   │   │   ├── Orders.API/
│   │   │   ├── Orders.Domain/          (Saga State Machine aquí)
│   │   │   └── Orders.Infrastructure/
│   │   ├── Payments/
│   │   │   └── Payments.API/
│   │   ├── Inventory/
│   │   │   └── Inventory.API/
│   │   └── Notifications/
│   │       └── Notifications.API/
│   ├── Gateway/
│   │   └── Shop133.Gateway/             (YARP)
│   ├── Frontend/
│   │   └── Shop133.Web/                 (MVC + Bootstrap 5)
│   └── Shared/
│       └── Shop133.Contracts/           (eventos compartidos: OrderCreated, StockReserved, etc.)
├── docs/                                (un .md por subfase completada — ver docs/README.md)
├── docker-compose.yml
└── docker-compose.override.yml
```

`Shop133.Contracts` es clave: contiene solo los DTOs/eventos que viajan por RabbitMQ, referenciado por todos los servicios para mantener consistencia de contratos sin acoplar lógica.

---

## Fase 0 — Setup base (3-4 días)

- [x] **0.1** Crear solución y estructura de carpetas
- [x] **0.2** Configurar `docker-compose.yml` con: SQL Server, RabbitMQ, Jaeger — [doc](docs/fase_0_2.md)
- [x] **0.3** Crear proyecto `Shop133.Contracts` con eventos base — [doc](docs/fase_0_3.md)
- [ ] **0.4** Configurar SQL Server con una base de datos por servicio (`CatalogDb`, `OrdersDb`, `InventoryDb`, `PaymentsDb`)
- [ ] **0.5** Repositorio Git con convención de branches (`main`, `develop`, `feature/*`)

**Docker Compose — servicio SQL Server:**
```yaml
sqlserver:
  image: mcr.microsoft.com/mssql/server:2022-latest
  environment:
    - ACCEPT_EULA=Y
    - MSSQL_SA_PASSWORD=YourStrong@Passw0rd
  ports:
    - "1433:1433"
  volumes:
    - sqlserver-data:/var/opt/mssql
```

---

## Fase 1 — Catalog.API (síncrono, base) (1 semana)

- [ ] **1.1** Modelo `Product` (Id, Nombre, Descripción, Precio, Stock inicial, ImagenUrl)
- [ ] **1.2** EF Core + migraciones contra SQL Server (`CatalogDb`)
- [ ] **1.3** Endpoints: `GET /products`, `GET /products/{id}`, `POST /products`, `PUT`, `DELETE`
- [ ] **1.4** Seed de datos de prueba
- [ ] **1.5** Swagger/OpenAPI habilitado
- [ ] **1.6** Dockerfile del servicio

**Objetivo de la fase:** tener un servicio funcional end-to-end (DB → API → Docker) antes de meter mensajería.

---

## Fase 2 — Orders.API + comunicación síncrona inicial (1 semana)

- [ ] **2.1** Modelo `Order`, `OrderItem` (estado: Pending, Confirmed, Cancelled)
- [ ] **2.2** EF Core contra `OrdersDb`
- [ ] **2.3** `POST /orders` que llama síncronamente (HttpClient) a Catalog.API para validar productos/precios

**Objetivo de la fase:** aquí **sentirás el acoplamiento** — si Catalog.API está caído, Orders falla. Este dolor es intencional; es el que resuelves en la Fase 3. No es una tarea entregable, por eso no lleva número.

---

## Fase 3 — Mensajería con MassTransit + RabbitMQ (1-1.5 semanas)

- [ ] **3.1** Instalar MassTransit + RabbitMQ transport en Orders, Inventory, Payments
- [ ] **3.2** Definir eventos en `Shop133.Contracts`: `OrderCreated`, `StockReserved`, `StockRejected`, `PaymentCompleted`, `PaymentFailed`
- [ ] **3.3** Orders.API publica `OrderCreated` en lugar de llamar síncronamente
- [ ] **3.4** Inventory.API consume `OrderCreated`, valida y reserva stock contra `InventoryDb`, publica `StockReserved`/`StockRejected`
- [ ] **3.5** Payments.API consume `StockReserved`, simula cobro, publica `PaymentCompleted`/`PaymentFailed`
- [ ] **3.6** Implementar **idempotencia**: guardar `MessageId` procesados para evitar duplicados

---

## Fase 4 — Saga completa con compensaciones (1-1.5 semanas)

Este es el núcleo del aprendizaje.

- [ ] **4.1** Crear `OrderStateMachine` en Orders.Domain con MassTransit Saga
- [ ] **4.2** Estados: `Submitted → StockPending → StockReserved → PaymentPending → Confirmed`
- [ ] **4.3** Camino de error: `StockRejected → Cancelled` / `PaymentFailed → CompensatingStock → Cancelled`
- [ ] **4.4** Implementar evento de compensación `ReleaseStock` cuando el pago falla después de reservar
- [ ] **4.5** Persistir el estado de la Saga en `OrdersDb` (SQL Server como Saga repository)
- [ ] **4.6** Notifications.API consume `OrderConfirmed`/`OrderCancelled` y "envía" notificación (log o mock de email)

**Escenarios de prueba obligatorios:**
1. Compra exitosa (feliz)
2. Sin stock disponible
3. Stock reservado pero pago rechazado (compensación)
4. Evento duplicado (verificar idempotencia)

---

## Fase 5 — API Gateway con YARP (3-4 días)

- [ ] **5.1** Configurar rutas: `/api/catalog/*`, `/api/orders/*`, etc. hacia cada servicio
- [ ] **5.2** Agregar rate limiting básico
- [ ] **5.3** Centralizar CORS aquí (para que el Frontend solo hable con el Gateway)

---

## Fase 6 — Frontend con Bootstrap 5 (1-1.5 semanas)

Proyecto `Shop133.Web` (ASP.NET Core MVC), consumiendo **únicamente el Gateway**, nunca los servicios directo.

- [ ] **6.1** Layout base con Bootstrap 5 (navbar, footer, `_Layout.cshtml`)
- [ ] **6.2** Vista de catálogo: grid de productos con `card` de Bootstrap
- [ ] **6.3** Carrito de compras (puede vivir en sesión o cookie mientras no hay auth)
- [ ] **6.4** Formulario de checkout (Bootstrap forms + validación client-side)
- [ ] **6.5** Página de estado del pedido — aquí es interesante mostrar el estado en tiempo real:
  - Opción simple: polling cada 2-3s a `GET /orders/{id}/status`
  - Opción avanzada: SignalR para push en tiempo real cuando la Saga cambia de estado
- [ ] **6.6** Uso de `IHttpClientFactory` con Polly para resiliencia al llamar al Gateway (retry, circuit breaker)
- [ ] **6.7** Toasts de Bootstrap para feedback de éxito/error

**Sugerencia de UX que refuerza el aprendizaje de arquitectura:** muestra visualmente el estado del pedido avanzando por las etapas (Reservando stock → Procesando pago → Confirmado), para que el frontend refleje la naturaleza asíncrona del backend en vez de ocultarla.

---

## Fase 7 — Observabilidad (4-5 días)

- [ ] **7.1** Integrar OpenTelemetry en todos los servicios + Frontend
- [ ] **7.2** Exportar trazas a Jaeger (ya en docker-compose)
- [ ] **7.3** Serilog con logging estructurado, correlación por `TraceId`
- [ ] **7.4** Verificar que puedes seguir **una sola request del Frontend** a través de Gateway → Orders → RabbitMQ → Inventory → Payments → de vuelta, en Jaeger

Este es el "aha" del proyecto: ver visualmente la complejidad que la arquitectura introduce.

---

## Fase 8 — Pulido y extras opcionales (según tiempo)

- [ ] **8.1** Autenticación con JWT en el Gateway (Identity o Auth0/Keycloak)
- [ ] **8.2** Tests de integración con Testcontainers (SQL Server + RabbitMQ reales en contenedores para tests)
- [ ] **8.3** CI/CD con GitHub Actions (build, test, push de imágenes Docker)
- [ ] **8.4** Health checks (`/health`) en cada servicio + panel simple en el Frontend
- [ ] **8.5** Migrar Kafka en lugar de RabbitMQ como ejercicio comparativo (opcional, ambicioso)

---

## Estimación total

| Fase | Duración estimada |
|---|---|
| 0. Setup | 3-4 días |
| 1. Catalog.API | 1 semana |
| 2. Orders síncrono | 1 semana |
| 3. Mensajería | 1-1.5 semanas |
| 4. Saga + compensación | 1-1.5 semanas |
| 5. Gateway | 3-4 días |
| 6. Frontend Bootstrap | 1-1.5 semanas |
| 7. Observabilidad | 4-5 días |
| **Total núcleo (Fases 0-7)** | **~7-9 semanas** a ritmo de side project (5-10h/semana) |

La Fase 8 es indefinida — extiéndela según lo que quieras profundizar.

---

## Checklist de "señales de que estás aprendiendo de verdad"

- Puedes explicar por qué NO compartes base de datos entre servicios
- Puedes reproducir y explicar un escenario de inconsistencia temporal (ej. el pedido aparece "Pending" en el Frontend mientras el pago aún se procesa)
- Puedes forzar un fallo de pago y ver la compensación liberar el stock sin intervención manual
- Puedes rastrear una request completa en Jaeger sin perderte
- Puedes matar un contenedor de un servicio a mitad de flujo y razonar qué pasa con los mensajes en RabbitMQ
