using DevApp.Hubs;
using DevApp.Services;
using DevApp.Options;

var builder = WebApplication.CreateBuilder(args);

// appsettings*.json and environment variables are loaded by default.
// Add custom appconfig.json (kept optional to avoid hard crashes on new servers)
builder.Configuration.AddJsonFile("appconfig.json", optional: true, reloadOnChange: true);

// Strongly-typed options + validation
builder.Services.AddOptions<ApiTestsOptions>()
    .Bind(builder.Configuration.GetSection("ApiTests"))
    .Validate(o => o.Items?.Count >= 0, "ApiTests binding failed")
    .ValidateOnStart();

builder.Services.AddOptions<HostAvailabilityOptions>()
    .Bind(builder.Configuration.GetSection("HostAvailability"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddLogging();

builder.Services.AddSingleton<HostAvailabilityService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostAvailabilityService>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapRazorPages();
app.MapHub<CopyHub>("/copyHub");

app.Run();
