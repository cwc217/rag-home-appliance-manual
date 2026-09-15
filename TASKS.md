# Implementation Tasks

給 coding agent 的執行順序。每一項完成後先跑對應測試，再進入下一階段。不要一次跳過所有 acceptance criteria。

## Phase 0 — Project foundation

- [x] Create a .NET 10 Blazor Web App using Interactive Server.
- [x] Add EF Core PostgreSQL provider, ASP.NET Core Identity, pgvector support, PDF text extraction library, Azure OpenAI client abstraction, and test projects.
- [x] Add Docker Compose PostgreSQL image with pgvector.
- [x] Add strongly typed options for database, Azure OpenAI, upload limits, retrieval, rate limiting, and seed admin.
- [x] Add safe configuration validation at startup; fail clearly for missing production-required settings.
- [x] Add `.env.example` with placeholders only.
- [x] Add initial xUnit test project and a deterministic fake chat/embedding provider.

## Phase 1 — Identity and roles

- [x] Create `ApplicationUser` and Identity DbContext.
- [x] Add `Admin` and `User` roles.
- [x] Implement idempotent admin seed from environment variables.
- [x] Implement registration that always creates `User`.
- [x] Add login, logout, registration, and localized validation messages.
- [x] Add authorization policies and a current-user abstraction.
- [x] Test role seed, registration role, login, and unauthorized/forbidden behavior.

## Phase 2 — Documents and quotas

- [x] Create `ManualDocument` and `ManualChunk` entities with UTC audit fields.
- [x] Add migrations for Identity, documents, chunks, pgvector extension, vector dimension, and indexes.
- [x] Implement server-side PDF signature/type validation and 10 MB limit.
- [x] Implement safe file storage abstraction; keep local storage as the initial development provider.
- [x] Implement transactional user quota enforcement for active/processing documents.
- [x] Implement document commands: upload, list, delete, retry, and admin re-index.
- [x] Implement status values `Pending`, `Processing`, `Ready`, `Failed`.
- [x] Create the role-aware `/my-documents` page with normal-user and admin actions.
- [ ] Add unit and integration tests for file validation, quota concurrency, ownership, and admin permissions.
- [x] Add storage cleanup/reconciliation for failures between file and database operations.

## Phase 3 — Indexing worker

- [ ] Add durable indexing job/outbox persistence.
- [ ] Add hosted worker with bounded retry and cancellation.
- [ ] Extract text page-by-page from valid PDFs.
- [ ] Reject empty/textless PDFs with an actionable `Failed` error.
- [ ] Implement deterministic configurable chunking with overlap and page metadata.
- [ ] Call Azure embedding through an interface and validate embedding dimensions.
- [ ] Persist chunks atomically for a document version.
- [ ] Make retries idempotent and ensure deleted documents are not reintroduced.
- [ ] Add worker lifecycle, parser failure, dimension mismatch, and retry tests.

## Phase 4 — Retrieval and chat

- [ ] Implement pgvector cosine similarity query.
- [ ] Implement explicit retrieval scopes: anonymous admin-public, normal-user-own, admin-admin scope.
- [ ] Ensure status and ownership/visibility filters execute in SQL before similarity ranking.
- [ ] Build grounded prompt with retrieved context and citation metadata.
- [ ] Add Azure chat client abstraction and streaming response adapter.
- [ ] Add no-context/no-answer behavior and provider error mapping.
- [ ] Add `/` Chat Room with anonymous and authenticated scope behavior.
- [ ] Render citations as manual name + page number.
- [ ] Add basic per-user chat rate limit and localized `429` state.
- [ ] Test scope isolation, prompt rules, streaming adapter, citations, and no-result behavior.

## Phase 5 — Localization and UX quality

- [ ] Add `en` and `zh-Hant` resource files.
- [ ] Add browser-culture default and language selector.
- [ ] Localize navigation, validation, status, errors, empty states, quota messages, and chat states.
- [ ] Add empty state when no admin document is ready.
- [ ] Add processing progress/status refresh without exposing internal errors or secrets.
- [ ] Add responsive layout and keyboard-accessible controls.
- [ ] Add Playwright smoke flows for anonymous, user, and admin journeys.

## Phase 6 — Operational readiness

- [ ] Add database and worker health checks.
- [ ] Add structured logging with secret/document-content redaction.
- [ ] Add local run instructions and Docker Compose commands to `README.md`.
- [ ] Add Azure App Service and Azure Database for PostgreSQL deployment instructions.
- [ ] Add opt-in real Azure OpenAI smoke-test instructions; default tests must use fakes.
- [ ] Verify migrations from an empty database.
- [ ] Run unit, integration, and Playwright smoke suites.
- [ ] Check every acceptance criterion in `SPEC.md`.

## Definition of done

- No document can be retrieved outside its authorized scope.
- User quota and 10 MB file size are enforced server-side.
- Only `Ready` documents enter retrieval.
- Citations retain exact source page metadata.
- Azure credentials never appear in source, tests, logs, or committed examples.
- Default automated tests pass without Azure credentials.
- README setup commands work on a clean local environment.
