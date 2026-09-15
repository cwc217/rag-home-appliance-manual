# Product and Technical Specification

## 1. Goal

Build a demonstrable RAG website for household appliance manuals. The demo must show a trustworthy answer flow: a user asks a question, the system retrieves relevant manual pages from the correct document scope, Azure OpenAI answers only from the retrieved context, and the response includes citations.

This is a demo, not a billing, enterprise document-management, or OCR product.

## 2. Roles and access matrix

| Capability | Anonymous | User | Admin |
|---|---:|---:|---:|
| View Chat Room | Yes | Yes | Yes |
| Query admin public manuals | Yes | No | Yes |
| Query own manuals | No | Yes | Yes |
| Register | Yes | N/A | N/A |
| Upload manuals | No | Yes, maximum 3 active | Unlimited count |
| Manage manuals | No | Own list/retry/delete | All documents: list/retry/delete/re-index |

The selected scope is intentional: after login, a normal user searches only their own manuals, not admin manuals.

## 3. Authentication and authorization

- Use ASP.NET Core Identity with PostgreSQL and cookie authentication.
- Seed the first admin from `SeedAdmin__Email` and `SeedAdmin__Password`.
- Public registration always creates the `User` role.
- There is no self-service role elevation and no “first registered user becomes admin” behavior.
- Use policy/role authorization on endpoints, Razor components, commands, and queries.
- User document commands must verify `Document.OwnerUserId == CurrentUserId`.
- Admin document commands require the `Admin` role.
- Anonymous chat has an explicit public retrieval scope; it must not fall through to a user scope.

## 4. Document rules

### Accepted input

- MVP accepts PDF files with a text layer.
- Maximum file size is 10 MB for every upload.
- Validate extension, declared content type, file signature/magic bytes, and request size on the server.
- Do not trust the original file name. Generate a storage-safe identifier and retain the display name as metadata.
- Scanned/image-only PDFs are unsupported in MVP and must reach `Failed` with a user-visible explanation.

### Ownership and quota

- Admin documents have `OwnerUserId = null` and are public to anonymous retrieval.
- User documents have the authenticated user as owner and are private to that user.
- Admin document count has no application-level count limit.
- A user may have at most three documents in `Pending`, `Processing`, or `Ready`.
- `Failed` and deleted documents do not count toward the limit.
- Enforce the quota transactionally in the application/database boundary to prevent concurrent uploads bypassing the limit.
- Deleting a document makes it unavailable to retrieval and permits a replacement upload.

### Lifecycle

```text
Pending -> Processing -> Ready
                     \-> Failed
```

- `Pending`: The document has been uploaded and stored, but background processing has not started.
- `Processing`: A background worker is extracting text, creating chunks, and generating embeddings.
- `Ready`: Processing completed successfully and all required chunks and embeddings were persisted. The document is available for retrieval.
- `Failed`: Processing could not complete. `LastError` stores an actionable failure message, and the document can be retried by the owner or an admin.
- `Pending`, `Processing`, and `Failed` documents must not participate in retrieval.
- `Failed` documents do not count toward the normal user's active document quota.
- During Phase 2, upload writes the original PDF to local storage first, then saves
  the metadata record as `Pending`. Durable queueing and background indexing begin
  in Phase 3.
- During Phase 2, delete removes the metadata record first, then deletes the stored
  file. A failure between these operations is a known local-storage consistency
  limitation; a later cleanup/reconciliation strategy may remove orphaned files.
- Only `Ready` documents participate in retrieval.
- Retry is allowed for `Failed` documents by admin or the owning user.
- Deleting a document must cancel/ignore outstanding work and remove its chunks from retrieval.
- Store `LastError` for failed processing; do not silently convert failure to an empty document.

## 5. RAG pipeline

### Extraction and chunking

1. Read each PDF page with a text-layer parser.
2. Preserve page number and document id on every extracted page.
3. Normalize whitespace without destroying meaningful line breaks.
4. Split text into deterministic, overlapping chunks.
5. Store chunk text, page number, ordinal, token/character metadata, and document id.
6. Generate an embedding for each chunk using the configured Azure OpenAI embedding deployment.
7. Verify the returned dimension equals `AzureOpenAI__EmbeddingDimensions` before persistence.
8. Mark the document `Ready` only when all required chunks are embedded and persisted.

The exact chunk size and overlap may be tuned during implementation, but must be configuration values and covered by unit tests. A chunk must never lose its source page metadata.

### Retrieval

Use PostgreSQL `pgvector` cosine distance. Retrieval must:

- Filter by `Status = Ready` first.
- For anonymous requests, include only admin documents.
- For a normal user, include only documents owned by the current user.
- For admin requests, use an explicit admin scope; do not accidentally broaden normal user queries.
- Apply the scope in the database query, not after fetching vectors.
- Return top-k chunks with document display name, page number, chunk text, and similarity score.
- Use a vector index when supported by the selected PostgreSQL/pgvector version.

### Grounded generation

The chat service must send Azure OpenAI a system instruction that:

- Treats retrieved context as the only source of truth.
- Refuses to invent specifications, safety advice, or steps absent from context.
- Says it cannot find the answer when context is insufficient.
- Includes citations in the agreed format, for example `[Manual name, p. 4]`.
- Keeps the answer concise and actionable.

The application must not claim that an answer is grounded when no chunk was retrieved.

