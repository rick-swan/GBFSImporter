using GbfsSystemsImporter.Services;
using GbfsSystemsImporter.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();

builder.Services.AddScoped<DatabaseService>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var sql = cfg.GetSection("Sql");
    var host = sql["Host"] ?? "localhost";
    var port = int.Parse(sql["Port"] ?? "51919");
    var password = sql["Password"]
        ?? Environment.GetEnvironmentVariable("GBFS_SQL_PASSWORD")
        ?? "Yukon2025!Gbfs";
    var conn = DatabaseService.BuildConnectionString(host, port, password);
    return new DatabaseService(conn);
});

builder.Services.AddSingleton<GbfsFeedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>();

app.Run();
