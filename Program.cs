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

// Add static files serving with explicit file extension content type mapping
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider
    {
        Mappings =
        {
            [".msix"] = "application/msix",
            [".appinstaller"] = "application/appinstaller"
        }
    }
});

app.UseRouting();
app.UseAuthorization();

app.MapRazorPages();
app.MapHub<CopyHub>("/copyHub");

app.Run();
