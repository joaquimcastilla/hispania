# Phase 13 project dependency audit

Snapshot of every repository project reference. Package references are listed separately. Runtime assembly boundaries are enforced by PlatformDependencyTests, PlatformIdentityTests and the domain/worker architecture tests. Conditional references are included conservatively; composition roots and test fixtures may reference both domains.

## application/authorization/Hispania.Authorization.Application.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## application/identity/Hispania.Identity.Application.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## application/inventory/Hispania.Inventory.Application.csproj

Projects: `contracts/inventory/Hispania.Inventory.Contracts.csproj`, `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## application/library/Hispania.Library.Application.csproj

Projects: `contracts/library/Hispania.Library.Contracts.csproj`, `core/domain/Hispania.Media.Domain.csproj`.
Packages: none.

## application/metadata/Hispania.Metadata.Application.csproj

Projects: `contracts/metadata/Hispania.Metadata.Contracts.csproj`, `contracts/library/Hispania.Library.Contracts.csproj`, `core/domain/Hispania.Media.Domain.csproj`.
Packages: `Microsoft.Extensions.Logging.Abstractions`.

## application/platform/jobs/Hispania.Platform.Jobs.Application.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## application/platform/observability/Hispania.Platform.Observability.Application.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## application/platform/storage/Hispania.Platform.Storage.Application.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## application/playback/Hispania.Playback.Application.csproj

Projects: `contracts/playback/Hispania.Playback.Contracts.csproj`, `contracts/library/Hispania.Library.Contracts.csproj`, `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`.
Packages: none.

## apps/cli/Hispania.Cli.csproj

Projects: `contracts/metadata/Hispania.Metadata.Contracts.csproj`, `infrastructure/metadata/Hispania.Metadata.Tmdb/Hispania.Metadata.Tmdb.csproj`.
Packages: none.

## apps/ffmpeg-worker/Hispania.FFmpeg.Worker.csproj

Projects: `contracts/transcoding-worker/Hispania.Transcoding.Worker.Contracts.csproj`.
Packages: none.

## apps/module-host/Hispania.ModuleHost.csproj

Projects: `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`.
Packages: none.

## apps/server/Hispania.Server.csproj

Projects: `apps/module-host/Hispania.ModuleHost.csproj`, `contracts/architecture-test/Hispania.ArchitectureTest.Contracts.csproj`, `core/runtime/Hispania.Runtime.csproj`, `modules/architecture-test/Hispania.ArchitectureTest.Modules.csproj`, `contracts/extension-demo/Hispania.ExtensionDemo.Contracts.csproj`, `application/library/Hispania.Library.Application.csproj`, `application/playback/Hispania.Playback.Application.csproj`, `modules/playback-conservative/Hispania.Playback.Conservative.csproj`, `infrastructure/library/Hispania.Library.LocalFileSystem.csproj`, `infrastructure/ffprobe/Hispania.Library.FFprobe.csproj`, `infrastructure/transcoding-ffmpeg/Hispania.Transcoding.FFmpeg.csproj`, `apps/ffmpeg-worker/Hispania.FFmpeg.Worker.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.Sqlite/Hispania.Library.Persistence.Sqlite.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.EFCore/Hispania.Library.Persistence.EFCore.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.PostgreSql/Hispania.Library.Persistence.PostgreSql.csproj`, `contracts/library/Hispania.Library.Contracts.csproj`, `contracts/playback/Hispania.Playback.Contracts.csproj`, `core/domain/Hispania.Media.Domain.csproj`, `application/metadata/Hispania.Metadata.Application.csproj`, `infrastructure/metadata/Hispania.Metadata.Tmdb/Hispania.Metadata.Tmdb.csproj`, `infrastructure/persistence/distributed-work/Hispania.DistributedWork.Persistence.Sqlite.csproj`, `application/inventory/Hispania.Inventory.Application.csproj`, `infrastructure/persistence/inventory-sqlite/Hispania.Inventory.Persistence.Sqlite.csproj`, `contracts/inventory/Hispania.Inventory.Contracts.csproj`, `contracts/platform/Hispania.Platform.Contracts.csproj`, `application/platform/storage/Hispania.Platform.Storage.Application.csproj`, `infrastructure/platform/storage-filesystem/Hispania.Platform.Storage.Filesystem.csproj`, `application/platform/jobs/Hispania.Platform.Jobs.Application.csproj`, `infrastructure/platform/worker-agent-jobs/Hispania.Platform.WorkerAgentJobs.csproj`, `application/identity/Hispania.Identity.Application.csproj`, `application/authorization/Hispania.Authorization.Application.csproj`, `infrastructure/platform/persistence-sqlite/Hispania.Platform.Persistence.Sqlite.csproj`, `application/platform/observability/Hispania.Platform.Observability.Application.csproj`.
Packages: none.

## apps/worker-agent/Hispania.WorkerAgent.csproj

Projects: `core/runtime/Hispania.Runtime.csproj`, `contracts/extension-demo/Hispania.ExtensionDemo.Contracts.csproj`.
Packages: none.

## contracts/architecture-test/Hispania.ArchitectureTest.Contracts.csproj

Projects: none.
Packages: none.

## contracts/extension-demo/Hispania.ExtensionDemo.Contracts.csproj

Projects: none.
Packages: none.

## contracts/inventory/Hispania.Inventory.Contracts.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## contracts/library/Hispania.Library.Contracts.csproj

Projects: `core/domain/Hispania.Media.Domain.csproj`.
Packages: none.

## contracts/metadata/Hispania.Metadata.Contracts.csproj

Projects: `core/domain/Hispania.Media.Domain.csproj`.
Packages: none.

## contracts/module-system/Hispania.ModuleSystem.Contracts.csproj

Projects: none.
Packages: none.

## contracts/platform/Hispania.Platform.Contracts.csproj

Projects: none.
Packages: none.

## contracts/playback/Hispania.Playback.Contracts.csproj

Projects: `core/domain/Hispania.Media.Domain.csproj`.
Packages: none.

## contracts/transcoding-worker/Hispania.Transcoding.Worker.Contracts.csproj

Projects: `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`.
Packages: none.

## core/domain/Hispania.Media.Domain.csproj

Projects: none.
Packages: none.

## core/kernel/Hispania.Kernel.csproj

Projects: none.
Packages: none.

## core/runtime/Hispania.Runtime.csproj

Projects: `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`, `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## infrastructure/ffprobe/Hispania.Library.FFprobe.csproj

