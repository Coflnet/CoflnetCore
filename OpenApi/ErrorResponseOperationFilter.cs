using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.OpenApi;

public class ErrorResponseOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Responses["400"] = new OpenApiResponse
        {
            Description = "Bad Request",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                {
                    "application/json", new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                {
                                    "slug", new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Description = "Human readable id for this kind of error"
                                    }
                                },
                                {
                                    "message", new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Description = "More info about the error, may sometimes be sufficient to display to user"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
        operation.Responses["500"] = new OpenApiResponse
        {
            Description = "Internal Server Error",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                {
                    "application/json", new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                {
                                    "slug", new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Description = "Human readable id for this kind of error"
                                    }
                                },
                                {
                                    "message", new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Description = "Unknown error occured"
                                    }
                                },
                                {
                                    "trace", new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Description = "Id for the error report with this id"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }
}