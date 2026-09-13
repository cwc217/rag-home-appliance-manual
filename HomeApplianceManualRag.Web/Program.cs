using HomeApplianceManualRag.Web.Components;
using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
ConfigurationValidation.ValidateProduction(builder.Configuration, builder.Environment);

builder.Services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AzureOpenAiOptions>().BindConfiguration(AzureOpenAiOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<UploadOptions>().BindConfiguration(UploadOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RetrievalOptions>().BindConfiguration(RetrievalOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RateLimitingOptions>().BindConfiguration(RateLimitingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SeedAdminOptions>().BindConfiguration(SeedAdminOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.UseVector()));
builder.Services.AddIdentity<ApplicationUser, Microsoft.AspNetCore.Identity.IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddScoped<IChatProvider, AzureOpenAiChatProvider>();
builder.Services.AddScoped<IEmbeddingProvider, AzureOpenAiEmbeddingProvider>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
