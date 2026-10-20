using System.Reflection;
using Coflnet.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.OpenApi;

public static class OpenApiExtension
{
    /// <summary>
    /// Component schema id for <see cref="ApiException"/>. Deliberately not "ErrorResponse" so it can't
    /// collide with a service's own ErrorResponse class or <see cref="ErrorResponse"/>.
    /// </summary>
    public const string ApiErrorResponseSchemaId = "ApiErrorResponse";

    public static void AddCoflnetOpenApi(this IServiceCollection services, string title)
    {
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = title,
                Version = "v1",
                Description = ""
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
                In = ParameterLocation.Header,
                Name = "Authorization",
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });
            c.CustomOperationIds(apiDesc =>
            {
                return apiDesc.TryGetMethodInfo(out MethodInfo methodInfo) ? methodInfo.Name : "xy";
            });
            c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecuritySchemeReference("Bearer"),
                    new List<string>()
                }
            });
            // Set the comments path for the Swagger JSON and UI.
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath))
                c.IncludeXmlComments(xmlPath, true);
            // main assembly this one is imported into
            var mainAssembly = Assembly.GetEntryAssembly()?.GetName().Name;
            if (mainAssembly != null)
            {
                xmlFile = $"{mainAssembly}.xml";
                xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                if (File.Exists(xmlPath))
                    c.IncludeXmlComments(xmlPath, true);
            }
            c.OperationFilter<ErrorResponseOperationFilter>();
            // ApiException is an System.Exception, describing it by reflection would pull the whole
            // reflection graph (Type, Assembly, MethodBase, ...) into the document. What is really sent
            // is the error body written by ErrorHandler, so document that as one shared component.
            // subclasses (ApiStatusException, ...) are mapped too, the assemblies are scanned when the options are first resolved
            foreach (var type in ApiExceptionTypes())
                c.MapType(type, () => new OpenApiSchemaReference(ApiErrorResponseSchemaId));
            c.DocumentFilter<ApiErrorResponseDocumentFilter>();
            c.DocumentFilter<NullOnlySchemaDocumentFilter>();
            c.SchemaFilter<EnumSchemaFilter>();
        });
    }

    private static IEnumerable<Type> ApiExceptionTypes()
    {
        yield return typeof(ApiException);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;
            Type?[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch (Exception) { continue; }
            foreach (var type in types)
                if (type != null && type != typeof(ApiException) && !type.IsAbstract && !type.ContainsGenericParameters
                    && typeof(ApiException).IsAssignableFrom(type))
                    yield return type;
        }
    }

    public static void UseOpenApi(this IApplicationBuilder app, string title)
    {
        app.UseSwagger(c =>
        {
            c.RouteTemplate = "api/openapi/{documentName}/openapi.json";
        })
        .UseSwaggerUI(c =>
        {
            c.RoutePrefix = "api";
            c.SwaggerEndpoint("/api/openapi/v1/openapi.json", title);
            c.EnablePersistAuthorization();
            c.EnableTryItOutByDefault();
        });
    }
}

/// <summary>
/// Registers the <see cref="OpenApiExtension.ApiErrorResponseSchemaId"/> component, mirrors what ErrorHandler serializes (camelCase).
/// Only added when the document references it. A service type with the same schema id is a clash and throws.
/// </summary>
public class ApiErrorResponseDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var id = OpenApiExtension.ApiErrorResponseSchemaId;
        if (swaggerDoc.Components?.Schemas?.ContainsKey(id) == true)
            throw new InvalidOperationException($"A type named {id} is used in the OpenAPI document and clashes with the {id} schema Coflnet.Core generates for ApiException. Rename that type.");
        var referenced = false;
        OpenApiSchemaWalker.Visit(swaggerDoc, schema =>
        {
            if (schema is OpenApiSchemaReference reference && reference.Reference?.Id == id)
                referenced = true;
        });
        if (!referenced)
            return;
        swaggerDoc.Components ??= new OpenApiComponents();
        swaggerDoc.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        swaggerDoc.Components.Schemas[id] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "Error body returned for an ApiException",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["slug"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Human readable id for this kind of error" },
                ["message"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "More info about the error, may sometimes be sufficient to display to user" },
                ["trace"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, Description = "Opentelemetry trace id, null for ApiException" }
            }
        };
    }
}
