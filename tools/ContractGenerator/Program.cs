using NSwag;
using NSwag.CodeGeneration.CSharp;
using NSwag.CodeGeneration.CSharp.Models;
using NJsonSchema.CodeGeneration.CSharp;
using System.Text.Json.Nodes;

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--check"))
{
    Console.Error.WriteLine("Usage: ContractGenerator OPENAPI_JSON GENERATED_CS [--check]");
    return 2;
}

var source = JsonNode.Parse(await File.ReadAllTextAsync(args[0]))!;
// DTO code cannot express the input XOR invariant. Preserve the declared fields
// while leaving oneOf enforcement to request validation against the source contract.
var schemas = source["components"]!["schemas"]!;
schemas["StreamInput"]!.AsObject().Remove("oneOf");
schemas["WriteStdinRequest"]!.AsObject().Remove("oneOf");
// The embedded specification is served directly by the host, not a DTO action.
source["paths"]!.AsObject().Remove("/v1/openapi.json");
var document = await OpenApiDocument.FromJsonAsync(source.ToJsonString());
var settings = new CSharpControllerGeneratorSettings
{
    ClassName = "ExecServerApi",
    ControllerTarget = CSharpControllerTarget.AspNetCore,
    ControllerStyle = CSharpControllerStyle.Abstract,
    GenerateModelValidationAttributes = true,
    UseActionResultType = true,
    UseCancellationToken = true,
    GenerateOptionalParameters = true,
};
settings.CSharpGeneratorSettings.Namespace = "ExecServer.Contracts";
settings.CSharpGeneratorSettings.JsonLibrary = CSharpJsonLibrary.SystemTextJson;
settings.CSharpGeneratorSettings.GenerateNullableReferenceTypes = true;
settings.CSharpGeneratorSettings.GenerateOptionalPropertiesAsNullable = true;
settings.CSharpGeneratorSettings.DateTimeType = "System.DateTimeOffset";
var output = new CSharpControllerGenerator(document, settings).GenerateFile();
if (args.Length == 3)
{
    if (!File.Exists(args[1]) || await File.ReadAllTextAsync(args[1]) != output)
    {
        Console.Error.WriteLine("Generated contract is stale; regenerate it from OpenAPI.");
        return 1;
    }
    Console.WriteLine("Generated contract matches OpenAPI.");
    return 0;
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
await File.WriteAllTextAsync(args[1], output);
return 0;
