using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
    [Flags]
    public enum PriceKind { Unknown, BuyItemNow, Auction, Negotiatable = 4, IncludesTax = 8 }
    public enum Plain { A, B = 5 }
    public enum BigEnum : ulong { Small = 1, Huge = ulong.MaxValue }

    public class EnumModel
    {
        public PriceKind Kind { get; set; }
        public PriceKind? MaybeKind { get; set; }
        public Plain Plain { get; set; }
        public BigEnum Big { get; set; }
        public Dictionary<PriceKind, int>? ByKind { get; set; }
        public object? Payload { get; set; }
        public string? Text { get; set; }
        public int? Number { get; set; }
        public Dictionary<string, object?>? Bag { get; set; }
        public Plain? MaybePlain { get; set; }
        public EnumModel? Child { get; set; }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class EnumsController : ControllerBase
    {
        [HttpGet]
        public EnumModel Get([FromQuery] Plain filter) => new();
    }

    public class EnumSchemaTests
    {
        private static JsonNode Doc(bool stringEnums, bool allOfRefs = false, bool inlineEnums = false, bool v31 = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(EnumSchemaTests).Assembly.GetName().Name
            });
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(EnumSchemaTests).Assembly)
                .AddJsonOptions(o =>
                {
                    if (stringEnums)
                        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                });
            builder.Services.AddCoflnetOpenApi("test");
            builder.Services.ConfigureSwaggerGen(o =>
            {
                if (allOfRefs)
                    o.UseAllOfToExtendReferenceSchemas();
                if (inlineEnums)
                    o.UseInlineDefinitionsForEnums();
            });
            builder.Services.ConfigureSwaggerGen(o => o.DocInclusionPredicate((_, a) =>
                a.ActionDescriptor.RouteValues["controller"] == "Enums"));
            using var app = builder.Build();
            var swagger = app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
            // serialize as 3.0, that is how the documents are served
            using var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                if (v31)
                    swagger.SerializeAsV31(new OpenApiJsonWriter(writer));
                else
                    swagger.SerializeAsV3(new OpenApiJsonWriter(writer));
            }
            stream.Position = 0;
            return JsonNode.Parse(stream)!;
        }

        private static JsonNode Schema(JsonNode doc, string name) => doc["components"]!["schemas"]![name]!;

        [TestCase(true)]
        [TestCase(false)]
        public void FlagsEnumGetsMembersAndFlag(bool stringEnums)
        {
            var schema = Schema(Doc(stringEnums), "PriceKind");
            TestContext.Out.WriteLine(schema.ToJsonString());
            Assert.That(schema["x-flags"]!.GetValue<bool>(), Is.True);
            var members = schema["x-enum-members"]!.AsArray();
            var actual = members.Select(m => (m!["name"]!.GetValue<string>(), m["value"]!.GetValue<long>())).ToArray();
            Assert.That(actual, Is.EqualTo(new[] { ("Unknown", 0L), ("BuyItemNow", 1L), ("Auction", 2L), ("Negotiatable", 4L), ("IncludesTax", 8L) }));
            // enum list is unchanged: names or numbers depending on the serializer
            var list = schema["enum"]!.AsArray().Select(e => e!.ToJsonString()).ToArray();
            Assert.That(list.Length, Is.EqualTo(5));
            Assert.That(list[3], Is.EqualTo(stringEnums ? "\"Negotiatable\"" : "4"));
        }

        [Test]
        public void PlainEnumHasNoFlag()
        {
            var schema = Schema(Doc(true), "Plain");
            Assert.That(schema["x-flags"], Is.Null);
            Assert.That(schema["x-enum-members"]!.AsArray().Count, Is.EqualTo(2));
            Assert.That(schema["x-enum-members"]![1]!["value"]!.GetValue<long>(), Is.EqualTo(5));
        }

        [Test]
        public void NullableDictionaryKeyAndQueryUsesGetExtensionsOnComponent()
        {
            var doc = Doc(true);
            var props = Schema(doc, "EnumModel")["properties"]!;
            // nullable enum and the query parameter reference the component, which carries the extensions
            Assert.That(props["maybeKind"]!.ToJsonString(), Does.Contain("PriceKind"));
            Assert.That(Schema(doc, "PriceKind")["x-enum-members"], Is.Not.Null);
            Assert.That(doc["paths"]!["/api/Enums"]!["get"]!["parameters"]!.ToJsonString(), Does.Contain("Plain"));
            Assert.That(Schema(doc, "Plain")["x-enum-members"], Is.Not.Null);
            // an enum keyed dictionary is an object with one integer property per enum name
            var byKind = props["byKind"]!;
            TestContext.Out.WriteLine(byKind.ToJsonString());
            Assert.That(byKind["type"]!.GetValue<string>(), Is.EqualTo("object"));
            Assert.That(byKind["nullable"]!.GetValue<bool>(), Is.True);
            var byKindProps = byKind["properties"]!.AsObject();
            Assert.That(byKindProps.Select(p => p.Key), Is.EquivalentTo(new[] { "Unknown", "BuyItemNow", "Auction", "Negotiatable", "IncludesTax" }));
            Assert.That(byKindProps["Auction"]!["type"]!.GetValue<string>(), Is.EqualTo("integer"));
        }

        [Test]
        public void UnsignedLongEnumValuesDoNotOverflow()
        {
            var members = Schema(Doc(true), "BigEnum")["x-enum-members"]!.AsArray();
            var huge = members.Single(m => m!["name"]!.GetValue<string>() == "Huge");
            Assert.That(huge!["value"]!.GetValue<ulong>(), Is.EqualTo(ulong.MaxValue));
        }

        [Test]
        public void ObjectPropertyHasOpenType()
        {
            var props = Schema(Doc(true), "EnumModel")["properties"]!;
            TestContext.Out.WriteLine(props["payload"]!.ToJsonString());
            var payload = props["payload"]!.AsObject();
            Assert.That(payload.ContainsKey("type"), Is.False);
            Assert.That(payload.ContainsKey("enum"), Is.False);
            Assert.That(props["text"]!["type"]!.GetValue<string>(), Is.EqualTo("string"));
            Assert.That(props["text"]!["nullable"]!.GetValue<bool>(), Is.True);
            Assert.That(props["number"]!["type"]!.GetValue<string>(), Is.EqualTo("integer"));
            Assert.That(props["number"]!["nullable"]!.GetValue<bool>(), Is.True);
        }

        [Test]
        public void DictionaryOfNullableObjectHasNoNullEnum()
        {
            var bag = Schema(Doc(true), "EnumModel")["properties"]!["bag"]!;
            TestContext.Out.WriteLine(bag.ToJsonString());
            Assert.That(bag.ToJsonString(), Does.Not.Contain("enum"));
            Assert.That(bag["additionalProperties"]!.AsObject().ContainsKey("type"), Is.False);
        }

        [Test]
        public void AllOfWrappersKeepNullability()
        {
            var props = Schema(Doc(true, allOfRefs: true), "EnumModel")["properties"]!;
            TestContext.Out.WriteLine(props["child"]!.ToJsonString());
            TestContext.Out.WriteLine(props["maybePlain"]!.ToJsonString());
            Assert.That(props["child"]!["nullable"]!.GetValue<bool>(), Is.True);
            Assert.That(props["maybePlain"]!["nullable"]!.GetValue<bool>(), Is.True);
        }

        [Test]
        public void ObjectPropertyHasNoEnumInV31()
        {
            var payload = Schema(Doc(true, v31: true), "EnumModel")["properties"]!["payload"]!;
            TestContext.Out.WriteLine(payload.ToJsonString());
            Assert.That(payload.AsObject().ContainsKey("enum"), Is.False);
        }

        [Test]
        public void NullableEnumGetsMembersWithInlineEnums()
        {
            var props = Schema(Doc(true, inlineEnums: true), "EnumModel")["properties"]!;
            TestContext.Out.WriteLine(props["maybeKind"]!.ToJsonString());
            Assert.That(props["maybeKind"]!["x-enum-members"], Is.Not.Null);
            Assert.That(props["maybeKind"]!["x-flags"]!.GetValue<bool>(), Is.True);
            Assert.That(props["kind"]!["x-enum-members"], Is.Not.Null);
        }

        [Test]
        public void NullOnlySchemaFilterOnlyTouchesPlainNullSchemas()
        {
            var plain = new OpenApiSchema { Type = JsonSchemaType.Null };
            var withEnum = new OpenApiSchema { Type = JsonSchemaType.Null, Enum = new List<JsonNode> { JsonValue.Create("a")! } };
            var withAllOf = new OpenApiSchema { Type = JsonSchemaType.Null, AllOf = new List<IOpenApiSchema> { new OpenApiSchemaReference("X") } };
            var str = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null };
            var doc = new OpenApiDocument
            {
                Components = new OpenApiComponents
                {
                    Schemas = new Dictionary<string, IOpenApiSchema>
                    {
                        ["A"] = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalProperties = plain, Properties = new Dictionary<string, IOpenApiSchema> { ["e"] = withEnum, ["a"] = withAllOf, ["s"] = str } }
                    }
                }
            };
            new NullOnlySchemaDocumentFilter().Apply(doc, null!);
            Assert.That(plain.Type, Is.Null);
            Assert.That(withEnum.Type, Is.EqualTo(JsonSchemaType.Null));
            Assert.That(withAllOf.Type, Is.EqualTo(JsonSchemaType.Null));
            Assert.That(str.Type, Is.EqualTo(JsonSchemaType.String | JsonSchemaType.Null));
        }
    }
}
