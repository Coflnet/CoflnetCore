using Microsoft.OpenApi;

namespace Coflnet.OpenApi;

/// <summary>
/// Visits every schema of a document: components, paths, nested properties, items, additionalProperties and composition children.
/// References are passed to the visitor but not followed (their target is a component and visited there).
/// </summary>
public static class OpenApiSchemaWalker
{
    public static void Visit(OpenApiDocument document, Action<IOpenApiSchema> visitor)
    {
        var seen = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);
        void Schema(IOpenApiSchema? schema)
        {
            if (schema == null || !seen.Add(schema))
                return;
            visitor(schema);
            if (schema is not OpenApiSchema concrete)
                return;
            foreach (var child in concrete.Properties?.Values ?? Enumerable.Empty<IOpenApiSchema>())
                Schema(child);
            foreach (var child in concrete.PatternProperties?.Values ?? Enumerable.Empty<IOpenApiSchema>())
                Schema(child);
            foreach (var child in concrete.AllOf ?? Enumerable.Empty<IOpenApiSchema>())
                Schema(child);
            foreach (var child in concrete.OneOf ?? Enumerable.Empty<IOpenApiSchema>())
                Schema(child);
            foreach (var child in concrete.AnyOf ?? Enumerable.Empty<IOpenApiSchema>())
                Schema(child);
            Schema(concrete.Items);
            Schema(concrete.AdditionalProperties);
            Schema(concrete.Not);
        }
        void Content(IDictionary<string, OpenApiMediaType>? content)
        {
            foreach (var media in content?.Values ?? Enumerable.Empty<OpenApiMediaType>())
                Schema(media.Schema);
        }
        void Parameter(IOpenApiParameter? parameter)
        {
            if (parameter == null || parameter is IOpenApiReferenceHolder)
                return;
            Schema(parameter.Schema);
            Content(parameter.Content);
        }
        void RequestBody(IOpenApiRequestBody? body)
        {
            if (body != null && body is not IOpenApiReferenceHolder)
                Content(body.Content);
        }
        void Response(IOpenApiResponse? response)
        {
            if (response == null || response is IOpenApiReferenceHolder)
                return;
            Content(response.Content);
            foreach (var header in response.Headers?.Values ?? Enumerable.Empty<IOpenApiHeader>())
            {
                if (header is IOpenApiReferenceHolder)
                    continue;
                Schema(header.Schema);
                Content(header.Content);
            }
        }

        var components = document.Components;
        if (components != null)
        {
            foreach (var schema in components.Schemas?.Values ?? Enumerable.Empty<IOpenApiSchema>())
                Schema(schema);
            foreach (var parameter in components.Parameters?.Values ?? Enumerable.Empty<IOpenApiParameter>())
                Parameter(parameter);
            foreach (var body in components.RequestBodies?.Values ?? Enumerable.Empty<IOpenApiRequestBody>())
                RequestBody(body);
            foreach (var response in components.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>())
                Response(response);
        }
        foreach (var path in document.Paths?.Values ?? Enumerable.Empty<IOpenApiPathItem>())
        {
            foreach (var parameter in path.Parameters ?? Enumerable.Empty<IOpenApiParameter>())
                Parameter(parameter);
            foreach (var operation in path.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
            {
                foreach (var parameter in operation.Parameters ?? Enumerable.Empty<IOpenApiParameter>())
                    Parameter(parameter);
                RequestBody(operation.RequestBody);
                foreach (var response in operation.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>())
                    Response(response);
            }
        }
    }
}
