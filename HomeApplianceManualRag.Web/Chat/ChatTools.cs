using System.Text.Json;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Web.Chat;

public sealed record ApplianceManualInfo(
    Guid ApplianceId,
    string ManualName,
    string? ProductName,
    string? ApplianceCategory,
    string? Brand,
    string? ModelNumber);

public sealed record ApplianceCatalogResult(
    string BasedOn,
    IReadOnlyList<ApplianceManualInfo> Appliances,
    bool IsTruncated);

public sealed record ManualSearchResult(IReadOnlyList<RetrievedChunk> Chunks);

public sealed record ChatToolExecution(string Content, IReadOnlyList<RetrievedChunk> Chunks);

public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatToolResult(string CallId, string Content);

public sealed record ChatToolRound(
    IReadOnlyList<ChatToolCall> Calls,
    IReadOnlyList<ChatToolResult> Results);

public sealed record ChatToolDefinition(string Name, string Description, string ParametersJson);

public sealed record ChatProviderUpdate(
    string? Text = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null);

public static class ChatToolDefinitions
{
    public static IReadOnlyList<ChatToolDefinition> All { get; } =
    [
        new(
            "get_my_appliances",
            "List appliance product name, category, brand, and model from the manuals available to the current user. Use for appliance inventory and model lookups.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        new(
            "search_my_manuals",
            "Search accessible appliance manuals for instructions or details. When selected manual IDs are provided by the application, search only within that selection. Use applianceId only to narrow to one selected manual.",
            """{"type":"object","properties":{"query":{"type":"string","description":"The appliance or manual question to search for."},"applianceId":{"type":"string","description":"Optional manual ID returned as applianceId by get_my_appliances."}},"required":["query"],"additionalProperties":false}""")
    ];
}

