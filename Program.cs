using ApplicationDeployment.Hubs;
using ApplicationDeployment.Services;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appconfig.json", optional: false, reloadOnChange: true);

// Services
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddLogging();

// Host availability
builder.Services.AddSingleton<HostAvailabilityService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostAvailabilityService>());

var app = builder.Build();

// Pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapRazorPages();
app.MapHub<CopyHub>("/copyHub");

// Simple API endpoints (optional if not using page handlers)
app.MapGet("/api/hosts/status", (HostAvailabilityService svc) =>
{
    var statuses = svc.GetStatuses().Values
        .OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase);
    return Results.Json(new
    {
        scanInProgress = svc.ScanInProgress,
        completed = svc.Completed,
        total = svc.Total,
        statuses
    });
});

app.MapPost("/api/hosts/refresh", async (HostAvailabilityService svc) =>
{
    await svc.TriggerScanAsync();
    return Results.Accepted("/api/hosts/status");
});

app.Run();