Projects: `contracts/library/Hispania.Library.Contracts.csproj`.
Packages: none.

## infrastructure/library/Hispania.Library.LocalFileSystem.csproj

Projects: `contracts/library/Hispania.Library.Contracts.csproj`, `contracts/playback/Hispania.Playback.Contracts.csproj`, `core/domain/Hispania.Media.Domain.csproj`.
Packages: none.

## infrastructure/metadata/Hispania.Metadata.Tmdb/Hispania.Metadata.Tmdb.csproj

Projects: `contracts/metadata/Hispania.Metadata.Contracts.csproj`.
Packages: none.

## infrastructure/persistence/Hispania.Library.Persistence.EFCore/Hispania.Library.Persistence.EFCore.csproj

Projects: `contracts/library/Hispania.Library.Contracts.csproj`, `contracts/metadata/Hispania.Metadata.Contracts.csproj`, `core/domain/Hispania.Media.Domain.csproj`.
Packages: `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Relational`.

## infrastructure/persistence/Hispania.Library.Persistence.PostgreSql/Hispania.Library.Persistence.PostgreSql.csproj

Projects: `infrastructure/persistence/Hispania.Library.Persistence.EFCore/Hispania.Library.Persistence.EFCore.csproj`, `contracts/library/Hispania.Library.Contracts.csproj`, `contracts/metadata/Hispania.Metadata.Contracts.csproj`.
Packages: `Microsoft.EntityFrameworkCore.Design`, `Npgsql.EntityFrameworkCore.PostgreSQL`.

## infrastructure/persistence/Hispania.Library.Persistence.Sqlite/Hispania.Library.Persistence.Sqlite.csproj

Projects: `infrastructure/persistence/Hispania.Library.Persistence.EFCore/Hispania.Library.Persistence.EFCore.csproj`, `contracts/metadata/Hispania.Metadata.Contracts.csproj`, `application/library/Hispania.Library.Application.csproj`.
Packages: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`.

## infrastructure/persistence/distributed-work/Hispania.DistributedWork.Persistence.Sqlite.csproj

Projects: `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`.
Packages: `Microsoft.Data.Sqlite`.

## infrastructure/persistence/inventory-sqlite/Hispania.Inventory.Persistence.Sqlite.csproj

Projects: `application/inventory/Hispania.Inventory.Application.csproj`.
Packages: `Microsoft.Data.Sqlite`.

## infrastructure/platform/persistence-sqlite/Hispania.Platform.Persistence.Sqlite.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`, `application/identity/Hispania.Identity.Application.csproj`, `application/authorization/Hispania.Authorization.Application.csproj`, `application/platform/jobs/Hispania.Platform.Jobs.Application.csproj`.
Packages: `Microsoft.Data.Sqlite`.

