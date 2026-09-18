# Home Appliance Manual RAG Demo

家庭電器使用手冊的 RAG demo site，使用最新 .NET 10、Blazor Web App Interactive Server、PostgreSQL + pgvector，以及 Azure OpenAI。

## Product scope

- 首頁 `/` 是 Chat Room。
- Anonymous visitor 只能查詢 admin 公開的 PDF 手冊。
- 登入後的普通 user 只能查詢自己上載的 PDF 手冊。
- Admin 可上載不限數量的 PDF；每份最多 10 MB，admin 可查看、刪除及重新索引。
- User 最多有 3 份 active/processing 文件；failed 文件不佔額，刪除後可補位。
- 回覆使用 streaming，顯示文件名稱及頁碼 citations；找不到答案時必須明確表示不知道。
- Identity routes: `/register` creates a normal User account, `/login` signs in, and
  `POST /account/logout` signs out. The configured `SeedAdmin` account is idempotently
  assigned the Admin role at startup. Validation errors support English and Traditional
  Chinese based on the request language.
- Authenticated users manage documents at `/my-documents`. The page is role-aware:
  normal users see and manage only their own documents and have three active
  (Pending, Processing, or Ready) document slots; administrators see every document
  (including its owner), have unlimited uploads, and can retry, re-index, or delete
  any document. Uploads are server-validated PDFs (extension, `application/pdf`
  content type, `%PDF-` signature) up to 10 MB and stored beneath `Storage:RootPath`
  with generated keys. Failed and deleted records do not consume a normal user's
  quota. A normal user may have only one document in `Pending` or `Processing` at
  a time; another upload or retry is rejected until it reaches `Ready` or `Failed`.
  The listing loads all authorized documents initially, then receives server-pushed
  status updates from the Interactive Server circuit while indexing. A focused
  three-second refresh remains as a reconnect fallback. The page shows a
  stage-based progress bar with a loading indicator. There is no separate
  `/admin/documents` route. Phase 2 creates Pending records; indexing is
  intentionally deferred to Phase 3. Upload, delete, retry, and re-index are
  handled by Blazor component events; the protected download route remains a
  normal GET so the browser can stream a PDF.

Phase 2 storage ordering writes the PDF to local storage before saving metadata,
and compensates by deleting the file if the metadata transaction fails. Delete
removes the database record and creates a durable pending cleanup record in the
same transaction, then removes the stored file. A cleanup worker retries failed
deletions and periodically removes unreferenced files older than one hour.

## Stack

- .NET 10 ASP.NET Core Blazor Web App
- Interactive Server rendering
- ASP.NET Core Identity with PostgreSQL
- PostgreSQL with `pgvector`
- Azure OpenAI chat and embedding deployments
- Local Docling CLI for Phase 3 PDF parsing (pin the Python/Docling environment)
- xUnit tests (integration and Playwright smoke tests are planned in later phases)

### Hosting defaults

Visual Studio and Docker use different host defaults even though both currently
run the application in the `Development` environment:

| Host | URL | Environment | HTTPS |
| --- | --- | --- | --- |
| Visual Studio `http` profile | `http://localhost:5170` | `Development` | Disabled |
| Visual Studio `https` profile | `https://localhost:7151` | `Development` | Enabled |
| Docker Compose `web` service | `http://localhost:8080` | `Development` | Disabled by Compose |

If no launch profile or environment variable is selected, ASP.NET Core defaults
to the `Production` environment. The Compose file explicitly sets
`ASPNETCORE_ENVIRONMENT=Development`, `ASPNETCORE_HTTP_PORTS=8080`, and
`HttpsRedirection__Enabled=false`. The Visual Studio profiles set
`ASPNETCORE_ENVIRONMENT=Development`; the `https` profile also supplies the
local HTTPS certificate and port.

