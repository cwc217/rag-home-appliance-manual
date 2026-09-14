# Home Appliance Manual RAG Demo

家庭電器使用手冊的 RAG demo site，使用最新 .NET 10、Blazor Web App Interactive Server、PostgreSQL + pgvector，以及 Azure OpenAI。

## Product scope

- 首頁 `/` 是 Chat Room。
- Anonymous visitor 只能查詢 admin 公開的 PDF 手冊。
- 登入後的普通 user 只能查詢自己上載的 PDF 手冊。
- Admin 可上載不限數量的 PDF；每份最多 10 MB，admin 可查看、刪除及重新索引。
- User 最多有 3 份 active/processing 文件；failed 文件不佔額，刪除後可補位。
- 回覆使用 streaming，顯示文件名稱及頁碼 citations；找不到答案時必須明確表示不知道。

## Stack

- .NET 10 ASP.NET Core Blazor Web App
- Interactive Server rendering
- ASP.NET Core Identity with PostgreSQL
- PostgreSQL with `pgvector`
- Azure OpenAI chat and embedding deployments
- Background indexing worker
- xUnit, integration tests, and Playwright smoke tests

## Local quick start

### Prerequisites

- .NET 10 SDK
- Docker Desktop
- Azure OpenAI resource with chat and embedding deployments

### Configuration

Copy the example configuration and set values locally. Never commit real secrets.

```powershell
Copy-Item .env.example .env
```

Required settings:

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

`AzureOpenAI__EmbeddingDimensions` must match the selected embedding deployment and the PostgreSQL vector column.

### Run

To run PostgreSQL and the website together in Docker for local debugging:

```powershell
docker compose up --build
```

Open `http://localhost:8080`. The web container waits for PostgreSQL to become healthy and applies EF Core migrations automatically in the Development environment. Stop the stack with `docker compose down`; add `-v` if you also want to remove the PostgreSQL data volume.

For running the website directly on the host:

```powershell
docker compose up -d postgres
dotnet ef database update --project HomeApplianceManualRag.Web --startup-project HomeApplianceManualRag.Web
dotnet run --project HomeApplianceManualRag.Web
```

The Docker Compose PostgreSQL service creates the `manuals` database on first initialization. The EF Core migration then creates the PostgreSQL `pgvector` extension, ASP.NET Core Identity tables, and the initial manual document/chunk tables. Run `dotnet ef database update` after starting PostgreSQL and before the first application run. If PostgreSQL is managed outside Docker, create the configured database first, then run the migration. The application must not call Azure OpenAI during database migrations. Indexing starts only after an upload is stored and a background job is queued.

## Repository conventions

- All authorization is enforced server-side; UI hiding is not authorization.
- All document retrieval queries must apply visibility and ownership filters before vector similarity.
- A document is searchable only when its status is `Ready`.
- Failed indexing must preserve an actionable error message for the document owner/admin.
- Use UTC timestamps and async APIs.
- Keep provider calls behind interfaces so tests can use deterministic fakes.

## Delivery phases

1. Foundation: project, Identity, roles, PostgreSQL, pgvector, configuration, migrations.
2. Documents: PDF validation, storage, ownership/quota rules, admin and user pages.
3. Indexing: page extraction, chunking, embeddings, worker, lifecycle statuses.
4. Chat: filtered retrieval, grounded prompt, streaming response, citations, anonymous/user behavior.
5. UX and quality: localization, empty/error states, rate limit, tests, Docker and deployment documentation.

See [SPEC.md](SPEC.md) for the complete product and technical specification and [TASKS.md](TASKS.md) for implementation-ready work items.
