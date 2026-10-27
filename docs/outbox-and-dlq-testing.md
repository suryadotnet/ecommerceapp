# Testing the Choreography Saga, Outbox, and Dead-Letter Queues

The end-to-end saga uses the `ordersvctopic` topic:

```text
OrderCreated -> InventoryReserved -> PaymentSucceeded -> Order Confirmed
                            \-> PaymentFailed
                                 |-> InventoryReleased
                                 \-> Order Failed
```

Each service persists business changes and outgoing events in one local database transaction. Its outbox dispatcher sends messages to Service Bus; consumers use inbox receipts and idempotent state transitions to tolerate redelivery.

## Prerequisites

- SQL Server and the OrderDb, InventoryDb, and PaymentDb connection strings are available.
- The Azure Service Bus namespace contains topic `ordersvctopic` and these subscriptions:
  - `inventorysvcsubscription`: `EventType = 'OrderCreated' OR EventType = 'PaymentFailed'`
  - `paymentsvcsubscription`: `EventType = 'InventoryReserved'`
  - `ordersvctopicsubscription`: `EventType = 'PaymentSucceeded' OR EventType = 'PaymentFailed' OR EventType = 'InventoryReleased'`
- Remove each subscription's default match-all rule.
- Each message must set the `EventType` application property. The application also sets the Service Bus subject to the event name.
- OrderService, InventoryService, and PaymentService have appropriate Service Bus send/receive permissions and configuration.

In Azure, prefer Managed Identity with least-privilege Azure Service Bus Data Sender and Data Receiver roles. For local development, use a Service Bus connection string from user secrets or an environment variable; do not commit credentials. Configure `ConnectionStrings__ServiceBus` or `ServiceBus__Namespace` for each service. When using namespace-based authentication locally, sign in with an Azure identity that has the required data-plane roles.

Start OrderService, InventoryService, and PaymentService. Confirm each logs that its consumer started and its outbox dispatcher has Service Bus settings. If a service reports an authorization or connection error, resolve it before testing.

## Test successful payment

Create an order whose total is below `99999`:

```bash
curl -i -X POST http://localhost:5001/api/orders \
  -H 'Content-Type: application/json' \
  -d '{
    "customerId": "11111111-1111-1111-1111-111111111111",
    "items": [{
      "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "quantity": 2,
      "unitPrice": 100
    }]
  }'
```

The order is created as `Pending`. Save its `orderId`, then poll its state:

```bash
curl http://localhost:5001/api/orders/<ORDER_ID>
```

After messages are processed, the expected order status is `Confirmed`. Inventory's available quantity decreases and reserved quantity increases; PaymentDb records a processed payment.

Check the outbox rows:

```sql
SELECT Id, Subject, DestinationName, Attempts, ProcessedAtUtc, LastError
FROM dbo.OutboxMessages
ORDER BY CreatedAtUtc DESC;

SELECT Id, Subject, DestinationName, Attempts, ProcessedAtUtc, LastError
FROM dbo.InventoryOutboxMessages
ORDER BY CreatedAtUtc DESC;

SELECT Id, Subject, DestinationName, Attempts, ProcessedAtUtc, LastError
FROM dbo.PaymentOutboxMessages
ORDER BY CreatedAtUtc DESC;
```

The respective rows should include `OrderCreated`, `InventoryReserved`, and `PaymentSucceeded`, with `ProcessedAtUtc` set after dispatch.

## Test payment failure and compensation

Create an order with a total of `99999` or greater. PaymentService intentionally declines this amount in the demo:

```bash
curl -i -X POST http://localhost:5001/api/orders \
  -H 'Content-Type: application/json' \
  -d '{
    "customerId": "11111111-1111-1111-1111-111111111111",
    "items": [{
      "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "quantity": 1,
      "unitPrice": 100000
    }]
  }'
```

The expected final order status is `Failed`; inventory is released and its available quantity is restored. Check that the Payment outbox contains `PaymentFailed`, and the Inventory outbox contains `InventoryReleased`. InventoryDb's `InventoryReservations` row for the order should have `IsReleased = 1`.

## Test duplicate handling

Redeliver an event or publish a duplicate `OrderCreated` for the same order with a different Service Bus message ID and `EventType = OrderCreated`. Inventory should not reserve the stock a second time. Replaying a `PaymentFailed` after compensation should not release stock twice.

## Test dead-letter handling

Send test events to topic `ordersvctopic` through Service Bus Explorer. Set `EventType` to a value accepted by the target subscription filter so the message is delivered to the consumer.

- For the Inventory subscription, set `EventType = OrderCreated`, subject `WrongSubject`, and body `{}`. It should be dead-lettered with reason `InvalidEventType`.
- For the Payment subscription, set `EventType = InventoryReserved`, subject `InventoryReserved`, and body `not-json`. It should be dead-lettered with reason `InvalidPayload`.
- For the Order subscription, set `EventType = PaymentFailed`, subject `PaymentFailed`, and body `not-json`. It should be dead-lettered with reason `InvalidPayload`.

Transient database or processing errors are abandoned for retry. Service Bus moves messages to the subscription's dead-letter queue after its configured maximum delivery count. Inspect active and dead-letter counts in the matching subscription.

## Troubleshooting

- **Outbox row remains pending:** Check that the service has database connectivity, Service Bus sender permissions, the correct topic name, and no persistent failure shown in `LastError`.
- **Subscription backlog grows:** Check its receiver identity, subscription name, SQL filter, and Service Bus network access.
- **Order stays Pending:** Follow the `OrderCreated`, `InventoryReserved`, and payment outcome outbox rows in order; inspect each subscription's active and dead-letter messages.
- **Order is Failed but stock remains reserved:** Check `PaymentFailed` delivery and InventoryService logs, then inspect the Inventory subscription DLQ and `InventoryReservations`.
- **InventoryReleased is not seen by OrderService:** Check the Order subscription's filter includes `EventType = 'InventoryReleased'` and the Inventory outbox row was dispatched.

Use unique message IDs for manual tests. Tests publish to the real topic and can affect consumers sharing these subscriptions.