## 6. Chat Room

- Route: `/`.
- Anonymous users see the public/admin scope.
- Authenticated normal users see their own scope.
- Responses stream to the UI.
- Each answer displays citations with manual name and page number.
- Conversation state is scoped to the current page/session and is not persisted across devices.
- Do not store raw prompts or model responses as permanent history in MVP.
- Show loading, empty-index, no-results, provider-error, and rate-limit states.
- If no admin document is `Ready`, anonymous chat shows a setup/empty state instead of pretending retrieval succeeded.

## 7. Pages

- `/`: Chat Room.
- `/login`: Identity login.
- `/register`: User registration.
- `/my-documents`: authenticated, role-aware document list, upload, status, retry, and
  delete. Normal users see only their own documents and have a three-active quota.
  Admins see all documents with owner information, have unlimited uploads, and can
  retry, delete, and re-index any document. There is no separate admin document route.

Every upload page must show the 10 MB limit, accepted PDF requirement, current quota, and processing status.

## 8. Localization

- Use ASP.NET localization resource files for `en` and `zh-Hant`.
- Provide a language selector.
- Default to the browser culture when supported.
- Localize navigation, validation, status, errors, empty states, and buttons.
- Do not translate the original PDF text.
- Do not require the model to translate an answer in MVP; model-language behavior is outside this scope.

## 9. Data model

The implementation should use normal EF Core entities with migrations. A minimum model is:

### `ApplicationUser`

ASP.NET Core Identity user plus normal audit fields as needed.

### `ManualDocument`

- `Id`
- `OwnerUserId` nullable for admin/public documents
- `DisplayName`
- `StorageKey`
- `ContentType`
- `SizeBytes`
- `Status`
- `LastError` nullable
- `CreatedAtUtc`
- `UpdatedAtUtc`
- `IndexedAtUtc` nullable

### `ManualChunk`

- `Id`
- `DocumentId`
- `ChunkOrdinal`
- `PageNumber`
- `Text`
- `Embedding` (`vector(N)`)
- `CreatedAtUtc`

Add indexes for document status/owner and chunk document id. Add the pgvector index after the extension and vector column migration are available. The dimension `N` is configured consistently with the Azure deployment.

## 10. Background processing

- Use a hosted background worker with a durable database-backed job/outbox record, or an equivalent retry-safe design.
- Do not depend on an in-memory queue as the only record of work.
- A job must be idempotent for a document/version.
- Capture provider/parser errors in `LastError` and structured logs without secrets.
- Use bounded retry with backoff for transient Azure/database errors.
- Do not retry invalid PDF or dimension mismatch errors indefinitely.

## 11. Configuration and deployment

Required environment settings:

```text
ConnectionStrings__Default
AzureOpenAI__Endpoint
AzureOpenAI__ApiKey
AzureOpenAI__ChatDeployment
AzureOpenAI__EmbeddingDeployment
AzureOpenAI__ApiVersion
AzureOpenAI__EmbeddingDimensions
SeedAdmin__Email
SeedAdmin__Password
```

- Local development uses Docker Compose PostgreSQL with pgvector.
- Production guidance targets Azure App Service and Azure Database for PostgreSQL.
- Secrets are environment/secret-store values only.
- Include health checks for database availability and worker readiness.
- Do not expose API keys in logs, error messages, committed config, or markdown examples.

## 12. Security baseline

- Server-side file size, MIME, signature, and PDF validation.
- Anti-forgery protection for state-changing browser requests.
- Role and ownership checks on every document command.
- Retrieval-scope filtering in SQL.
- Safe generated storage names and no path traversal.
- Do not render PDF text as unsanitized HTML.
- Basic per-user chat rate limit; return a clear `429` state.
- Avoid logging document contents, prompts, API keys, or full model responses by default.

## 13. Testing requirements

### Unit tests

Cover PDF validation, chunking/page metadata, quota decisions, scope selection, lifecycle transitions, prompt construction, citation formatting, and embedding dimension validation.

### Integration tests

Use PostgreSQL/pgvector test infrastructure. Cover migrations, Identity roles, user quota under concurrency, ownership authorization, anonymous/admin/user retrieval scopes, and document lifecycle persistence.

### UI smoke tests

Playwright must cover:

1. Anonymous visitor opens Chat Room and sees admin/public scope.
2. Registered user uploads at most three documents and cannot query another user's document.
3. Admin uploads a 10 MB-or-less PDF, observes processing, and can delete/re-index.
4. Chat displays streaming output and page citations for a ready document.

Use deterministic fake chat and embedding providers in automated tests. A real Azure OpenAI smoke test is opt-in and never required for the default test suite.

## 14. Acceptance criteria

- A fresh local setup can start PostgreSQL/pgvector and the .NET 10 app using documented commands.
- Seeded admin can upload a valid PDF under 10 MB and the worker eventually marks it `Ready`.
- Anonymous chat retrieves only ready admin documents.
- A normal user can register, upload no more than three active documents, and retrieve only their own ready documents.
- A fourth active upload is rejected server-side with a localized, actionable message.
- Failed indexing is visible and retryable without corrupting prior ready data.
- Every grounded chat answer has document/page citations; insufficient context produces an explicit no-answer response.
- Admin-only actions and cross-user document access are rejected server-side.
- Unit, integration, and Playwright smoke tests pass without real Azure credentials.
