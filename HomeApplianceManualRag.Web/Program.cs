using HomeApplianceManualRag.Web.Components;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HomeApplianceManualRag.Web.Resources;
using HomeApplianceManualRag.Web.Documents;
using HomeApplianceManualRag.Web.Chat;
using HomeApplianceManualRag.Web.Admin;
using HomeApplianceManualRag.Web.Activity;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
ConfigurationValidation.ValidateProduction(builder.Configuration, builder.Environment);

builder.Services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AzureOpenAiOptions>()
    .BindConfiguration(AzureOpenAiOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(options => options.EmbeddingDimensions == ApplicationDbContext.EmbeddingDimensions,
        $"AzureOpenAI:EmbeddingDimensions must be {ApplicationDbContext.EmbeddingDimensions} to match the database vector column.")
    .ValidateOnStart();
builder.Services.AddOptions<UploadOptions>().BindConfiguration(UploadOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RetrievalOptions>().BindConfiguration(RetrievalOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<DoclingOptions>().BindConfiguration(DoclingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TypeSafeOptions>().BindConfiguration(TypeSafeOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<MetadataExtractionOptions>()
    .BindConfiguration(MetadataExtractionOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ImageAnalysisOptions>()
    .BindConfiguration(ImageAnalysisOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHttpClient<DoclingHttpParser>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<DoclingOptions>>().Value;
    client.BaseAddress = new Uri(options.ServiceUrl.TrimEnd('/') + "/");
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHttpClient<TypeSafeManualClassifier>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<TypeSafeOptions>>().Value;
    client.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddOptions<RateLimitingOptions>().BindConfiguration(RateLimitingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ChatOptions>().BindConfiguration(ChatOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SeedAdminOptions>().BindConfiguration(SeedAdminOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LocalStorageOptions>().BindConfiguration(LocalStorageOptions.SectionName);
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.UseVector()));
builder.Services.AddIdentity<ApplicationUser, Microsoft.AspNetCore.Identity.IdentityRole>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/forbidden";
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ApplicationPolicies.AdminOnly, policy => policy.RequireRole(ApplicationRoles.Admin));
    options.AddPolicy(ApplicationPolicies.RegisteredUser, policy => policy.RequireAuthenticatedUser());
});
builder.Services.AddScoped<IChatProvider, AzureOpenAiChatProvider>();
builder.Services.AddScoped<IManualMetadataExtractor, AzureOpenAiManualMetadataExtractor>();
builder.Services.AddScoped<IImageAnalyzer, AzureOpenAiImageAnalyzer>();
builder.Services.AddScoped<IEmbeddingProvider, AzureOpenAiEmbeddingProvider>();
builder.Services.AddScoped<ManualRetriever>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<ChatHistoryService>();
builder.Services.AddScoped<AdminDashboardService>();
builder.Services.AddScoped<AdminUserService>();
builder.Services.AddScoped<ActivityLogService>();
builder.Services.AddScoped<AdminActivityService>();
builder.Services.AddScoped<AdminIndexingJobService>();
builder.Services.AddScoped<AdminConversationService>();
builder.Services.AddScoped<ChatConversationState>();
builder.Services.AddSingleton<ChatRateLimiter>();
builder.Services.AddScoped<PdfValidator>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<IDocumentParser>(serviceProvider =>
    serviceProvider.GetRequiredService<DoclingHttpParser>());
builder.Services.AddScoped<IManualClassifier>(serviceProvider =>
    serviceProvider.GetRequiredService<TypeSafeManualClassifier>());
builder.Services.AddScoped<DocumentChunker>();
builder.Services.AddScoped<IndexingPipeline>();
builder.Services.AddSingleton<DocumentProgressNotifier>();
builder.Services.AddHostedService<IndexingWorker>();
builder.Services.AddScoped<StorageCleanupService>();
builder.Services.AddHostedService<StorageCleanupWorker>();
builder.Services.AddHostedService<ChatHistoryService.AnonymousChatCleanupWorker>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
}

await using (var scope = app.Services.CreateAsyncScope())
    await IdentitySeeder.SeedAsync(scope.ServiceProvider);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (builder.Configuration.GetValue("HttpsRedirection:Enabled", true))
    app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
app.UseAuthentication();
app.UseMiddleware<PageViewLoggingMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/account/login", async (
    HttpContext context,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ActivityLogService activityLog,
    [FromForm] string? email,
    [FromForm] string? password,
    [FromForm] string? returnUrl,
    [FromForm] string? culture) =>
{
    culture ??= context.Request.Headers.AcceptLanguage.ToString();
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return Results.Redirect($"/login?error={Uri.EscapeDataString(IdentityMessages.Required("Email and password", culture))}");
    var user = await userManager.FindByEmailAsync(email);
    if (user?.AccountDisabled == true)
    {
        await activityLog.RecordAsync(
            ActivityTypes.LoginFailed,
            userId: user.Id,
            metadata: new { Succeeded = false, LockedOut = false, AccountDisabled = true });
        return Results.Redirect($"/login?error={Uri.EscapeDataString(IdentityMessages.InvalidCredentials(culture))}");
    }

    var result = await signInManager.PasswordSignInAsync(email, password, isPersistent: false, lockoutOnFailure: true);
    if (result.Succeeded)
    {
        await activityLog.RecordAsync(ActivityTypes.LoginSucceeded, userId: user?.Id);
        return Results.LocalRedirect(IsSafeLocalReturnUrl(returnUrl) ? returnUrl! : "/");
    }
    await activityLog.RecordAsync(
        ActivityTypes.LoginFailed,
        metadata: new { Succeeded = result.Succeeded, LockedOut = result.IsLockedOut });
    return Results.Redirect($"/login?error={Uri.EscapeDataString(IdentityMessages.InvalidCredentials(culture))}");
}).AllowAnonymous().AddEndpointFilter(ValidateAntiforgery);

app.MapPost("/account/register", async (
    HttpContext context,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ActivityLogService activityLog,
    [FromForm] string? email,
    [FromForm] string? password,
    [FromForm] string? confirmPassword,
    [FromForm] string? culture) =>
{
    culture ??= context.Request.Headers.AcceptLanguage.ToString();
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || password != confirmPassword)
        return Results.Redirect($"/register?error={Uri.EscapeDataString(IdentityMessages.RegistrationFailed(culture))}");
    if (await userManager.FindByEmailAsync(email) is not null)
        return Results.Redirect($"/register?error={Uri.EscapeDataString(IdentityMessages.EmailTaken(culture))}");
    var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
    var result = await userManager.CreateAsync(user, password);
    if (!result.Succeeded)
        return Results.Redirect($"/register?error={Uri.EscapeDataString(IdentityMessages.RegistrationFailed(culture))}");
    var roleResult = await userManager.AddToRoleAsync(user, ApplicationRoles.User);
    if (!roleResult.Succeeded)
    {
        var deleteResult = await userManager.DeleteAsync(user);
        if (!deleteResult.Succeeded)
            throw new InvalidOperationException(
                $"Registration role assignment failed and cleanup failed: {string.Join("; ", deleteResult.Errors.Select(x => x.Description))}");
        return Results.Redirect($"/register?error={Uri.EscapeDataString(IdentityMessages.RegistrationFailed(culture))}");
    }
    await signInManager.SignInAsync(user, isPersistent: false);
    await activityLog.RecordAsync(ActivityTypes.UserRegistered, userId: user.Id);
    return Results.Redirect("/");
}).AllowAnonymous().AddEndpointFilter(ValidateAntiforgery);

app.MapPost("/account/logout", async (
    SignInManager<ApplicationUser> signInManager,
    ICurrentUser currentUser,
    ActivityLogService activityLog) =>
{
    var userId = currentUser.UserId;
    await signInManager.SignOutAsync();
    await activityLog.RecordAsync(ActivityTypes.Logout, userId: userId);
    return Results.Redirect("/");
}).RequireAuthorization(ApplicationPolicies.RegisteredUser).AddEndpointFilter(ValidateAntiforgery);

app.MapGet("/documents/{id:guid}/download", async (Guid id, DocumentService documents, HttpContext context) =>
{
    try
    {
        var (document, content) = await documents.OpenReadAsync(id, context.RequestAborted);
        return Results.File(content, document.ContentType, document.DisplayName, enableRangeProcessing: true);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound();
    }
}).RequireAuthorization(ApplicationPolicies.RegisteredUser);

app.MapGet("/activity/page-view", async (
    HttpContext context,
    ActivityLogService activityLog,
    string? path,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(path)
        || !path.StartsWith("/", StringComparison.Ordinal)
        || path.Contains('?', StringComparison.Ordinal)
        || path.Contains('#', StringComparison.Ordinal))
        return Results.BadRequest();

    var visitorId = PageViewLoggingMiddleware.GetOrCreateVisitorId(context);
    await activityLog.RecordPageViewAsync(path, visitorId, cancellationToken);
    return Results.NoContent();
}).AllowAnonymous();

app.MapGet("/account/me", (ICurrentUser currentUser) => Results.Ok(new { currentUser.UserId, currentUser.Email }))
    .RequireAuthorization(ApplicationPolicies.RegisteredUser);

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static bool IsSafeLocalReturnUrl(string? returnUrl) =>
    !string.IsNullOrWhiteSpace(returnUrl)
    && returnUrl.StartsWith("/", StringComparison.Ordinal)
    && !returnUrl.StartsWith("//", StringComparison.Ordinal)
    && !returnUrl.Contains('\\', StringComparison.Ordinal);

static async ValueTask<object?> ValidateAntiforgery(
    EndpointFilterInvocationContext context,
    EndpointFilterDelegate next)
{
    var antiforgery = context.HttpContext.RequestServices
        .GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();
    await antiforgery.ValidateRequestAsync(context.HttpContext);
    return await next(context);
}
