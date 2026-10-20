using MudBlazor.Services;
using PLCScada.Core;
using PLCScada.Infrastructure;
using PLCScada.Web;
using PLCScada.Web.Components;
using Serilog;
var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
builder.Host.UseSerilog((context, config) => config.ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext().WriteTo.Console().WriteTo.File(Path.Combine(dataDirectory, "logs", "scada-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddScoped<PageUiState>();
var simulatorEnabled = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Simulator:Enabled");
var simulatorPort = builder.Configuration.GetValue("Simulator:Port", 15020);
var connectionString = builder.Configuration.GetConnectionString("Scada")
    ?? $"Data Source={Path.Combine(dataDirectory, "plcscada.db")};Foreign Keys=True;Default Timeout=5";
var retentionOptions = new DataRetentionOptions();
builder.Configuration.GetSection("Retention").Bind(retentionOptions);
builder.Services.AddScada(
    connectionString,
    simulatorEnabled,
    simulatorPort,
    builder.Configuration["RecipeApply:LockDirectory"],
    retentionOptions);
var app = builder.Build();
await app.Services.InitializeScadaAsync(simulatorEnabled && builder.Configuration.GetValue<bool>("Simulator:SeedDemo"), simulatorPort);
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseSerilogRequestLogging();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapGet("/health", () => Results.Ok(new { status = "running", application = "PLCScada" }));
try { await app.RunAsync(); }
finally { await Log.CloseAndFlushAsync(); }
public partial class Program;
