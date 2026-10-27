# E-Commerce Microservices — .NET 10 Demo

A teaching/demo project showing **Microservices + CQRS + Saga choreography + separate databases + transactional outbox messaging**.

## Services

- OrderService — port 5001 — owns `OrderDb`
- InventoryService — port 5002 — owns `InventoryDb`
- PaymentService — port 5003 — owns `PaymentDb`

Order, inventory, and payment events are published asynchronously through Azure Service Bus. Each service owns its local database transaction and outgoing-event outbox.

## Timer Function

`src/EcommTimerFunctions` is a .NET 8 isolated Azure Functions app. Its `TimerFunction` runs at second 0 of every two-minute interval (`0 */2 * * * *`) and logs its execution time. The schedule uses UTC unless the Function App is configured with a supported time zone. The timer requires working storage in both environments: locally, `local.settings.json` uses Azurite through `UseDevelopmentStorage=true`; in Azure, the Function App must have a valid `AzureWebJobsStorage` application setting.

To run locally, start Azurite in one terminal (Docker must be running):

```bash
docker run --rm --name ecommerce-azurite \
  -p 10000:10000 -p 10001:10001 -p 10002:10002 \
  mcr.microsoft.com/azure-storage/azurite
```

Then, from `src/EcommTimerFunctions`, start the Functions host:

```bash
func start
```

Keep Azurite running while the Functions host is running. If the host reports connection failures to `127.0.0.1:10000`, Azurite is not reachable and the timer listener cannot start.

For Azure, set `AzureWebJobsStorage` on the Function App to the connection string of a reachable Azure Storage account, and set `FUNCTIONS_WORKER_RUNTIME` to `dotnet-isolated` and `FUNCTIONS_EXTENSION_VERSION` to `~4`. Check the Function App's logs for the timer listener and its scheduled execution; the timer will not run while storage is missing or inaccessible.

### HTTP inbound to Service Bus outbound

`InboundMessageFunction` accepts a `POST` at `/api/messages` and sends the raw request body to the Service Bus queue `inbound-messages`. It returns `202 Accepted` after the output binding has queued the message; an empty body returns `400 Bad Request`.

Create the `inbound-messages` queue and configure the `ServiceBusConnection` setting with a connection string that has send permission. Locally, set it from `src/EcommTimerFunctions` with:

```bash
func settings add ServiceBusConnection "<Service Bus connection string>"
```

In Azure, add `ServiceBusConnection` as an application setting on the Function App. This example allows anonymous HTTP requests; call it with:

```bash
curl -X POST "http://localhost:7071/api/messages" \
  -H "Content-Type: application/json" \
  -d '{"event":"example","id":123}'
```

The same endpoint is available at the deployed Function App URL. The Function host must also have the storage configuration described above.

### Service Bus queue trigger

`src/EcommAzureFunctions` includes `ServiceBusQueueTriggerFunction`, which listens to the queue named by `ServiceBusQueueName` using the `ServiceBusConnection` setting. It passes the message body to the shared `GreetingService` and logs the greeting with the message ID and subject. `HelloFunction` uses the same service to produce its HTTP response. The Service Bus Functions extension completes a message after the function returns successfully; failed invocations are retried by the host and eventually dead-lettered according to the queue's configured delivery limit.

Create the queue in your Service Bus namespace and configure a connection string with **Listen** permission. For local development, from `src/EcommAzureFunctions`, set the ignored local setting:

```bash
func settings add ServiceBusConnection "<Service Bus connection string>"
```

The local `ServiceBusQueueName` is already set to `inbound-messages`; override it if you use a different queue. Keep `AzureWebJobsStorage` configured as described above. In Azure, add `ServiceBusConnection` and `ServiceBusQueueName` as Function App application settings. Do not commit the connection string.

### Blob trigger for product images

`BlobTriggerFunc` watches `product-images/{name}` in the `ordersvcblob` storage account using the `BlobStorageConnection` setting. It reads the uploaded image stream and logs the blob name and size, not the binary contents. Keep `AzureWebJobsStorage` configured separately for the Functions host (locally it can use Azurite).

