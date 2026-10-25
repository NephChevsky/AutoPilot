using AutoPilot.Components;
using AutoPilot.Services;
using GitHub.Copilot;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<AutoPilotSettings>(builder.Configuration.GetSection(AutoPilotSettings.SectionName));
builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<AutoPilotSettings>>().Value;
    var copilotDirectory = settings.ResolveCopilotDirectory();
    if (copilotDirectory is not null)
    {
        Directory.CreateDirectory(copilotDirectory);
    }

    return new CopilotClient(new CopilotClientOptions
    {
        WorkingDirectory = settings.ResolveWorkingDirectory(),
        BaseDirectory = copilotDirectory,
        // Falls back to the logged-in user when no token is configured.
        GitHubToken = string.IsNullOrWhiteSpace(builder.Configuration["Copilot:Token"]) ? null : builder.Configuration["Copilot:Token"],
        Logger = sp.GetRequiredService<ILogger<CopilotClient>>(),
    });
});
builder.Services.AddSingleton(sp => new UsageLedger(
    sp.GetRequiredService<IOptions<AutoPilotSettings>>().Value.ResolveUsageFile(),
    sp.GetRequiredService<ILogger<UsageLedger>>()));
builder.Services.AddSingleton(sp => new TaskStore(
    sp.GetRequiredService<IOptions<AutoPilotSettings>>().Value.ResolveTasksFile(),
    sp.GetRequiredService<ILogger<TaskStore>>()));
builder.Services.AddSingleton<RepositoryCatalog>();
builder.Services.AddScoped<AgentChatSession>();

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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
