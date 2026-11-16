using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExecServer;
using ExecServer.Contracts;
using ExecServer.Diagnostics;
using ExecServer.Processes;
using ExecServer.Storage;
using Microsoft.AspNetCore.Mvc;

if (args.Length > 0 && args[0] == "--exec-child")
{
    Environment.ExitCode = UnixLauncher.Run(args[1..]);
    return;
}
try
{
    var builder = WebApplication.CreateBuilder(args);
    string? apiKey = Environment.GetEnvironmentVariable("EXECSERVER_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("EXECSERVER_API_KEY must be set; the server refuses to run without authentication.");
    byte[] keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
    var settings = new ServerSettings();
    builder.Configuration.GetSection("ExecServer").Bind(settings);
    settings.Validate();
    if (builder.Configuration["urls"] is null) builder.WebHost.UseUrls("http://127.0.0.1:5080");
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole();
    builder.Logging.AddProvider(new JsonFileLoggerProvider(settings));
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton<ExecutionManager>();
    builder.Services.AddSingleton<IHostLifetime, DrainLifetime>();
    builder.Services.AddSingleton(new RequestSchemaFilter());
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
    builder.Services.AddControllers(options => options.Filters.AddService<RequestSchemaFilter>()).AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });
    builder.Services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = _ =>
        new BadRequestObjectResult(new ApiError { Code = "invalid_request", Message = "Request does not conform to the API schema." }));
    using var spoolLease = new SpoolLease(settings.SpoolDirectory);
    var app = builder.Build();
    app.Use(async (context, next) =>
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string? authorization = context.Request.Headers.Authorization;
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new ApiError { Code = "unauthorized", Message = "A valid Bearer API key is required." });
            app.Logger.LogWarning("Authentication refused {RequestId} {Method} {Path}", context.TraceIdentifier, context.Request.Method, context.Request.Path.Value);
            return;
        }
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (error is ApiFault or ArgumentException or FormatException or InvalidOperationException
            or IOException or UnauthorizedAccessException or Win32Exception)
        {
            if (context.Response.HasStarted) throw;
            var fault = error as ApiFault;
            context.Response.StatusCode = fault?.Status ?? (error switch
            {
                InvalidOperationException => 409,
                ArgumentException or FormatException => 400,
                _ => 500
            });
            await context.Response.WriteAsJsonAsync(new ApiError
            {
                Code = fault?.Code ?? "operation_failed",
                Message = error.Message,
                AccumulatedBytes = fault?.AccumulatedBytes
            });
        }
        finally
        {
            app.Logger.LogInformation("HTTP operation {RequestId} {Method} {Path} {Query} {Status} {DurationMilliseconds}",
                context.TraceIdentifier, context.Request.Method, context.Request.Path.Value, context.Request.QueryString.Value,
                context.Response.StatusCode, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    });
    app.MapControllers();
    app.MapGet("/v1/openapi.json", async context =>
    {
        context.Response.ContentType = "application/json";
        using Stream spec = typeof(ServerSettings).Assembly.GetManifestResourceStream("ExecServer.OpenApi.json")!;
        await spec.CopyToAsync(context.Response.Body, context.RequestAborted);
    });
    await app.RunAsync();
}
catch (Exception error) when (error is InvalidOperationException or IOException
    or UnauthorizedAccessException or ArgumentException or JsonException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new ApiError { Code = "startup_failed", Message = error.Message }));
    Environment.ExitCode = 2;
}