For local development, configure the storage account connection string in the ignored local settings file. Copy it from the `ordersvcblob` storage account's **Access keys** page, then run this from `src/EcommAzureFunctions`:

```bash
func settings add BlobStorageConnection "<ordersvcblob connection string>"
```

Restart the Functions host after setting it. Do not commit or share this connection string. In Azure, prefer managed identity: enable the Function App's system-assigned identity, assign it **Storage Blob Data Owner** and **Storage Queue Data Contributor** roles on `ordersvcblob`, and add these application settings:

```text
BlobStorageConnection__blobServiceUri=https://ordersvcblob.blob.core.windows.net
BlobStorageConnection__queueServiceUri=https://ordersvcblob.queue.core.windows.net
```

Ensure the `product-images` container exists. Start the Functions host and upload a blob to that container; the function should log its name and size. The Azure configuration uses managed identity, so no Blob Storage secret is required in Function App settings.

## Architecture

```text
Client
  |
  v
OrderService (creates pending orders; consumes payment outcomes)
  | OrderCreated
  v
InventoryService (reserves stock; consumes PaymentFailed)
  | InventoryReserved                 ^ PaymentFailed
  v                                   |
PaymentService (processes payment) ---+
  | PaymentSucceeded / PaymentFailed
  v
OrderService (confirms or fails order)

InventoryService publishes InventoryReleased after compensation.

OrderDb is accessed only by OrderService.
InventoryDb is accessed only by InventoryService.
PaymentDb is accessed only by PaymentService.
```

## Outbox and Service Bus consumers

Each service writes its outgoing messages to its own outbox in the same database transaction as its business update. Outbox dispatchers retry pending messages and send them to topic `ordersvctopic`. Messages set both the subject and the `EventType` application property to the event name; subscription SQL rules use that property.

Create one subscription per consumer and remove the default match-all rule:

| Subscription | SQL filter |
| --- | --- |
| `inventorysvcsubscription` | `EventType = 'OrderCreated' OR EventType = 'PaymentFailed'` |
| `paymentsvcsubscription` | `EventType = 'InventoryReserved'` |
| `ordersvctopicsubscription` | `EventType = 'PaymentSucceeded' OR EventType = 'PaymentFailed' OR EventType = 'InventoryReleased'` |

Configure either `ConnectionStrings:ServiceBus` with a Service Bus connection string, or `ServiceBus:Namespace` with Managed Identity access. OrderService and InventoryService need sender access; all services need receiver access for their subscription; InventoryService and PaymentService need sender access as well. Prefer managed identities and least-privilege Azure Service Bus Data Sender/Receiver roles in Azure.

For Docker Compose, provide `SERVICEBUS_CONNECTION_STRING` in the local `.env` file. It must allow the services to send and receive messages from the configured topic/subscriptions and allow the Functions app to listen to its configured queue and subscription.

See [Outbox and DLQ testing notes](./docs/outbox-and-dlq-testing.md) for end-to-end delivery checks and dead-letter test cases.

## Saga

Success path: `OrderCreated` → `InventoryReserved` → `PaymentSucceeded` → OrderService marks the order `Confirmed`.

Payment failure path: `OrderCreated` → `InventoryReserved` → `PaymentFailed`; InventoryService releases the reservation and publishes `InventoryReleased`, while OrderService marks the order `Failed`.

## Run with Docker Compose

Prerequisites: Docker Desktop with Docker Compose, an Azure Service Bus namespace with the topic/subscriptions and queue described above, and the `ordersvcblob` Storage account with the `product-images` container.

Copy `.env.example` to `.env` and fill in the SQL password, Entra tenant/API client IDs, and connection strings. Do not commit `.env`; it contains secrets.

```bash
cp .env.example .env
# Edit .env, then:
docker compose up --build
```

APIs:

- OrderService: http://localhost:5001
- InventoryService: http://localhost:5002
- PaymentService: http://localhost:5003
- EcommAzureFunctions `HelloFunction`: http://localhost:7071/api/HelloFunction

SQL Server:

