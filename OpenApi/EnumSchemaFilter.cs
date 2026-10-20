using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.OpenApi;

/// <summary>
/// Enums go over the wire as names, so the OpenAPI enum list carries no numbers.
/// This adds what client generators need to rebuild the server enum:
/// x-enum-members with each member's name and numeric value, and x-flags: true for [Flags] enums.
/// Limitation: x-enum-members uses the C# member names. When the wire name differs
/// ([JsonStringEnumMemberName] or a naming policy on the converter) the names do not match the values in the enum list.
/// </summary>
public class EnumSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema concreteSchema)
            return;
        // E? is Nullable<E>, with inline enum definitions its schema is the enum itself
        var type = Nullable.GetUnderlyingType(context.Type) ?? context.Type;
        if (type.IsEnum && concreteSchema.Enum is { Count: > 0 })
            DescribeMembers(concreteSchema, type);
    }

    private static void DescribeMembers(OpenApiSchema enumSchema, Type enumType)
    {
        enumSchema.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        enumSchema.Extensions["x-enum-members"] = new JsonNodeExtension(Members(enumType));
        if (enumType.IsDefined(typeof(FlagsAttribute), false))
            enumSchema.Extensions["x-flags"] = new JsonNodeExtension(JsonValue.Create(true));
    }

    private static JsonArray Members(Type enumType)
    {
        var members = new JsonArray();
        var unsigned64 = Enum.GetUnderlyingType(enumType) == typeof(ulong);
        foreach (var name in Enum.GetNames(enumType))
        {
            var raw = Enum.Parse(enumType, name);
            // ulong values above long.MaxValue would overflow Convert.ToInt64. A ulong JsonValue is
            // written as nothing by the OpenAPI writer (invalid json), so parse it from its text instead
            JsonNode value = unsigned64
                ? JsonNode.Parse(Convert.ToUInt64(raw).ToString(System.Globalization.CultureInfo.InvariantCulture))!
                : JsonValue.Create(Convert.ToInt64(raw));
            members.Add(new JsonObject { ["name"] = name, ["value"] = value });
        }
        return members;
    }
}
