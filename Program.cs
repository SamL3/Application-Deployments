using ApplicationDeployment.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Remove or comment out the default appsettings.json line if you want to fully replace it
// builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

// Add appconfig.json instead, with reloadOnChange to avoid locking
builder.Configuration.AddJsonFile("appconfig.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddLogging();


var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.MapHub<CopyHub>("/copyHub");
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();
