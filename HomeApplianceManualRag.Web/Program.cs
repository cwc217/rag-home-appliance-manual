using HomeApplianceManualRag.Web.Components;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using HomeApplianceManualRag.Web.Resources;
using HomeApplianceManualRag.Web.Documents;

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
builder.Services.AddOptions<RateLimitingOptions>().BindConfiguration(RateLimitingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
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
builder.Services.AddScoped<IEmbeddingProvider, AzureOpenAiEmbeddingProvider>();
builder.Services.AddScoped<PdfValidator>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<IDocumentParser, DoclingProcessParser>();
builder.Services.AddScoped<DocumentChunker>();
builder.Services.AddScoped<IndexingPipeline>();
builder.Services.AddSingleton<DocumentProgressNotifier>();
builder.Services.AddHostedService<IndexingWorker>();
builder.Services.AddScoped<StorageCleanupService>();
builder.Services.AddHostedService<StorageCleanupWorker>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
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
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/account/login", async (
    HttpContext context,
    SignInManager<ApplicationUser> signInManager,
    [FromForm] string? email,
    [FromForm] string? password,
    [FromForm] string? returnUrl,
    [FromForm] string? culture) =>
{
    culture ??= context.Request.Headers.AcceptLanguage.ToString();
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return Results.Redirect($"/login?error={Uri.EscapeDataString(IdentityMessages.Required("Email and password", culture))}");
    var result = await signInManager.PasswordSignInAsync(email, password, isPersistent: false, lockoutOnFailure: false);
    if (result.Succeeded)
        return Results.LocalRedirect(IsSafeLocalReturnUrl(returnUrl) ? returnUrl! : "/");
    return Results.Redirect($"/login?error={Uri.EscapeDataString(IdentityMessages.InvalidCredentials(culture))}");
}).AllowAnonymous();

app.MapPost("/account/register", async (
    HttpContext context,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
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
    return Results.Redirect("/");
}).AllowAnonymous();

app.MapPost("/account/logout", async (SignInManager<ApplicationUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/");
}).RequireAuthorization(ApplicationPolicies.RegisteredUser);

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

app.MapGet("/admin", () => Results.Ok(new { message = "Admin area" })).RequireAuthorization(ApplicationPolicies.AdminOnly);
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
