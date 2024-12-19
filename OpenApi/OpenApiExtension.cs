using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.OpenApi;

public static class OpenApiExtension
{
    public static void AddOpenApi(this IServiceCollection services, string title)
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
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    new string[] { }
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
        });
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