The application explicitly enables WebSockets with a 30-second keep-alive.
Interactive Server prefers the `/_blazor` WebSocket, while the document page
also performs a focused three-second refresh for Pending/Processing documents
when a circuit event is delayed or missed. A reverse proxy must forward
WebSocket upgrade requests.

## Local quick start

### Prerequisites

- .NET 10 SDK
- Docker Desktop
- Azure OpenAI resource with chat and embedding deployments

### Configuration

Copy the example configuration for reference and set local secrets with User Secrets. Never commit real secrets.

```powershell
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=manuals;Username=postgres;Password=replace-with-password" --project HomeApplianceManualRag.Web
dotnet user-secrets set "SeedAdmin:Email" "admin@example.invalid" --project HomeApplianceManualRag.Web
dotnet user-secrets set "SeedAdmin:Password" "replace-with-a-strong-development-password" --project HomeApplianceManualRag.Web
```

The `.env.example` file documents Docker Compose environment variables. ASP.NET Core does not load a `.env` file automatically; Docker Compose loads `.env` for Compose variable substitution.

The Docker Compose deployment uses these settings:

```text
POSTGRES_PASSWORD
POSTGRES_DB
POSTGRES_USER
AzureOpenAI__Endpoint
AzureOpenAI__ApiKey
AzureOpenAI__ChatDeployment
AzureOpenAI__EmbeddingDeployment
AzureOpenAI__ApiVersion
AzureOpenAI__EmbeddingDimensions
SeedAdmin__Email
SeedAdmin__Password
```

`POSTGRES_PASSWORD` is required by PostgreSQL. `POSTGRES_DB` and
`POSTGRES_USER` default to `manuals` and `postgres`; Compose passes the same
values to PostgreSQL and builds the web container's `ConnectionStrings__Default`
with `Host=postgres`. Upload, retrieval, rate-limit, storage, and
Docling settings use the checked-in `appsettings.json` defaults unless they are
explicitly added to the Compose service environment.

`AzureOpenAI__EmbeddingDimensions` must be `1536` for the current migration and match the selected embedding deployment. Changing the embedding deployment dimension requires a new migration and compatible embedding configuration. `SeedAdmin__Password` and registration passwords must contain at least 8 characters; stronger passwords are still recommended.

### Run

To run PostgreSQL and the website together in Docker for local debugging:

```powershell
Copy-Item .env.example .env
# Replace placeholder passwords in .env before starting the stack.
docker compose up --build
```

Open `http://localhost:8080`. The web container waits for PostgreSQL to become healthy and applies EF Core migrations automatically in the Development environment. Stop the stack with `docker compose down`; add `-v` if you also want to remove the PostgreSQL data volume.

When source code has changed, rebuild and recreate the web container so it
contains the current Blazor progress-update implementation:

```powershell
docker compose down
docker compose build --no-cache web
docker compose up -d
docker compose logs -f web
```

The page requires an active Blazor Interactive Server circuit for push updates.
If the status only changes after a refresh, check the browser developer tools
for a connected `/_blazor` WebSocket and confirm the container was rebuilt from
the current branch. A reverse proxy in front of Docker must forward WebSocket
upgrade requests.

For GitHub Actions self-hosted deployment, the workflow creates the secrets file
outside the runner workspace because Actions may clean that directory. Set the
machine-level `RAG_DEPLOY_ENV_FILE` variable on the deployment host to a fixed
path, for example:

```text
D:\deploy-secrets\rag-home-appliance-manual\.env
```

Add these repository Actions secrets; the names are intentionally different
from the Compose keys so the workflow can map them without exposing values:

