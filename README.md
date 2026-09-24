# Shop Platform — Microservices Example (.NET 10)

A minimal but realistic microservices e-commerce platform, built to demonstrate
the core patterns interviewers ask about: **service-per-database**, an **API
gateway**, **synchronous** service-to-service HTTP calls, and **asynchronous**
event-driven messaging.

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
                              │ GET /products/{id}│  (sync HTTP: "is this product real?")
                              └────────◀──────────┘
                                                 │ publishes "order-placed"
                                                 ▼
                                           ┌──────────┐      ┌──────────────┐
                                           │ RabbitMQ │ ───▶ │ Shipping svc │ (async consumer)
                                           └──────────┘      └──────────────┘
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

Open **http://localhost:8080**. Click **Order** on any product. The new order
appears in the Orders panel, and the **shipping** container logs a
`📦 Preparing shipment...` line — proving the async path end-to-end (Orders
never called Shipping directly).

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

## Run without Docker (for local dev)

You can also run each project directly with `dotnet run` — but you'd need a local
Postgres and RabbitMQ. The `appsettings.json` files default to `localhost` for
exactly this. Docker Compose is the intended way to run the whole platform.

## What this shows in an interview

- **Bounded contexts + service-per-database** (the hardest part to get right)
- **API gateway** as a single entry point for cross-cutting concerns
- **Resilient startup** — services retry their DB / broker connections because
  containers boot in parallel
- **Both communication styles** and *when* to use each
- **Clean, layered .NET** with minimal APIs, EF Core, typed HTTP clients, and a
  background worker

Next steps a reviewer might expect: health-check-based `depends_on`, a shared
contracts library for events, an outbox pattern for reliable publishing, and
per-service authentication at the gateway.
