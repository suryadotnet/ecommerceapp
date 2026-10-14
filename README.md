# E-Commerce Microservices — .NET 10 Demo

A teaching/demo project showing **Microservices + CQRS + Saga orchestration + separate databases + transactional outbox messaging**.

## Services

- OrderService — port 5001 — owns `OrderDb`
- InventoryService — port 5002 — owns `InventoryDb`
- PaymentService — port 5003 — owns `PaymentDb`

Order and payment events are published asynchronously through Azure Service Bus. Checkout's inventory reservation and payment processing still use synchronous HTTP.

## Architecture

```text
Client
  |
  v
OrderService (CQRS + Saga Orchestrator)
  | HTTP
  +--------------------> InventoryService -> InventoryDb
  |                         |
  |                         +-- reserve / release
  |
  +--------------------> PaymentService -> PaymentDb
                            |
                            +-- process / refund

OrderDb is accessed only by OrderService.
InventoryDb is accessed only by InventoryService.
PaymentDb is accessed only by PaymentService.
```

## Outbox and Service Bus consumers

OrderService writes each outgoing event to `OutboxMessages` in the same OrderDb save as the order or payment event. A background dispatcher retries pending rows and sends them to:

- Queue `ordersvcqueue` with subject `OrderCreated`
- Topic `ordersvctopic` with subject `PaymentInfo`

The InventoryService background consumer listens to `ordersvcqueue` and the `ordersvctopicsubscription` subscription on `ordersvctopic`. It stores received events in `OrderCreatedReceipts` and `PaymentInfoReceipts`; the message ID is the primary key, so redelivered events are recorded only once. These consumers record events; inventory is still reserved by the existing synchronous checkout flow.

Create the queue, topic, and topic subscription in the Azure Service Bus namespace before running the services. Configure either `ConnectionStrings:ServiceBus` with a Service Bus connection string, or `ServiceBus:Namespace` with Managed Identity access. Grant OrderService send access and InventoryService listen access.

For Docker Compose, provide `SERVICEBUS_CONNECTION_STRING` or `SERVICEBUS_NAMESPACE` in the environment before starting the services. If neither is configured, both background services log a warning and remain disabled; outbox events stay persisted until Service Bus is configured.

See [Outbox and DLQ testing notes](./docs/outbox-and-dlq-testing.md) for end-to-end delivery checks and dead-letter test cases.

## Saga

Happy path:

1. Create order in `Pending` state.
2. Reserve inventory over HTTP.
3. Process payment over HTTP.
4. Confirm order.

Failure path:

1. Create order.
2. Reserve inventory.
3. Payment fails.
4. Call InventoryService `/api/inventory/release` as compensation.
5. Cancel order.

## Run with Docker Compose

Prerequisites: Docker Desktop.

```bash
docker compose up --build
```

APIs:

- OrderService: http://localhost:5001
- InventoryService: http://localhost:5002
- PaymentService: http://localhost:5003

SQL Server:

- localhost:1433
- User: sa
- Password: `Your_strong_password123!`

The compose file creates one SQL Server instance with three logically separate databases. Each microservice has its own connection string and EF Core DbContext.

## Run locally

Prerequisites: .NET 10 SDK and SQL Server.

Update connection strings in each service's `appsettings.json`, then:

```bash
dotnet restore

dotnet run --project src/OrderService
```

Run InventoryService and PaymentService separately as well.

For local Service Bus access, configure `ConnectionStrings__ServiceBus` or `ServiceBus__Namespace` in the process environment for both OrderService and InventoryService.

## Demo requests

### 1. Happy path

```http
POST http://localhost:5001/api/orders
Content-Type: application/json

{
  "customerId": "11111111-1111-1111-1111-111111111111",
  "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "quantity": 2,
  "amount": 2000
}
```

### 2. Force payment failure

Use an amount of `99999` or greater. PaymentService intentionally declines that amount for the training demo.

```http
POST http://localhost:5001/api/orders
Content-Type: application/json

{
  "customerId": "11111111-1111-1111-1111-111111111111",
  "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "quantity": 1,
  "amount": 100000
}
```

The expected Saga is:

```text
Order Created
    |
    v
Inventory Reserved
    |
    v
Payment Failed
    |
    v
Release Inventory
    |
    v
Order Cancelled
```

## CQRS endpoints

- Command: `POST /api/orders`
- Query: `GET /api/orders/{id}`

The command side changes order state. The query side reads it.

## Teaching points

1. Database-per-service means no service queries another service's database.
2. HTTP calls are synchronous and easy to understand first.
3. Saga is not a distributed SQL transaction; it is a sequence of local transactions plus compensating actions.
4. CQRS separates intent to change state from reading state.
5. Later, HTTP calls can be replaced by Azure Service Bus messages without changing the business concepts.
