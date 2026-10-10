# ADR-0036: Platform Storage Provider Abstraction

- Status: Accepted for Phase 13.7 implementation
- Date: 2026-10-03

## Context

Platform Assets currently combines metadata persistence with a filesystem content implementation. Assets need to retain ownership of asset semantics while allowing the physical byte store to be replaced behind a stable Platform boundary. Inventory must continue to store only opaque `AssetId` references, and Media must continue to resolve Platform content through the Assets API.

## Decision

`Hispania.Platform.Contracts` owns the versioned `IStorageProviderV1`, `IStorageProviderResolverV1`, `StorageObjectHandle`, and technical `StorageObjectMetadata` contracts. Provider identity is stable and belongs to the provider. The host explicitly composes a `StorageProviderRegistryV1`; `Storage:DefaultProvider` selects the provider for new writes. The current provider is `filesystem`, configured by `Storage:Providers:Filesystem:Root` (with the former Platform Assets content path retained as a configuration fallback).

New handles have the form `storage://<provider-id>/<opaque-id>`. The filesystem provider generates random IDs internally and derives paths only under its configured root. It never treats an incoming handle as a path. Assets persists the provider ID, handle, byte size, MIME type, and existing semantic metadata. Storage does not decide MIME semantics. No HTTP response includes a physical path, storage root, or temporary filename.

Assets writes bytes first, then persists metadata. If metadata persistence fails, Assets tries to delete the just-written object; a failed compensation may leave an orphan object. Delete removes metadata first and then content; if content deletion fails, the object may be orphaned but is no longer addressable as an Asset. SQLite and a filesystem cannot share an ACID transaction, so this is explicit best-effort compensation, not transactionality. A maintenance cleanup job is outside this phase.

## Compatibility and security

The SQLite migration adds `provider_id` with the legacy value `filesystem`, preserving old rows and their existing hex content handles. The filesystem provider accepts that old format for reading/deletion while all new writes use the URI form. No bulk byte migration is performed. A future filesystem-to-object-store move must copy/verify content, update metadata safely, retain rollback/cleanup policy, and handle mixed provider versions; this phase does not implement a migration engine.

The filesystem provider validates the scheme/provider/opaque ID, rejects rooted paths, separators, traversal, malformed and percent-encoded handles, creates unique temporary files, streams within the configured byte limit, restricts Unix file mode when available, and publishes with a same-root rename. It rejects symlink/reparse-point object files. The configured root and host process identity remain trusted; this is not a filesystem sandbox, and portable race-free defenses against a hostile process with write access to the root are not claimed. File permissions and safety also depend on host OS/user configuration.

Unknown providers fail explicitly and HTTP content endpoints return service unavailable; missing objects return not found and are not recreated. Normal reads are not audited. Asset create/delete audit and existing `platform.assets.write` authorization remain. Inventory stores only `AssetReference`; Media playback streams through the Assets store. WorkerAgent does not receive local paths or storage access.

## Consequences and limitations

Providers store bytes plus optional technical metadata, not MIME policy. The registry supports multiple explicitly supplied providers without auto-discovery. Size is streamed and bounded; no whole-file buffering or mandatory content hash is imposed. Health is a read-only root availability check and does not perform a destructive probe. Orphan cleanup, retention, S3/Azure/GCS/MinIO, replication, encryption at rest, deduplication, object versioning and remote worker transfer are future work.
