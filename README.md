# Home Appliance Manual RAG

家庭電器使用手冊問答網站：使用 .NET 10、Blazor Interactive Server、PostgreSQL/pgvector、Docling 和 Azure OpenAI。

## 功能概覽

- 訪客查詢管理員公開的手冊；登入使用者只查詢自己的手冊；管理員可管理所有文件及索引工作。
- PDF 上傳、索引、語意與多語詞彙檢索，以及附頁碼來源的串流回答。
- 支援英文及繁體中文；聊天記錄依使用者或匿名存取金鑰隔離。
- `SPEC.md` 列出完整需求、權限、資料模型及安全規則；`TASKS.md` 列出實作進度。

## 本機啟動

### 需求

- Docker Desktop
- .NET 10 SDK（只在主機直接執行網站時需要）
- Azure OpenAI 及 TypeSafe API 設定，才能使用完整索引與聊天功能

### Docker Compose

```powershell
Copy-Item .env.example .env
# 在 .env 設定實際的密碼及服務金鑰，不要提交此檔案。
docker compose up --build
```

開啟 `http://localhost:8500`。Compose 會啟動 PostgreSQL/pgvector、Docling 和網站；網站啟動時會套用 EF Core migrations 並建立設定中的管理員帳戶。

主要 `.env` 設定：

```text
POSTGRES_PASSWORD
AzureOpenAI__Endpoint
AzureOpenAI__ApiKey
AzureOpenAI__ChatDeployment
AzureOpenAI__EmbeddingDeployment
AzureOpenAI__ApiVersion
AzureOpenAI__EmbeddingDimensions=1536
TypeSafe__ApiKey
SeedAdmin__Email
SeedAdmin__Password
```

`POSTGRES_DB` 和 `POSTGRES_USER` 預設為 `manuals`、`postgres`。`AzureOpenAI__EmbeddingDimensions` 必須符合目前資料庫向量欄位的 1536 維設定。管理員和註冊密碼至少 8 個字元。ASP.NET Core 不會自行載入 `.env`；此檔由 Docker Compose 讀取。

停止服務：

```powershell
docker compose down
```

`docker compose down -v` 會連資料庫 volume 一併刪除。PDF 和資料庫分別儲存在 `document-data` 與 `postgres-data` volumes。

### 主機直接執行網站

先依 `.env.example` 設定 Docker Compose 所需服務，再使用 User Secrets 設定主機上的網站連線及秘密：

```powershell
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=manuals;Username=postgres;Password=<password>" --project HomeApplianceManualRag.Web
dotnet user-secrets set "SeedAdmin:Email" "admin@example.invalid" --project HomeApplianceManualRag.Web
dotnet user-secrets set "SeedAdmin:Password" "<development-password>" --project HomeApplianceManualRag.Web
dotnet user-secrets set "Docling:ServiceUrl" "http://localhost:5001" --project HomeApplianceManualRag.Web
docker compose up -d postgres docling
dotnet run --project HomeApplianceManualRag.Web
```

另以 User Secrets 設定 Azure OpenAI 與 TypeSafe 的 endpoint、deployment 和 API keys。不要把真實秘密放入設定檔或提交至 Git。

## 測試

執行一般測試（不需要 Azure credentials）：

```powershell
dotnet test HomeApplianceManualRag.Tests\HomeApplianceManualRag.Tests.csproj
```

PostgreSQL/pgvector 整合測試需使用專用 PostgreSQL 17 測試資料庫。測試會建立暫存資料庫、執行 migrations 和檢索測試，完成後刪除暫存資料庫；連線角色必須有建立及刪除資料庫的權限。**不可使用 Production 資料庫。**

```powershell
$env:POSTGRES_INTEGRATION_TEST_CONNECTION_STRING = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=<password>"
dotnet test HomeApplianceManualRag.Tests\HomeApplianceManualRag.Tests.csproj --filter FullyQualifiedName~PostgresIntegrationTests
Remove-Item Env:POSTGRES_INTEGRATION_TEST_CONNECTION_STRING
```

未設定連線字串時，PostgreSQL 整合測試會略過。

## 部署

GitHub Actions 使用兩個環境：

| 分支／觸發 | GitHub Environment | Runner | 部署設定 |
| --- | --- | --- | --- |
| `dev` push 或手動執行 `deploy-dev.yml` | `dev` | `self-hosted`, `dev` | 一般 Compose，網站 port `8500` |
| Push version tag on `main` 或手動執行 `deploy-main.yml` | `production` | `self-hosted`, `qnap` | Production Compose，網站 port `8501` |

Production 只部署符合 `vMAJOR.MINOR.PATCH`（例如 `v1.2.3`）且指向 `main` 歷史的 tag。手動執行亦須輸入版本 tag。發佈前先將變更合併至 `main`，再建立並推送 tag；`dev` 不需要版本 tag。兩個 GitHub Environment 都需設定以下項目：

- **Variables**：`POSTGRES_DB`（可選，預設 `manuals`）、`POSTGRES_USER`（可選，預設 `postgres`）、`AzureOpenAI__EmbeddingDimensions`（可選，預設 `1536`）。
- **Secrets**：`POSTGRES_PASSWORD`、`AzureOpenAI__Endpoint`、`AzureOpenAI__ApiKey`、`AzureOpenAI__ChatDeployment`、`AzureOpenAI__EmbeddingDeployment`、`AzureOpenAI__ApiVersion`、`SeedAdmin__Email`、`SeedAdmin__Password`、`TypeSafe__ApiKey`。

Production Compose 不對外公開 PostgreSQL、Docling 或 Adminer。正式公開服務前，應由 TLS reverse proxy 提供 HTTPS，並確認它支援 WebSocket upgrade，供 Blazor Interactive Server 使用。

## 選用工具

只在需要檢視資料庫時啟動 Adminer（本機 `http://localhost:8581`）：

```powershell
docker compose --profile tools up
```

Adminer 只綁定 localhost。Docling Serve 固定使用 `v1.34.0`，避免未檢查相容性便改用 `latest`。完成 PDF 索引後，若程式或 parser 有更新，請重新 build web image。
