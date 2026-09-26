# PROGRESS-LOG.md

### 2026-09-26 — Corrección de CI Linux/Windows — PASS local
- Goal: reparar la validación del commit `34681b0` en GitHub Actions.
- Finding: GitHub descargó ese commit correctamente; el job Ubuntu abortó al ejecutar `Hispania.DevLauncher.Tests` porque `Microsoft.WindowsDesktop.App` sólo está disponible en Windows. La compilación y las demás pruebas publicadas en el log pasaron.
- Changed: filtro `Hispania.CrossPlatform.slnf` para restore/build/test Linux y job Windows separado para las pruebas del launcher.
- Verification: comandos del filtro y pruebas Windows ejecutados localmente; resultado final sujeto al nuevo run de GitHub Actions.
- Next: comprobar ambos jobs del nuevo commit remoto.

### 2026-09-23 — Auditoría integral y sincronización Git — PASS
- Goal: revisar la consistencia modular y publicar el estado real del árbol.
- Changed: auditoría de fronteras en `docs/MODULARITY_AUDIT_2026-09-23.md`, roadmap y backlog actualizados; expectativa web obsoleta corregida; artefactos locales excluidos de Git.
- Verification: `dotnet test Hispania.sln --no-restore --verbosity quiet` 412/412; `npm test` 29/29; `npm run build` correcto; `git diff --check` correcto.
- Result: dominio/contratos/aplicación mantienen el aislamiento principal; se registran acoplamientos ffprobe/filesystem, FFmpeg/host y EFCore/SQLite, además de autorización pendiente. No se declara validación real TMDB ni Phase 7 terminada.
- Next: ver `TODO.md`, empezando por completar P0-010 y proteger operaciones administrativas antes de exposición remota.

### 2026-09-22 — P0-010 — PASS
- Goal: cerrar Stage C con persistencia, locks, overrides manuales y bridge legacy 0001–0004.
- Changed: validación explícita de assertions y external IDs antes/después de dos startups; se corrigió el fixture para sembrar esos datos en el test legacy correcto.
- Commands: `dotnet restore Hispania.sln`; `dotnet build Hispania.sln --no-restore`; `dotnet test Hispania.sln --no-build`; `npm test`; `npm run build`; `git diff --check`.
- Verification: backend 385/385, persistence 53/53, frontend 17/17; build sin warnings/errores.
- Result: Stage B y Stage C COMPLETE. Stage D/E pendientes; no se marca el milestone global.
- Findings: bridge legacy normaliza sólo history; ProductVersion, IDs y datos permanecen intactos. Fingerprint idéntico no se reaplica; collections locked conservan la proyección del provider refrescado.
- Next: Stage D — Metadata Editor API.

Append only.

### 2026-09-22 — Inicio / Mis contenidos — PASS
- Changed: added reusable `ContentCarousel`, Inicio with `Mis contenidos` before `Seguir viendo`, accessible full-surface library cards and explicit placeholder labels for unsupported libraries.
- Verification: frontend 26/26 and production build passed. No fictitious progress or recent media was added; future recent/continue sections can reuse the same carousel when real data exists.

### 2026-09-22 — Web navigation and localized artwork diagnosis — PASS
- Changed: added accessible HISPANIA drawer with real movie/series/collection/music routes, explicit administration placeholders, Escape/outside-click close and responsive styling; preserved Movie Detail and Metadata Editor entry points.
- Matching: filename parser tests now cover MP4/AVI/MKV naming and the matcher accepts an exact normalized provider original title with the same year; no fuzzy/popularity/first-result matching.
- Verification: persistence 56/56, frontend 22/22, build green; full solution validation green with backend totals 389 tests from the preceding complete run, now 392 after the three new matcher/parser cases.
- Live diagnosis: no local live TMDB/server log contains records for The Weather Man or Un Ratoncito Duro De Roer, so absent artwork cannot be attributed to download, locale, token, candidate, confirmation, refresh, locator delivery or frontend without a live run. Pending explicitly.

### 2026-09-22 — Stage E — PASS
- Goal: verificar el Web Metadata Editor sin ampliar el alcance.
- Changed: se corrigió el orden de hooks del router real para que la ruta directa y el retorno no cambien el orden de hooks; se conservaron drafts y estados de error.
- Verification: `dotnet test Hispania.sln` sin filtros: 389/389; suites 179, 11, 18, 15, 11, 22, 27, 53 y 53; frontend 20/20; build y diff check pendientes de la corrida final.
- Result: Stage E implementado y probado automáticamente. Live browser y TMDB siguen pendientes.
- Findings: la cifra 347 no corresponde al árbol actual; no hay proyectos excluidos ni tests omitidos en la ejecución completa. La diferencia es de 42 tests frente al snapshot previo.

