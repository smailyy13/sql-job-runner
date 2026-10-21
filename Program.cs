using SqlJobRunner;

var builder = WebApplication.CreateBuilder(args);
var demoMode = builder.Configuration.GetValue<bool>("DemoMode");
if (demoMode) builder.Configuration["DataDirectory"] = builder.Configuration["DemoDataDirectory"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlJobRunnerDemo");
builder.Host.UseWindowsService(options => options.ServiceName = "SqlJobRunner");
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<AgentClient>();
builder.Services.AddSingleton<IAgentClient>(sp => demoMode ? new DemoAgentClient() : sp.GetRequiredService<AgentClient>());
builder.Services.AddHostedService<JobWorker>();
var app = builder.Build();
// One process per data directory.
var storeInstance = app.Services.GetRequiredService<Store>();
using var instanceLock = new FileStream(Path.Combine(storeInstance.DataDirectory, "runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
app.Urls.Clear(); app.Urls.Add(demoMode ? "http://127.0.0.1:5296" : "http://127.0.0.1:5295");
if (demoMode && storeInstance.GetConnection() is null) storeInstance.SetConnection(new ConnectionInput(Server: "DEMO"));

app.Use(async (context, next) =>
{
    // Local binding alone does not protect against cross-site requests / DNS rebinding.
    if (context.Request.Host.Host is not ("localhost" or "127.0.0.1")) { context.Response.StatusCode = 403; return; }
    if (context.Request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != $"http://{context.Request.Host}")
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "İstek başarısız: {Path}", context.Request.Path);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "İşlem tamamlanamadı. Bağlantıyı ve uygulama loglarını kontrol edin." });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();

app.MapGet("/api/state", (Store store) =>
{
    var c = store.GetConnection();
    var jobs=store.Jobs();
    return Results.Ok(new
    {
        connection = c is null ? null : store.View(c),
        demoMode,
        jobs,
        runs = store.Runs().Select(r => new { r.Id, r.JobId, r.Definition, r.Status, r.CreatedUtc, r.FinishedUtc, r.CurrentStep, r.Message, r.AgentJobName, r.AgentJobId, r.GeneratorJobName, r.StartRequested, steps = System.Text.Json.JsonSerializer.Deserialize<object>(r.StepsJson) }),
        notifications = store.Notifications(), serverUtc = DateTimeOffset.UtcNow
    });
});
app.MapPut("/api/connection", (ConnectionInput input, Store store) =>
{
    if (string.IsNullOrWhiteSpace(input.Server)) throw new ArgumentException("Sunucu zorunlu.");
    if (!input.IntegratedSecurity && string.IsNullOrWhiteSpace(input.UserName)) throw new ArgumentException("SQL kullanıcı adı zorunlu.");
    store.SetConnection(input); return Results.Ok(new { saved = true });
});
app.MapPost("/api/connection/test", async (Store store, AgentClient client, CancellationToken ct) =>
{
    var connection = store.GetConnection() ?? throw new InvalidOperationException("Önce bağlantıyı kaydedin.");
    try { return Results.Ok(demoMode ? DemoAgentClient.Test(connection) : await client.Test(connection, ct)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/connections/recent", (Store store) => Results.Ok(store.RecentConnections()));
app.MapDelete("/api/connections/recent/{id}", (string id,Store store) => { store.ForgetConnection(id); return Results.NoContent(); });
app.MapPost("/api/connections/test", async (ConnectionInput input,Store store,AgentClient client,CancellationToken ct) =>
{
    try { return Results.Ok(demoMode ? DemoAgentClient.Test(input) : await client.Test(store.DraftConnection(input),ct)); }
    catch(Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/jobs", (JobDefinition job, Store store) =>
{
    GeneratorContract.Validate(job);
    var clean = job with { JobName = job.JobName.Trim(), AgentJobName = "" };
    store.SaveJob(clean);return Results.Ok(clean);
});
app.MapPost("/api/jobs/execute", (ExecuteJobRequest request,Store store) =>
{
    var run=store.Execute(request.Job,request.RequestId);
    return Results.Ok(new {runId=run.Id,jobId=run.JobId});
});
app.MapDelete("/api/jobs/{id}", (string id, Store store) => { store.DeleteJob(id); return Results.NoContent(); });
app.MapPost("/api/jobs/{id}/run", (string id, Store store) => Results.Ok(new { id=store.Enqueue(id).Id }));
app.MapPost("/api/runs/{id}/close-tracking", (string id, Store store) => { store.CloseTracking(id); return Results.NoContent(); });
app.MapPost("/api/notifications/read", (Store store) => { store.ReadNotifications(); return Results.NoContent(); });
if (demoMode) app.MapGet("/api/demo/cases", () => DemoAgentClient.Cases);
app.Run();
