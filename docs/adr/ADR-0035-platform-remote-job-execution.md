# ADR-0035: Platform Remote Job Execution via WorkerAgent

Status: Accepted (Phase 13.6 V1).

## Decision and ownership

Platform Jobs remains the durable source of truth for Job state, retry policy, attempts, cancellation intent, and accepted results. Each Job keeps its ordinary Pending/Running/terminal state regardless of executor. V1 adds `LocalOnly`, `RemotePreferred`, and `RemoteOnly` policy plus remote execution metadata on Job/Attempt; it does not introduce remote-only Job states.

The Server composition root connects the Jobs dispatcher to `DistributedPlatformJobExecutor` in `infrastructure/platform/worker-agent-jobs`. The adapter depends on the public Jobs and Runtime/Worker contracts. It does not reference WorkerAgent implementation, Jobs SQLite persistence, Media, Inventory, or FFmpeg. WorkerAgent remains a generic host and loads `PlatformJobsCapabilityProviderV1` only from the operator-configured trusted provider assembly list.

## Capability and protocol mapping

V1 maps `platform.test.hash` to `platform.jobs.hash@1.0`, operation `Execute`. The request contains a bounded JSON wrapper around the registered Job payload, correlation ID, and optional timeout. It never contains a CLR type name, assembly name, executable, shell command, or path. The WorkerAgent provider invokes the known Platform hash contract and returns bounded metadata.

Remote work reuses the existing WorkerAgent connect/heartbeat/lease/start/complete protocol, authorized worker capability lists, mTLS certificate identity, and single-coordinator generation fencing. This phase adds no worker registration table, transport, authentication scheme, lease state machine, or scheduler. The existing coordinator still has one server owner protected by its persisted coordinator lock.

## Policies and selection

- `LocalOnly` is acquired by the local dispatcher and never submitted to WorkerAgent.
- `RemotePreferred` uses an authorized, connected, healthy worker with the exact capability/version when capacity is available. If none can claim the queued request during the bounded preference window, the coordinator request is cancelled and local execution is used. If a worker has already claimed it, the dispatcher waits for that attempt rather than racing a local duplicate.
- `RemoteOnly` requires a registered remote JobType-to-capability mapping. While there is no compatible authorized worker with capacity, acquisition filters the Job out so it remains Pending. It never falls back locally.

The worker selector considers coordinator authorization/enabled state, connection and recent heartbeat, authorized capability, advertised capability/version, and current capacity. User RBAC remains separate from worker certificate/capability authorization.

## Attempts, fencing, and recovery

Platform attempt metadata records executor kind, worker ID, capability/version, coordinator generation, and a nonsecret lease identifier. The coordinator validates worker ID and generation when accepting results; a stale generation receives conflict and cannot replace coordinator state. The Jobs SQLite completion update also compares Job ID, Running state, and attempt number, so a late completion cannot overwrite a newer/recovered attempt.

The Job payload and attempt history remain durable in `jobs.sqlite`; coordinator lease state remains in the existing distributed-work store. A process or worker crash may repeat a handler side effect after lease recovery. Delivery remains **at-least-once**, never exactly-once. Retry-safe Platform Jobs declare no external side effects to the coordinator; unsafe work is not submitted remotely by V1.

Coordinator restart recovers eligible retry-safe distributed requests through its existing generation mechanism. Worker restart/reconnect and expired leases can cause the request to run again. Jobs owns final status/retry decisions and only commits a result while its current attempt remains Running.

## Cancellation, timeout, and payload safety

Running cancellation first persists Job cancellation intent, then cancels the coordinator request. WorkerAgent heartbeat delivers cooperative cancellation to the provider token. A noncooperative handler cannot be forcibly killed; its late result is rejected by coordinator state/generation and the Jobs attempt compare-and-set. Job timeout is also carried in the Runtime request and applied cooperatively by WorkerAgent; timeout does not promise immediate process termination.

V1 uses bounded inline JSON payloads and small result metadata only. Large content must use a future opaque Asset reference; no Media transfer endpoint is used. `OpaqueResultRef` remains available in the result contract, but storage integration is out of scope.

## Security and limits

Only operator-installed trusted providers and registered JobTypes/capability mappings execute. Worker certificate identity and allowed capability are checked by the existing coordinator endpoints. No arbitrary code, shell, PowerShell, bash, executable path, DLL upload, or reflection-by-payload is permitted. Secrets from mTLS configuration are never included in Job metadata or results.

V1 remains a single-coordinator, at-least-once adapter. Multi-coordinator scheduling, consensus, sharding, workflows/DAGs, recurrence, autoscaling, remote Storage, large result transfer, dynamic provider download, and exactly-once execution are out of scope.