```text
DEPLOY_POSTGRES_PASSWORD
DEPLOY_POSTGRES_DB (Actions variable; defaults to `manuals`)
DEPLOY_POSTGRES_USER (Actions variable; defaults to `postgres`)
DEPLOY_AZURE_OPENAI_ENDPOINT
DEPLOY_AZURE_OPENAI_API_KEY
DEPLOY_AZURE_OPENAI_CHAT_DEPLOYMENT
DEPLOY_AZURE_OPENAI_EMBEDDING_DEPLOYMENT
DEPLOY_AZURE_OPENAI_API_VERSION
DEPLOY_AZURE_OPENAI_EMBEDDING_DIMENSIONS
DEPLOY_SEED_ADMIN_EMAIL
DEPLOY_SEED_ADMIN_PASSWORD
```

The workflow writes these secrets to the fixed `.env` path and passes it with
`docker compose --env-file`; it does not print the file contents. After setting
`RAG_DEPLOY_ENV_FILE`, restart the self-hosted runner service so it receives the
machine-level variable. Do not commit the generated file or put its values in
workflow logs.

To inspect the PostgreSQL database with Adminer, opt in to the `tools` profile:

```powershell
docker compose --profile tools up --build
```

Open `http://localhost:8081` and use `PostgreSQL` as the system, `postgres` as the server, `postgres` as the username, the `POSTGRES_PASSWORD` value from `.env` as the password, and `manuals` as the database. Adminer is bound to localhost and is not started by the default Compose command.

For running the website directly on the host:

```powershell
docker compose up -d postgres
dotnet ef database update --project HomeApplianceManualRag.Web --startup-project HomeApplianceManualRag.Web
dotnet run --project HomeApplianceManualRag.Web
```

The Docker Compose PostgreSQL service creates the `manuals` database on first initialization. The EF Core migrations then create the PostgreSQL `pgvector` extension, ASP.NET Core Identity tables, document/chunk tables, storage cleanup records, and durable indexing jobs. Run `dotnet ef database update` after starting PostgreSQL and before the first application run. If PostgreSQL is managed outside Docker, create the configured database first, then run the migration. The application must not call Azure OpenAI during database migrations.

### Phase 3 document indexing

Phase 3 keeps the upload MVP PDF-only. A durable indexing job is created with
each new, retried, or re-indexed document. The hosted worker invokes the local
Docling executable using `Docling:ExecutablePath` and
`Docling:ArgumentsTemplate`, reads a structured JSON result with page/block
provenance, creates deterministic page-aware chunks, and validates
`AzureOpenAI:EmbeddingDimensions` before storing vectors atomically.

Install and pin Docling separately in the worker environment; the web Docker
image does not install Python packages. The default command template expects a
wrapper/CLI that writes the JSON contract to `{output}`:

```text
Docling:ExecutablePath = docling
Docling:ArgumentsTemplate = --output {output} --format json {input}
```

The JSON contract is intentionally application-owned: each page contains
ordered blocks with `text`, `blockType`, `headingPath`, and optional
`sourceReference`. Markdown export can be used for diagnostics, but indexing
must consume structured output so citations never have to be reconstructed from
rendered Markdown. DOCX, image inputs, and OCR remain future parser adapters.

## Repository conventions

- All authorization is enforced server-side; UI hiding is not authorization.
- All document retrieval queries must apply visibility and ownership filters before vector similarity.
- A document is searchable only when its status is `Ready`.
- Failed indexing must preserve an actionable error message for the document owner/admin.
- Use UTC timestamps and async APIs.
- Keep provider calls behind interfaces so tests can use deterministic fakes.

## Delivery phases

1. Foundation: project, Identity, roles, PostgreSQL, pgvector, configuration, migrations.
2. Documents: PDF validation, storage, ownership/quota rules, and the unified
   role-aware `/my-documents` page.
3. Indexing: page extraction, chunking, embeddings, worker, lifecycle statuses.
4. Chat: filtered retrieval, grounded prompt, streaming response, citations, anonymous/user behavior.
5. UX and quality: localization, empty/error states, rate limit, tests, Docker and deployment documentation.

See [SPEC.md](SPEC.md) for the complete product and technical specification and [TASKS.md](TASKS.md) for implementation-ready work items.