public sealed class ChatToolsService(
    ApplicationDbContext db,
    ManualRetriever retriever,
    ICurrentUser currentUser,
    IActiveAccountGuard accountGuard,
    IOptions<ChatOptions> chatOptions,
    ILogger<ChatToolsService> logger)
{
    private const int MaxCatalogEntries = 50;
    private const int MaxSearchResults = 10;
    private const int MaxChunkCharacters = 4000;
    private const int MaxArgumentsCharacters = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ApplianceManualInfo>> ListAvailableManualsAsync(
        CancellationToken ct = default)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        return await AccessibleDocuments()
            .Where(x => x.Status == DocumentStatus.Ready && x.DeletedAtUtc == null)
            .OrderBy(x => x.DisplayName)
            .Select(x => new ApplianceManualInfo(
                x.Id, x.DisplayName, x.ProductName, x.ApplianceCategory, x.Brand, x.ModelNumber))
            .ToListAsync(ct);
    }

    public async Task ValidateSelectedManualsAsync(
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken ct = default)
    {
        var selectedIds = documentIds.Distinct().ToArray();
        if (selectedIds.Length == 0)
            return;

        await accountGuard.EnsureEnabledAsync(ct);
        var availableCount = await AccessibleDocuments()
            .Where(x => x.Status == DocumentStatus.Ready &&
                        x.DeletedAtUtc == null &&
                        selectedIds.Contains(x.Id))
            .CountAsync(ct);
        if (availableCount != selectedIds.Length)
            throw new ArgumentException(
                "One or more selected manuals are no longer available. Refresh the page and try again.",
                nameof(documentIds));
    }

    public async Task<ChatToolExecution> ExecuteAsync(
        ChatToolCall call,
        CancellationToken ct = default,
        IReadOnlyCollection<Guid>? selectedDocumentIds = null)
    {
        if (selectedDocumentIds is { Count: 0 })
            selectedDocumentIds = null;

        if (call.ArgumentsJson.Length > MaxArgumentsCharacters)
            return Error("Tool arguments exceed the allowed size.");

        JsonDocument arguments;
        try
        {
            arguments = JsonDocument.Parse(call.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Chat tool {ToolName} returned invalid JSON arguments.", call.Name);
            return Error("Tool arguments must be a JSON object.");
        }

        using (arguments)
        {
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                return Error("Tool arguments must be a JSON object.");

            return call.Name switch
            {
                "get_my_appliances" => await ExecuteApplianceCatalogAsync(arguments.RootElement, selectedDocumentIds, ct),
                "search_my_manuals" => await ExecuteManualSearchAsync(arguments.RootElement, selectedDocumentIds, ct),
                _ => Error("This tool is not available.")
            };
        }
    }

    private async Task<ChatToolExecution> ExecuteApplianceCatalogAsync(
        JsonElement arguments,
        IReadOnlyCollection<Guid>? selectedDocumentIds,
        CancellationToken ct)
    {
        if (arguments.EnumerateObject().Any())
            return Error("get_my_appliances does not accept arguments.");

        await accountGuard.EnsureEnabledAsync(ct);
        var query = AccessibleDocuments()
            .Where(x => x.Status == DocumentStatus.Ready && x.DeletedAtUtc == null)
            .Where(x => selectedDocumentIds == null || selectedDocumentIds.Contains(x.Id))
            .OrderBy(x => x.DisplayName)
            .Select(x => new ApplianceManualInfo(
                x.Id, x.DisplayName, x.ProductName, x.ApplianceCategory, x.Brand, x.ModelNumber));
        var appliances = await query.Take(MaxCatalogEntries + 1).ToListAsync(ct);
        var basis = !currentUser.IsAuthenticated
            ? "public manuals"
            : currentUser.IsInRole(ApplicationRoles.Admin)
                ? "all accessible manuals"
                : "manuals uploaded to this account";
        var result = new ApplianceCatalogResult(
            basis, appliances.Take(MaxCatalogEntries).ToArray(), appliances.Count > MaxCatalogEntries);
        return new ChatToolExecution(JsonSerializer.Serialize(result, JsonOptions), []);
    }

    private async Task<ChatToolExecution> ExecuteManualSearchAsync(
        JsonElement arguments,
        IReadOnlyCollection<Guid>? selectedDocumentIds,
        CancellationToken ct)
    {
        var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "query", "applianceId"
        };
        if (arguments.EnumerateObject().Any(property => !allowedProperties.Contains(property.Name)))
            return Error("search_my_manuals accepts only query and applianceId.");
        if (!arguments.TryGetProperty("query", out var queryElement) ||
            queryElement.ValueKind != JsonValueKind.String)
            return Error("A text query is required.");

        var query = queryElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return Error("A non-empty text query is required.");
        if (query.Length > chatOptions.Value.MaxQuestionLength)
            return Error($"The query cannot exceed {chatOptions.Value.MaxQuestionLength} characters.");

        Guid? applianceId = null;
        if (arguments.TryGetProperty("applianceId", out var idElement))
        {
            if (idElement.ValueKind == JsonValueKind.Null)
                applianceId = null;
            else if (idElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(idElement.GetString(), out var parsedId))
                return Error("applianceId must be a valid appliance ID from the catalog.");
            else
                applianceId = parsedId;
        }

        if (applianceId is Guid requestedId &&
            selectedDocumentIds is not null &&
            !selectedDocumentIds.Contains(requestedId))
            return Error("The requested appliance is outside the selected manuals.");

        await accountGuard.EnsureEnabledAsync(ct);
        var scope = GetRetrievalScope();
        IReadOnlyCollection<Guid>? documentIds = applianceId is Guid selectedId
            ? [selectedId]
            : selectedDocumentIds;
        var results = await retriever.RetrieveAsync(
            query, scope, currentUser.UserId, documentIds, ct);
        var boundedResults = results
            .Take(MaxSearchResults)
            .Select(chunk => chunk with
            {
                Text = chunk.Text.Length <= MaxChunkCharacters
                    ? chunk.Text
                    : chunk.Text[..MaxChunkCharacters]
            })
            .ToArray();
        return new ChatToolExecution(
            JsonSerializer.Serialize(new ManualSearchResult(boundedResults), JsonOptions),
            boundedResults);
    }

    private IQueryable<ManualDocument> AccessibleDocuments()
    {
        var query = db.ManualDocuments.AsNoTracking();
        return GetRetrievalScope() switch
        {
            RetrievalScope.AnonymousAdminPublic => query.Where(x => x.OwnerUserId == null),
            RetrievalScope.UserOwn when !string.IsNullOrWhiteSpace(currentUser.UserId) =>
                query.Where(x => x.OwnerUserId == currentUser.UserId),
            RetrievalScope.AdminAll => query,
            _ => query.Where(_ => false)
        };
    }

    private RetrievalScope GetRetrievalScope() =>
        currentUser.IsInRole(ApplicationRoles.Admin)
            ? RetrievalScope.AdminAll
            : currentUser.IsAuthenticated
                ? RetrievalScope.UserOwn
                : RetrievalScope.AnonymousAdminPublic;

    private static ChatToolExecution Error(string message) =>
        new(JsonSerializer.Serialize(new { error = message }), []);
}
