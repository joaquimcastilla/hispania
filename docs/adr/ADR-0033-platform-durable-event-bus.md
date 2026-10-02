# ADR-0033: Platform durable event bus

Status: Accepted (Phase 13.4 V1).

## Decision

Keep `LocalPlatformEventBus` as the immediate in-process publisher. Add a separate Platform V1 durable store contract with a SQLite implementation owned by Platform persistence. The server composes both. Inventory's `inventory.stock-changed@1` event is persisted for the stable subscriber `platform.inventory.stock-observer`; Inventory references Platform contracts only.

Each event uses a random GUID, a stable event type/version, UTC occurrence time, producer, optional correlation and causation identifiers, and bounded UTF-8 JSON. The application registers concrete event codecs/consumers by stable event type, version and subscriber ID; CLR type names from persisted data are never loaded or instantiated.

SQLite stores Events, Subscribers and Deliveries in a separate `events.sqlite` file. Event plus initial deliveries are inserted in one transaction using the subscriber snapshot supplied at publication. New subscribers do not retroactively receive old events. One dispatcher per store is supported; the process-level semaphore prevents concurrent dispatch in one host. There is no multi-process coordinator or global ordering guarantee.

## Delivery and recovery

Durable mode is **at-least-once**. A consumer may observe the same event again if it completed an external side effect before the process crashed and before Delivered was persisted. Consumers should use EventId with a processed-event record or a domain invariant when the effect needs idempotency. Exactly-once, distributed transactions and cross-domain atomicity are not provided.

Each subscriber has independent delivery state. Failed attempts return to Pending with exponential backoff; exhausted attempts become DeadLettered with a bounded error summary. Processing rows carry a lease timestamp; an expired lease returns to Pending after restart. Dead-letter replay explicitly resets that delivery's current attempt state and retains its DeliveryId/event history. A separate durable attempt journal records each completed delivery attempt across replay.

Correlation is carried through EventContext; causation is the triggering command/event identifier when supplied. No new correlation should be generated when request context already exists. No global ordering is promised. Retention/compaction is an operator policy for a later maintenance facility; V1 does not delete durable events automatically.

## Boundaries and limits

The SQLite schema and connection details stay in Platform persistence. Media and Inventory are not references of Platform contracts/application/persistence. `LocalPlatformEventBus` remains volatile/immediate; Durable Platform Events persist and dispatch on a background loop. This phase adds no Background Jobs, WorkerAgent integration, external broker, multi-node coordination, exactly-once, event sourcing, or workflow engine.
