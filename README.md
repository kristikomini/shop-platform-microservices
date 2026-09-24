# Shop Platform — Microservices Example (.NET 10)

[![CI](https://github.com/kristikomini/shop-platform-microservices/actions/workflows/ci.yml/badge.svg)](https://github.com/kristikomini/shop-platform-microservices/actions/workflows/ci.yml)

A minimal but realistic microservices e-commerce platform, built to demonstrate
the core patterns interviewers ask about: **service-per-database**, an **API
gateway**, **synchronous** service-to-service HTTP calls, and **asynchronous**
event-driven messaging — with **health checks**, **resilience policies**,
**OpenAPI docs**, and an **automated test suite** on top.

**Stack:** .NET 10 · Angular 20 · PostgreSQL · RabbitMQ · YARP · Docker Compose ·
Polly · OpenTelemetry + Jaeger · xUnit + Testcontainers · GitHub Actions.

## Architecture

```
        ┌──────────────┐        ┌──────────────┐
        │ Angular SPA  │ ◀────── │  API Gateway │   (YARP — the only public port, 8080)
        │  (nginx)     │         └──────┬───────┘   serves the SPA at "/",
        └──────────────┘      ┌─────────┴───────┐   routes "/catalog/*" & "/orders/*"
                              ▼                 ▼
                      ┌───────────────┐  ┌───────────────┐
                      │ Catalog svc   │  │ Orders svc    │
                      │  + catalog-db │  │  + orders-db  │
                      └───────┬───────┘  └───────┬───────┘
                              │ POST /reserve     │  (sync HTTP: atomically reserve stock)
                              └────────◀──────────┘
                                                 │ outbox → dispatcher → "order-placed"
                                                 ▼
                                           ┌──────────┐      ┌──────────────┐
                                           │ RabbitMQ │ ───▶ │ Shipping svc │
                                           └──────────┘      └──────┬───────┘
                    "order-shipped"  ◀───────────────────────────────┘
                    (Orders consumes it and advances the order to "Shipped")
```

The whole app lives on **one public port (8080)**: the browser loads the Angular
SPA from the gateway, and the SPA's `/catalog/*` and `/orders/*` calls hit the
same origin — so the gateway routes them to the services with **no CORS**.

### The pieces

| Component      | Type              | Owns        | Talks via                          |
|----------------|-------------------|-------------|------------------------------------|
| **frontend**   | Angular 20 + nginx| —           | HTTP, all via the gateway          |
| **gateway**    | YARP proxy        | —           | serves SPA at `/`, routes `/catalog/*` & `/orders/*` |
| **catalog**    | Minimal API       | `catalog-db`| HTTP                               |
| **orders**     | Minimal API       | `orders-db` | HTTP (calls catalog) + RabbitMQ    |
| **shipping**   | Worker (no API)   | —           | RabbitMQ (consumes events)         |

### Two principles that make this "microservices"

1. **Each service owns its own database.** Orders never reads Catalog's tables —
   it asks Catalog over HTTP. `catalog-db` and `orders-db` are separate Postgres
   containers.
2. **Sync vs async, used deliberately.** Orders calls Catalog *synchronously*
   because it needs the price *now*. It announces `order-placed` *asynchronously*
   so Shipping (and any future service) can react without Orders waiting or even
   knowing they exist.

## Run it

Requires Docker Desktop.

```bash
cd "C:\Users\krsit\Desktop\E-commerce"
docker compose up --build
```

First build downloads the images and compiles all services (a few minutes).
When it settles, open **http://localhost:8080** in a browser — the Angular shop
UI loads, lists the catalog, and lets you place orders.

## Try it — the UI

Open **http://localhost:8080**. You can:
- **Order** a product — its stock drops, the order appears as **Placed**, then
  flips to **Shipped** a couple of seconds later (watch it change live).
- Try to order more than the available stock — it's **rejected** with a message.
- **Add** a product with the form, or **search** the catalog by name.

The **shipping** container logs `📦 Preparing shipment...` then `🚚 shipped`,
proving the event path end-to-end (Orders never called Shipping directly).

## Try it — the API directly

Everything the UI does, you can do with curl (all through the gateway on 8080):

```bash
# 1. List seeded products (routed to the Catalog service)
curl http://localhost:8080/catalog/products

# 2. Place an order (routed to Orders, which calls Catalog, then emits an event)
curl -X POST http://localhost:8080/orders/orders \
  -H "Content-Type: application/json" \
  -d "{\"productId\":1,\"quantity\":2}"

# 3. List placed orders
curl http://localhost:8080/orders/orders
```

- RabbitMQ management UI: **http://localhost:15672** (guest / guest)
- API docs (Scalar / OpenAPI): **http://localhost:8080/catalog/scalar/v1** and
  **http://localhost:8080/orders/scalar/v1**
- Distributed traces (Jaeger UI): **http://localhost:16686** — place an order,
  then open the `gateway` service to see one trace span gateway → orders →
  catalog *and* the async RabbitMQ hops into shipping and back.

## Domain features

- **Stock management with atomic reservation.** Placing an order asks Catalog to
  `POST /products/{id}/reserve`, which decrements stock **only if enough is
  available** (`WHERE stock >= qty` + rows-affected check — race-safe under
  concurrent orders). If not, Catalog returns **409** and the order is rejected
  before anything is persisted. Catalog owns stock; Orders never writes it.
- **Event-driven order lifecycle.** A new order starts as **Placed**. Shipping
  consumes `order-placed`, prepares the parcel, and emits `order-shipped`; Orders
  consumes *that* and advances the order to **Shipped** — so the lifecycle is
  driven entirely by events, and the UI reflects the change live (it polls).
- **Product management + search.** The UI can add products and search the catalog
  by name (`GET /products?search=...`, case-insensitive on the server).

## Production-minded engineering

Beyond "it runs", the repo shows the patterns a reviewer looks for:

- **Distributed tracing (OpenTelemetry → Jaeger)** — every service is
  auto-instrumented (ASP.NET Core, HttpClient, PostgreSQL) and exports OTLP to
  Jaeger, so a single order request shows as one trace across all services.
  Trace context is also propagated **through RabbitMQ** (W3C `traceparent` in the
  message headers, carried in the outbox row), so the async publish/consume hops
  join the same trace — not just the synchronous HTTP calls.
- **Transactional outbox** — Orders writes the `order-placed` event into an
  `Outbox` table in the **same transaction** as the order, then a background
  `OutboxDispatcher` publishes unsent rows to RabbitMQ and stamps them processed.
  This removes the dual-write race: the event is published **iff** the order was
  committed (at-least-once delivery), instead of "save, then hope the publish
  also succeeds."
- **Versioned schema via EF Core migrations** — the schema lives in source
  control under each service's `Migrations/`, and every service applies pending
  migrations on startup (`db.Database.MigrateAsync()`). A design-time
  `IDesignTimeDbContextFactory` lets `dotnet ef` build the context without
  running app startup.
- **Health checks** — each service exposes `/health` that verifies its real
  dependencies (Catalog → its database; Orders → its database *and* RabbitMQ).
  Docker Compose gates startup on them: databases and the broker must report
  **healthy** before the services start, and the services before the gateway
  (`depends_on: condition: service_healthy`).
- **Resilience** — the Orders→Catalog HTTP client uses
  `AddStandardResilienceHandler()` (Polly): retries, a circuit breaker, and
  timeouts, so a transient Catalog blip doesn't instantly fail an order.
- **OpenAPI docs** — every service publishes an OpenAPI document with an
  interactive Scalar UI.
- **Automated tests** (`dotnet test`):
  - *Unit* — pure domain logic (`OrderFactory`) with no I/O.
  - *Integration* — the Catalog service booted via `WebApplicationFactory`
    against a **real PostgreSQL** spun up by **Testcontainers**.
- **CI** — GitHub Actions builds and tests the .NET solution and builds the
  Angular app on every push (badge above).

## Run without Docker (for local dev)

You can also run each project directly with `dotnet run` — but you'd need a local
Postgres and RabbitMQ. The `appsettings.json` files default to `localhost` for
exactly this. Docker Compose is the intended way to run the whole platform.

## What this shows in an interview

- **Bounded contexts + service-per-database** (the hardest part to get right)
- **API gateway** as a single entry point for cross-cutting concerns
- **Both communication styles** and *when* to use each
- **Failure-aware** — health checks, resilience policies, and health-gated
  startup ordering
- **Tested** — unit + integration tests (Testcontainers) running in CI
- **Clean, layered .NET** with minimal APIs, EF Core, typed HTTP clients, and a
  background worker

Next steps a reviewer might expect: a shared contracts library for events,
metrics + dashboards (Prometheus/Grafana), and per-service authentication at the
gateway.
