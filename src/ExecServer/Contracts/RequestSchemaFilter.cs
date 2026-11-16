using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ExecServer.Contracts;

/// <summary>Validate original JSON against the normative contract before deserialization erases absence/null distinctions.</summary>
public sealed class RequestSchemaFilter : IAsyncResourceFilter
{
    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    public RequestSchemaFilter()
    {
        using Stream stream = typeof(RequestSchemaFilter).Assembly.GetManifestResourceStream("ExecServer.OpenApi.json")!;
        JsonNode document = JsonNode.Parse(stream)!;
        string definitions = document["components"]!["schemas"]!.ToJsonString()
            .Replace("#/components/schemas/", "#/$defs/", StringComparison.Ordinal);
        foreach (string name in new[] { "StartExecutionRequest", "WriteStdinRequest", "SignalRequest" })
        {
            var schema = new JsonObject
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["$ref"] = "#/$defs/" + name,
                ["$defs"] = JsonNode.Parse(definitions)
            };
            using var parsed = JsonDocument.Parse(schema.ToJsonString());
            _schemas[name] = JsonSchema.Build(parsed.RootElement.Clone(), new BuildOptions
            {
                Dialect = Dialect.Draft202012, SchemaRegistry = new()
            });
        }
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action) { await next(); return; }
        var body = action.Parameters.FirstOrDefault(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body);
        if (body is null || !_schemas.TryGetValue(body.ParameterType.Name, out var schema)) { await next(); return; }
        var request = context.HttpContext.Request;
        if (!request.HasJsonContentType())
        {
            context.Result = Error();
            return;
        }
        request.EnableBuffering();
        try
        {
            using var value = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.HttpContext.RequestAborted);
            if (!schema.Evaluate(value.RootElement).IsValid) { context.Result = Error(); return; }
        }
        catch (JsonException) { context.Result = Error(); return; }
        finally { request.Body.Position = 0; }
        await next();
    }

    private static BadRequestObjectResult Error() => new(new ApiError
    {
        Code = "invalid_request", Message = "Request does not conform to the OpenAPI schema."
    });
}