## infrastructure/platform/storage-filesystem/Hispania.Platform.Storage.Filesystem.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`.
Packages: none.

## infrastructure/platform/worker-agent-jobs/Hispania.Platform.WorkerAgentJobs.csproj

Projects: `contracts/platform/Hispania.Platform.Contracts.csproj`, `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`, `core/runtime/Hispania.Runtime.csproj`.
Packages: none.

## infrastructure/transcoding-ffmpeg/Hispania.Transcoding.FFmpeg.csproj

Projects: `contracts/playback/Hispania.Playback.Contracts.csproj`, `contracts/library/Hispania.Library.Contracts.csproj`, `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`, `contracts/transcoding-worker/Hispania.Transcoding.Worker.Contracts.csproj`, `core/runtime/Hispania.Runtime.csproj`.
Packages: none.

## modules/architecture-test/Hispania.ArchitectureTest.Modules.csproj

Projects: `contracts/architecture-test/Hispania.ArchitectureTest.Contracts.csproj`, `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`.
Packages: none.

## modules/extension-demo/Hispania.ExtensionDemo.csproj

Projects: `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`, `contracts/extension-demo/Hispania.ExtensionDemo.Contracts.csproj`.
Packages: none.

## modules/playback-conservative/Hispania.Playback.Conservative.csproj

Projects: `core/domain/Hispania.Media.Domain.csproj`, `contracts/library/Hispania.Library.Contracts.csproj`, `contracts/playback/Hispania.Playback.Contracts.csproj`, `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`.
Packages: none.

## tests/closure-process-host/Hispania.Closure.ProcessHost.csproj

Projects: `infrastructure/platform/persistence-sqlite/Hispania.Platform.Persistence.Sqlite.csproj`, `application/platform/jobs/Hispania.Platform.Jobs.Application.csproj`, `application/inventory/Hispania.Inventory.Application.csproj`.
Packages: none.

## tests/fakes/Hispania.FFmpeg.Worker.Fake/Hispania.FFmpeg.Worker.Fake.csproj

Projects: `contracts/transcoding-worker/Hispania.Transcoding.Worker.Contracts.csproj`.
Packages: none.

## tests/ffprobe/Hispania.Library.FFprobe.Tests.csproj

Projects: `infrastructure/ffprobe/Hispania.Library.FFprobe.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tests/inventory/Hispania.Inventory.Tests.csproj

Projects: `application/platform/jobs/Hispania.Platform.Jobs.Application.csproj`, `application/platform/storage/Hispania.Platform.Storage.Application.csproj`, `infrastructure/platform/storage-filesystem/Hispania.Platform.Storage.Filesystem.csproj`, `infrastructure/platform/worker-agent-jobs/Hispania.Platform.WorkerAgentJobs.csproj`, `apps/server/Hispania.Server.csproj`, `apps/worker-agent/Hispania.WorkerAgent.csproj`, `application/authorization/Hispania.Authorization.Application.csproj`, `application/identity/Hispania.Identity.Application.csproj`, `application/inventory/Hispania.Inventory.Application.csproj`, `infrastructure/persistence/inventory-sqlite/Hispania.Inventory.Persistence.Sqlite.csproj`, `infrastructure/platform/persistence-sqlite/Hispania.Platform.Persistence.Sqlite.csproj`, `core/runtime/Hispania.Runtime.csproj`, `core/domain/Hispania.Media.Domain.csproj`, `application/library/Hispania.Library.Application.csproj`, `application/metadata/Hispania.Metadata.Application.csproj`, `application/playback/Hispania.Playback.Application.csproj`, `infrastructure/transcoding-ffmpeg/Hispania.Transcoding.FFmpeg.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.Sqlite/Hispania.Library.Persistence.Sqlite.csproj`, `application/platform/observability/Hispania.Platform.Observability.Application.csproj`, `tests/closure-process-host/Hispania.Closure.ProcessHost.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tests/library-application/Hispania.Library.Application.Tests.csproj

