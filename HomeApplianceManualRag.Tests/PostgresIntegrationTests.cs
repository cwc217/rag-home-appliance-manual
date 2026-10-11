using HomeApplianceManualRag.Web.Chat;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Documents;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using System.Data;

namespace HomeApplianceManualRag.Tests;

[CollectionDefinition("PostgreSQL integration", DisableParallelization = true)]
public sealed class PostgresIntegrationCollection : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "PostgreSQL integration";
}

[Collection(PostgresIntegrationCollection.Name)]
public sealed class PostgresIntegrationTests(PostgresDatabaseFixture database)
{
    [PostgresIntegrationFact]
    public async Task Migrations_install_pgvector_and_create_generated_lexical_index()
    {
        await using var db = database.CreateContext();
        var migrations = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(migrations, migration =>
            migration.EndsWith("_DropLegacyManualChunkColumns", StringComparison.Ordinal));

        var vectorExtensionInstalled = await ExecuteScalarAsync<bool>(
            db, "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector')");
        Assert.True(vectorExtensionInstalled);

        var searchVectorIsGenerated = await ExecuteScalarAsync<bool>(db, """
            SELECT EXISTS (
                SELECT 1
                FROM pg_attribute
                WHERE attrelid = '"ManualChunks"'::regclass
                    AND attname = 'SearchVector'
                    AND attgenerated = 's')
            """);
        Assert.True(searchVectorIsGenerated);

        var ginIndexExists = await ExecuteScalarAsync<bool>(db, """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE tablename = 'ManualChunks'
                    AND indexdef ILIKE '%USING gin%SearchVector%')
            """);
        Assert.True(ginIndexExists);
    }

    [PostgresIntegrationFact]
    public async Task Retrieval_executes_postgres_search_and_enforces_document_scope()
    {
        await using var db = database.CreateContext();
        var ownManual = CreateManual("user-1", "own-manual.pdf");
        var unrelatedOwnManual = CreateManual("user-1", "unrelated-own-manual.pdf");
        var otherUsersManual = CreateManual("user-2", "other-user-manual.pdf");
        var publicManual = CreateManual(null, "public-manual.pdf");
        var pendingManual = CreateManual("user-1", "pending-manual.pdf", DocumentStatus.Pending);
        var deletedManual = CreateManual("user-1", "deleted-manual.pdf");
        deletedManual.DeletedAtUtc = DateTime.UtcNow;

        db.ManualDocuments.AddRange(
            ownManual, unrelatedOwnManual, otherUsersManual,
            publicManual, pendingManual, deletedManual);
        db.ManualChunks.AddRange(
            CreateChunk(ownManual, "Error code E:20 means inspect the drain pump.", [4], embeddingIndex: 0),
            CreateChunk(unrelatedOwnManual, "Routine cleaning instructions.", [2], embeddingIndex: 1),
            CreateChunk(otherUsersManual, "Error code E:20 means inspect another pump.", [8], embeddingIndex: 0),
            CreateChunk(publicManual, "Error code E:20 means inspect the public pump.", [6], embeddingIndex: 0),
            CreateChunk(pendingManual, "Error code E:20 means inspect the pending pump.", [5], embeddingIndex: 0),
            CreateChunk(deletedManual, "Error code E:20 means inspect the deleted pump.", [7], embeddingIndex: 0));
        await db.SaveChangesAsync();

        var searchVectorMatchesCode = await ExecuteScalarAsync<bool>(
            db,
            """SELECT "SearchVector" @@ to_tsquery('simple', 'e20') FROM "ManualChunks" WHERE "DocumentId" = @documentId""",
            new NpgsqlParameter("documentId", ownManual.Id));
        Assert.True(searchVectorMatchesCode);

        var retriever = new ManualRetriever(
            db,
            new FixedQueryEmbedding(),
            Options.Create(new RetrievalOptions { TopK = 10, CandidateCount = 20 }));

        var userResults = await retriever.RetrieveAsync(
            "What does E20 mean?", RetrievalScope.UserOwn, "user-1");
        Assert.Contains(userResults, result => result.DocumentName == ownManual.DisplayName);
        Assert.Contains(userResults, result => result.DocumentName == unrelatedOwnManual.DisplayName);
        Assert.DoesNotContain(userResults, result => result.DocumentName == otherUsersManual.DisplayName);
        Assert.DoesNotContain(userResults, result => result.DocumentName == publicManual.DisplayName);
        Assert.DoesNotContain(userResults, result => result.DocumentName == pendingManual.DisplayName);
        Assert.DoesNotContain(userResults, result => result.DocumentName == deletedManual.DisplayName);

        var selectedResults = await retriever.RetrieveAsync(
            "What does E20 mean?",
            RetrievalScope.UserOwn,
            "user-1",
            [ownManual.Id]);
        Assert.All(selectedResults, result => Assert.Equal(ownManual.DisplayName, result.DocumentName));

        var anonymousResults = await retriever.RetrieveAsync(
            "What does E20 mean?", RetrievalScope.AnonymousAdminPublic, null);
        Assert.Contains(anonymousResults, result => result.DocumentName == publicManual.DisplayName);
        Assert.DoesNotContain(anonymousResults, result => result.DocumentName == ownManual.DisplayName);
        Assert.DoesNotContain(anonymousResults, result => result.DocumentName == pendingManual.DisplayName);

        var adminResults = await retriever.RetrieveAsync(
            "What does E20 mean?", RetrievalScope.AdminAll, null);
        Assert.Contains(adminResults, result => result.DocumentName == ownManual.DisplayName);
        Assert.Contains(adminResults, result => result.DocumentName == otherUsersManual.DisplayName);
        Assert.Contains(adminResults, result => result.DocumentName == publicManual.DisplayName);
        Assert.DoesNotContain(adminResults, result => result.DocumentName == pendingManual.DisplayName);
        Assert.DoesNotContain(adminResults, result => result.DocumentName == deletedManual.DisplayName);
    }