- localhost:1433
- User: sa
- Password: the `SQL_SA_PASSWORD` value in `.env`

The Compose stack builds and runs the three .NET APIs, the isolated-worker Azure Functions app, and SQL Server. The Function host uses the configured Storage account for its host state and blob trigger. Service Bus and Blob Storage connect to the Azure resources provided in `.env`; the stack does not emulate those services locally. `docker compose down` stops the containers; `docker compose down -v` also deletes the local SQL Server data volume.

## Run locally

Prerequisites: .NET 10 SDK and SQL Server.

### Entra ID authentication for OrderService

All OrderService HTTP endpoints require a valid Microsoft Entra ID access token issued by the configured tenant for the OrderService API. Requests without a bearer token or with a token for a different tenant/audience receive `401 Unauthorized`. Swagger can sign in and obtain an access token using its **Authorize** button with OAuth 2.0 authorization code flow and PKCE.

Configure the API app registration and allow Swagger to sign in:

1. Register OrderService in Microsoft Entra ID as a single-tenant application.
2. Under **Expose an API**, use the Application ID URI `api://<ORDER_API_CLIENT_ID>` and add a delegated scope named `access_as_user`.
3. Under **Authentication**, add the **Single-page application (SPA)** platform and register Swagger's redirect URI. With the default local URL this is `http://localhost:5001/oauth2-redirect.html`; use the exact scheme, host, port, and path where Swagger is served.
4. In **API permissions**, add the delegated permission `api://<ORDER_API_CLIENT_ID>/access_as_user` and grant consent as required by your tenant. Swagger uses the API app registration's client ID as its OAuth client ID by default, so no separate client application is required. Optionally set `AzureAd__SwaggerClientId` to a separate SPA app registration's client ID instead.
5. Set `AzureAd__TenantId` to the tenant ID and `AzureAd__ClientId` to the OrderService API app registration's client ID. These values are required at startup. Configure them as environment variables for local development and as App Service application settings in Azure; neither is a client secret.
6. Open Swagger, click **Authorize**, sign in with a tenant user, and grant consent if prompted. Swagger requests `api://<ORDER_API_CLIENT_ID>/access_as_user` and attaches the resulting access token to API calls.

For calls outside Swagger, request an access token for that scope and send it as a bearer token:

```bash
curl -H "Authorization: Bearer $ACCESS_TOKEN" \
  http://localhost:5001/api/orders
```

Do not use an ID token to call the API; the client must request an access token for the OrderService API audience. The development Swagger UI remains available for local diagnostics, but all mapped OrderService endpoints, including health endpoints, require authentication.

Update connection strings in each service's `appsettings.json`, then:

```bash
dotnet restore

dotnet run --project src/OrderService
```

Run InventoryService and PaymentService separately as well.

For local Service Bus access, configure `ConnectionStrings__ServiceBus` or `ServiceBus__Namespace` in the process environment for each service. The configured subscriptions must exist in Azure Service Bus.

## Demo requests

### 1. Happy path

```http
POST http://localhost:5001/api/orders
Content-Type: application/json

{
  "customerId": "11111111-1111-1111-1111-111111111111",
  "items": [
    {
      "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "quantity": 2,
      "unitPrice": 2000
    }
  ]
}
```

### 2. Force payment failure

Use an order total of `99999` or greater. PaymentService intentionally declines that amount for the training demo.

```http
POST http://localhost:5001/api/orders
Content-Type: application/json

{
  "customerId": "11111111-1111-1111-1111-111111111111",
  "items": [
    {
      "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "quantity": 1,
      "unitPrice": 100000
    }
  ]
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
Order Failed
```

## CQRS endpoints

- Command: `POST /api/orders`
- Query: `GET /api/orders/{id}`

The command side changes order state. The query side reads it.

## Teaching points

1. Database-per-service means no service queries another service's database.
2. Saga choreography uses events rather than a central orchestrator.
3. A saga is not a distributed SQL transaction; it is a sequence of local transactions plus compensating actions.
4. CQRS separates intent to change state from reading state.
5. Transactional outboxes, inbox receipts, and idempotent compensations handle message delivery retries safely.