Projects: `application/library/Hispania.Library.Application.csproj`, `infrastructure/library/Hispania.Library.LocalFileSystem.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tests/library-contracts/Hispania.Library.Contracts.Tests.csproj

Projects: `contracts/library/Hispania.Library.Contracts.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tests/library-filesystem/Hispania.Library.LocalFileSystem.Tests.csproj

Projects: `infrastructure/library/Hispania.Library.LocalFileSystem.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tests/library-persistence/Hispania.Library.Persistence.Sqlite.Tests.csproj

Projects: `infrastructure/persistence/Hispania.Library.Persistence.Sqlite/Hispania.Library.Persistence.Sqlite.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.PostgreSql/Hispania.Library.Persistence.PostgreSql.csproj`, `infrastructure/library/Hispania.Library.LocalFileSystem.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.EFCore/Hispania.Library.Persistence.EFCore.csproj`, `infrastructure/metadata/Hispania.Metadata.Tmdb/Hispania.Metadata.Tmdb.csproj`, `contracts/metadata/Hispania.Metadata.Contracts.csproj`, `application/metadata/Hispania.Metadata.Application.csproj`, `application/library/Hispania.Library.Application.csproj`, `application/playback/Hispania.Playback.Application.csproj`, `apps/server/Hispania.Server.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tests/metadata-tmdb/Hispania.Metadata.Tmdb.Tests.csproj

Projects: `infrastructure/metadata/Hispania.Metadata.Tmdb/Hispania.Metadata.Tmdb.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tests/playback/Hispania.Playback.Tests.csproj

Projects: `application/playback/Hispania.Playback.Application.csproj`, `modules/playback-conservative/Hispania.Playback.Conservative.csproj`, `application/library/Hispania.Library.Application.csproj`, `infrastructure/library/Hispania.Library.LocalFileSystem.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.EFCore/Hispania.Library.Persistence.EFCore.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.Sqlite/Hispania.Library.Persistence.Sqlite.csproj`, `apps/server/Hispania.Server.csproj`, `apps/worker-agent/Hispania.WorkerAgent.csproj`, `modules/extension-demo/Hispania.ExtensionDemo.csproj`, `contracts/extension-demo/Hispania.ExtensionDemo.Contracts.csproj`, `infrastructure/transcoding-ffmpeg/Hispania.Transcoding.FFmpeg.csproj`, `apps/ffmpeg-worker/Hispania.FFmpeg.Worker.csproj`, `tests/fakes/Hispania.FFmpeg.Worker.Fake/Hispania.FFmpeg.Worker.Fake.csproj`, `infrastructure/persistence/distributed-work/Hispania.DistributedWork.Persistence.Sqlite.csproj`, `application/platform/jobs/Hispania.Platform.Jobs.Application.csproj`, `infrastructure/platform/persistence-sqlite/Hispania.Platform.Persistence.Sqlite.csproj`, `infrastructure/platform/worker-agent-jobs/Hispania.Platform.WorkerAgentJobs.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.AspNetCore.TestHost`.

## tests/unit/Hispania.Media.Domain.Tests.csproj

Projects: `core/domain/Hispania.Media.Domain.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tests/unit/Hispania.Runtime.Tests.csproj

Projects: `apps/module-host/Hispania.ModuleHost.csproj`, `contracts/architecture-test/Hispania.ArchitectureTest.Contracts.csproj`, `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`, `contracts/platform/Hispania.Platform.Contracts.csproj`, `core/runtime/Hispania.Runtime.csproj`, `modules/extension-demo/Hispania.ExtensionDemo.csproj`, `modules/architecture-test/Hispania.ArchitectureTest.Modules.csproj`, `application/playback/Hispania.Playback.Application.csproj`, `application/library/Hispania.Library.Application.csproj`, `application/metadata/Hispania.Metadata.Application.csproj`, `infrastructure/metadata/Hispania.Metadata.Tmdb/Hispania.Metadata.Tmdb.csproj`, `infrastructure/library/Hispania.Library.LocalFileSystem.csproj`, `infrastructure/ffprobe/Hispania.Library.FFprobe.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.EFCore/Hispania.Library.Persistence.EFCore.csproj`, `infrastructure/persistence/Hispania.Library.Persistence.Sqlite/Hispania.Library.Persistence.Sqlite.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tools/dev-launcher-tests/Hispania.DevLauncher.Tests.csproj

Projects: `tools/dev-launcher/Hispania.DevLauncher.csproj`.
Packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## tools/dev-launcher/Hispania.DevLauncher.csproj

Projects: none.
Packages: none.
