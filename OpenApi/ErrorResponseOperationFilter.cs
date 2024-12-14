using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.OpenApi;

public class ErrorResponseOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Responses.Add("400", new OpenApiResponse
        {
            Description = "Bad Request",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                {
                    "application/json", new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, OpenApiSchema>
                            {
                                {
                                    "slug", new OpenApiSchema
                                    {
                                        Type = "string",
                                        Description = "Human readable id for this kind of error"
                                    }
                                },
                                {
                                    "message", new OpenApiSchema
                                    {
                                        Type = "string",
                                        Description = "More info about the error, may sometimes be sufficient to display to user"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });
        operation.Responses.Add("500", new OpenApiResponse
        {
            Description = "Internal Server Error",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                {
                    "application/json", new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, OpenApiSchema>
                            {
                                {
                                    "slug", new OpenApiSchema
                                    {
                                        Type = "string",
                                        Description = "Human readable id for this kind of error"
                                    }
                                },
                                {
                                    "message", new OpenApiSchema
                                    {
                                        Type = "string",
                                        Description = "Unknown error occured"
                                    }
                                },
                                {
                                    "trace", new OpenApiSchema
                                    {
                                        Type = "string",
                                        Description = "Id for the error report with this id"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });
    }
}