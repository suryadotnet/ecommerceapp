# E-Commerce Microservices — .NET 10 Demo

A teaching/demo project showing **Microservices + CQRS + HTTP Saga orchestration + separate databases**.

## Services

- OrderService — port 5001 — owns `OrderDb`
- InventoryService — port 5002 — owns `InventoryDb`
- PaymentService — port 5003 — owns `PaymentDb`

No Service Bus is used in this version. Inter-service communication is synchronous HTTP.

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
