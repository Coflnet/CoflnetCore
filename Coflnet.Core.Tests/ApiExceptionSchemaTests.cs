using System.Text.Json.Nodes;
using Coflnet.Core;
using Coflnet.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using NUnit.Framework;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.Core.Tests
{
    [ApiController]
    [Route("api/[controller]")]
    public class ThrowingController : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType(typeof(string), 200)]
        [ProducesResponseType(typeof(ApiException), 401)]
        public string Get() => "x";
    }

    [ApiController]
    [Route("api/[controller]")]
    public class OwnErrorController : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType(typeof(Other.ErrorResponse), 404)]
        public string Get() => "x";
    }

    [ApiController]
    [Route("api/[controller]")]
    public class CoreErrorController : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType(typeof(Coflnet.OpenApi.ErrorResponse), 409)]
        public string Get() => "x";
    }

    public class TestStatusException : ApiException
    {
        public TestStatusException() : base("status", "status") { }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class SubExceptionController : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType(typeof(TestStatusException), 404)]
        public string Get() => "x";
    }

    [ApiController]
    [Route("api/[controller]")]
    public class BadRequestExceptionController : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType(typeof(ApiException), 400)]
        public string Get() => "x";
    }

    [ApiController]
    [Route("api/[controller]")]
    public class ClashController : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType(typeof(Clash.ApiErrorResponse), 404)]
        public string Get() => "x";
    }

    [ApiController]
    [Route("api/[controller]")]
    public class PlainController : ControllerBase
    {
        [HttpGet]
        public string Get() => "x";
    }

    public class ApiExceptionSchemaTests
    {
        private static JsonNode GetDocument(params string[] controllers)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(ApiExceptionSchemaTests).Assembly.GetName().Name
            });
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(ApiExceptionSchemaTests).Assembly);
            builder.Services.AddCoflnetOpenApi("test");
            // only document the controllers a test is about, the other fixtures would conflict with each other on purpose
            builder.Services.ConfigureSwaggerGen(o => o.DocInclusionPredicate((_, a) =>
                controllers.Contains(a.ActionDescriptor.RouteValues["controller"])));
            using var app = builder.Build();
            var swagger = app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
            using var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                swagger.SerializeAsV3(new OpenApiJsonWriter(writer));
            }
            stream.Position = 0;
            return JsonNode.Parse(stream)!;
        }

        [Test]
        public void ApiExceptionIsDescribedAsSentErrorBody()
        {
            var doc = GetDocument("Throwing", "OwnError");
            var schemas = doc["components"]!["schemas"]!.AsObject();

            var response = doc["paths"]!["/api/Throwing"]!["get"]!["responses"]!["401"]!;
            var schema = response["content"]!["application/json"]!["schema"]!;
            var reference = schema["$ref"]?.GetValue<string>();
            Assert.That(reference, Is.EqualTo("#/components/schemas/ApiErrorResponse"));
            var resolved = reference == null
                ? schema
                : schemas[reference.Substring(reference.LastIndexOf('/') + 1)]!;

            Assert.That(resolved["type"]!.GetValue<string>(), Is.EqualTo("object"));
            var props = resolved["properties"]!.AsObject();
            Assert.That(props.Select(p => p.Key), Is.EquivalentTo(new[] { "slug", "message", "trace" }));
            foreach (var p in props)
                Assert.That(p.Value!["type"]!.ToJsonString(), Does.Contain("string"), p.Key);
            // v3.0 serialization expresses a nullable string as nullable: true
            Assert.That(props["trace"]!.ToJsonString(), Does.Contain("null"));

            TestContext.Out.WriteLine(resolved.ToJsonString());
        }

        [Test]
        public void ExceptionGraphIsNotInComponents()
        {
            var schemas = GetDocument("Throwing", "OwnError")["components"]!["schemas"]!.AsObject().Select(s => s.Key).ToList();
            TestContext.Out.WriteLine(string.Join(",", schemas));
            foreach (var bad in new[] { "Exception", "Type", "MethodBase", "Assembly", "ConstructorInfo", "ApiException" })
                Assert.That(schemas, Does.Not.Contain(bad));
        }

        [Test]
        public void ServiceOwnErrorResponseClassStillWorks()
        {
            var schemas = GetDocument("Throwing", "OwnError")["components"]!["schemas"]!.AsObject();
            Assert.That(schemas.ContainsKey("ErrorResponse"), Is.True);
            Assert.That(schemas["ErrorResponse"]!["properties"]!.AsObject().ContainsKey("code"), Is.True);
        }

        [Test]
        public void ServiceClassNamedApiErrorResponseThrows()
        {
            var e = Assert.Throws<InvalidOperationException>(() => GetDocument("Clash"));
            Assert.That(e!.Message, Does.Contain("ApiErrorResponse").And.Contain("clashes"));
        }

        [Test]
        public void NoApiErrorResponseWithoutApiExceptionUsage()
        {
            var schemas = GetDocument("Plain")["components"]?["schemas"]?.AsObject();
            Assert.That(schemas?.ContainsKey("ApiErrorResponse") ?? false, Is.False);
        }

        [Test]
        public void ApiExceptionAsBadRequestDoesNotPullReflectionTypes()
        {
            var doc = GetDocument("BadRequestException");
            AssertNoReflectionTypes(doc);
        }

        [Test]
        public void ApiExceptionSubclassIsMappedToErrorResponse()
        {
            var doc = GetDocument("SubException");
            var schema = doc["paths"]!["/api/SubException"]!["get"]!["responses"]!["404"]!["content"]!["application/json"]!["schema"]!;
            Assert.That(schema["$ref"]!.GetValue<string>(), Is.EqualTo("#/components/schemas/ApiErrorResponse"));
            Assert.That(doc["components"]!["schemas"]!.AsObject().ContainsKey("ApiErrorResponse"), Is.True);
            AssertNoReflectionTypes(doc);
        }

        private static void AssertNoReflectionTypes(JsonNode doc)
        {
            var schemas = doc["components"]?["schemas"]?.AsObject().Select(s => s.Key).ToList() ?? new List<string>();
            foreach (var bad in new[] { "Exception", "Type", "MethodBase", "Assembly", "ConstructorInfo", "Module", "TestStatusException", "ApiException" })
                Assert.That(schemas, Does.Not.Contain(bad));
        }

        [Test]
        public void CoreErrorResponseUsedDirectlyDoesNotConflict()
        {
            var schemas = GetDocument("Throwing", "CoreError")["components"]!["schemas"]!.AsObject();
            Assert.That(schemas.ContainsKey("ErrorResponse"), Is.True);
            var props = schemas["ErrorResponse"]!["properties"]!.AsObject();
            Assert.That(props.ContainsKey("slug"), Is.True);
            Assert.That(props.ContainsKey("message"), Is.True);
            Assert.That(props.ContainsKey("code"), Is.False);
            TestContext.Out.WriteLine(props.ToJsonString());
        }
    }
}

namespace Other
{
    public class ErrorResponse
    {
        public int Code { get; set; }
    }
}

namespace Clash
{
    public class ApiErrorResponse
    {
        public int OwnCode { get; set; }
    }
}