### 2026-09-22 — P0-010 — PASS
- Goal: complete Stage D Metadata Editor API.
- Changed: HTTP editor DTOs/endpoints, explicit value mapping, collection lock aliases, multi-provider HTTP provenance tests and SQLite restart persistence coverage.
- Verification: Playback HTTP 53/53; backend 389/389; persistence 53/53; frontend 17/17; builds green.
- Result: Stage B, Stage C and Stage D COMPLETE. Stage E remains pending; global milestone remains active.
- Findings: `PUT []` is an explicit empty Manual collection override; DELETE removes it. People/Credits and External IDs remain read-only.

## Entry template
### 2026-09-22 — Inicio / carruseles visuales — PASS
- Goal: alinear Inicio a filas horizontales propias de bibliotecas, continuidad y recientes.
- Changed: `ContentCarousel` ahora ofrece variantes library (2:1), landscape (16:9) y poster (2:3), soporte de teclado/flechas/trackpad/touch, artwork placeholder y fila de recientes que abre Movie Detail. Seguir viendo permanece vacío sin progreso persistido.
- Verification: `npm test` 30/30; `npm run build` correcto; `git diff --check` sin errores de whitespace (avisos CRLF existentes).
- Documentation: se actualizó `docs/codex-contexts/web-ui.md` con orden, proporciones y límites actuales del read model.

## Entry template
### YYYY-MM-DD HH:MM — TASK-ID — START|PASS|FAIL|BLOCKED|NOTE
- Goal:
- Changed:
- Commands:
- Verification:
- Result:
- Findings:
- Next:

### 2026-09-21 — P0-010 — PASS
- Goal: complete Stage B normalized rich metadata persistence.
- Changed: added provider-neutral SQLite tables/migration `0003_rich_metadata`, `IRichMetadataStore`, EF persistence, Work-scoped genres/tags/studios/credits, reusable person identity and provider-scoped refresh reconciliation.
- Verification: restart, same-payload idempotency, changed payload and multi-provider isolation tests pass; full backend/frontend validation follows.
- Result: rich normalized persistence implemented and tested; editor API/UI, locks/manual overrides and live verification remain pending.

### 2026-09-21 20:10 — P0-001 — PASS
- Goal: reconcile project documentation with the repository and current implementation state.
- Changed: aligned `TODO.md` with completed library/discovery/persistence/artwork/Web UI/Direct Play slices and left metadata identification as the active item pending live/restart verification.
- Commands: `dotnet test Hispania.sln --no-restore`; `npm run build`.
- Verification: backend 179/179 tests passed; frontend TypeScript/Vite production build passed. Vitest was started but did not return in the available run; no test result was claimed from it.
- Result: documentation now reflects the current working tree and explicit deferred TMDB scope.
- Findings: repository contains unrelated/pre-existing uncommitted source and operational files; they were preserved.
- Next: complete live TMDB verification and SQLite restart verification before marking P0-005 complete.

### 2026-09-21 20:45 — P0-005 — NOTE
- Goal: implement the bounded automatic-enrichment slice without live TMDB credentials.
- Changed: active-root filtering during catalog snapshot reconstruction; optional video bit depth; deterministic filename/year parser; provider-neutral exact title/year auto-matcher; startup enrichment hosted workflow reusing select/confirm/fetch; matcher tests; roadmap and metadata/web context updates.
- Commands: `dotnet build Hispania.sln --no-restore`; `dotnet test Hispania.sln --no-build`.
- Verification: live scan with only `movies` configured exposes exactly three items (MP4, AVI, MKV); backend build passes with 0 warnings/errors; backend tests pass, including 38 persistence tests and 6 new matcher/parser tests.
- Result: stale imports remain durable but roots outside the active configuration are excluded from the visible projection. TMDB auto-enrichment is skipped cleanly when disabled or credentials are absent.
- Findings: live TMDB verification, fake end-to-end auto-enrichment integration coverage and restart request-count verification remain to be completed; no milestone completion claimed.
- Next: add fake-provider integration/restart/failure tests and later run live verification in a session carrying the TMDB token.

### 2026-09-21 21:10 — P0-010 — START
- Goal: establish the provider-neutral foundation for Movie Metadata Model 0.2 and Metadata Editor 0.1 without changing Movie Detail into an editor.
- Changed: TMDB normalization now maps original language, tagline, explicit community rating, genres, production companies, keywords, IMDb ID and collection ID through generic metadata fields/identifiers; the application profile accepts these fields without provider-specific database columns. Documentation records the consumption/admin separation and staged scope.
- Commands: `dotnet test tests/metadata-tmdb/Hispania.Metadata.Tmdb.Tests.csproj --no-restore`.
- Verification: 27 TMDB adapter tests passed.
- Result: Stage B foundation is green; no milestone completion claimed.
- Findings: normalized people/credits and settings/locks need first-class provider-neutral persistence before editor API/UI work; live TMDB verification remains unavailable without a token.
- Next: continue with normalized people, credits, studios/tags, metadata settings/locks, editor API and Web Editor tests.
