using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.OpenApi;

/// <summary>
/// Leaves the type of object? properties open. Their schema type is only "null", written as enum: [null] in OpenAPI 3.0,
/// which openapi-generator turns into an enum that does not compile. Runs on the whole document because
/// dictionary values and allOf wrappers never pass through schema filters. Only plain null-typed schemas are changed,
/// references and schemas that carry composition, enum, properties, items or additionalProperties are left alone.
/// </summary>
public class NullOnlySchemaDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        OpenApiSchemaWalker.Visit(swaggerDoc, schema =>
        {
            if (schema is OpenApiSchema s && s.Type == JsonSchemaType.Null
                && s.AllOf is not { Count: > 0 } && s.OneOf is not { Count: > 0 } && s.AnyOf is not { Count: > 0 }
                && s.Enum is not { Count: > 0 } && s.Properties is not { Count: > 0 }
                && s.Items == null && s.AdditionalProperties == null)
                s.Type = null;
        });
    }
}