    private static ManualDocument CreateManual(
        string? ownerUserId,
        string displayName,
        DocumentStatus status = DocumentStatus.Ready) =>
        new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            DisplayName = displayName,
            StorageKey = $"integration-tests/{Guid.NewGuid():N}.pdf",
            Status = status
        };

    private static ManualChunk CreateChunk(
        ManualDocument document,
        string content,
        int[] pageNumbers,
        int embeddingIndex)
    {
        var analyzer = new MultilingualTextAnalyzer();
        var embedding = new float[ApplicationDbContext.EmbeddingDimensions];
        embedding[embeddingIndex] = 1f;
        return new ManualChunk
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            ChunkOrdinal = 1,
            PageNumbers = pageNumbers,
            Content = content,
            EmbeddingText = content,
            SearchText = analyzer.Analyze(content).SearchText,
            Embedding = new Vector(embedding),
            ChunkType = ManualChunkType.Text,
            LanguageCode = "en",
            TokenCount = 12,
            ParserVersion = "integration-test",
            ChunkerVersion = "integration-test",
            EmbeddingModel = "integration-test"
        };
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        ApplicationDbContext db,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var openedConnection = connection.State != ConnectionState.Open;
        if (openedConnection)
            await db.Database.OpenConnectionAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);
            return (T)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("The PostgreSQL integration query returned no result."));
        }
        finally
        {
            if (openedConnection)
                await db.Database.CloseConnectionAsync();
        }
    }

    private sealed class FixedQueryEmbedding : IEmbeddingProvider
    {
        public string ModelId => "postgres-integration-test";

        public Task<ReadOnlyMemory<float>> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = new float[ApplicationDbContext.EmbeddingDimensions];
            embedding[0] = 1f;
            return Task.FromResult<ReadOnlyMemory<float>>(embedding);
        }
    }
}

public sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public PostgresIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(PostgresDatabaseFixture.ConnectionStringEnvironmentVariable)))
            Skip = $"Set {PostgresDatabaseFixture.ConnectionStringEnvironmentVariable} to run PostgreSQL integration tests.";
    }
}

public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionStringEnvironmentVariable =
        "POSTGRES_INTEGRATION_TEST_CONNECTION_STRING";

    private string? databaseName;
    private string? connectionString;

    public async Task InitializeAsync()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
            return;

        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnectionString)
        {
            Database = "postgres"
        };
        databaseName = $"manual_rag_it_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testBuilder = new NpgsqlConnectionStringBuilder(configuredConnectionString)
        {
            Database = databaseName
        };
        connectionString = testBuilder.ConnectionString;

        try
        {
            await using var db = CreateContext();
            await db.Database.MigrateAsync();
        }
        catch
        {
            await DropDatabaseAsync(adminBuilder.ConnectionString);
            throw;
        }
    }

    public ApplicationDbContext CreateContext()
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The PostgreSQL integration database is not initialized.");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .Options;
        return new ApplicationDbContext(options);
    }

    public async Task DisposeAsync()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);
        if (databaseName is null || string.IsNullOrWhiteSpace(configuredConnectionString))
            return;

        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnectionString)
        {
            Database = "postgres"
        };
        await DropDatabaseAsync(adminBuilder.ConnectionString);
    }

    private async Task DropDatabaseAsync(string adminConnectionString)
    {
        if (databaseName is null)
            return;

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
