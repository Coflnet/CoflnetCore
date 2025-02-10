using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Mvc;
using Coflnet.Core;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coflnet.Auth;

public static class AuthExtensions
{
    public static IServiceCollection AddCoflAuthService(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<AuthService>();
        // from config
        var issuer = builder.Configuration["jwt:issuer"];
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["jwt:secret"] ?? throw new InvalidOperationException("jwt:secret is missing in the configuration.")));
        // override default claim mapping to not remab "sub" to "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"
        JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();
        builder.Services
            .AddAuthorization()
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = issuer,
                    IssuerSigningKey = key
                };

                options.Events = new JwtBearerEvents
                {
                    OnChallenge = c =>
                    {
                        var logger = c.HttpContext.RequestServices.GetRequiredService<ILogger<AuthService>>();
                        logger.LogError("authentication challenge {error}", c.Error);
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
                        {
                            context.Response.Headers.Append(new("Token-Expired", "true"));
                        }
                        else
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<AuthService>>();
                            logger.LogError(context.Exception, "authentication issue");
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddEndpointsApiExplorer();
        return builder.Services;
    }

    public static void UseCoflAuthService(this WebApplication app)
    {
        app.MapPost("/api/auth/firebase", async ([FromBody] TokenContainer request) =>
        {
            var authService = app.Services.GetRequiredService<AuthService>();
            if (request == null)
            {
                return Results.BadRequest();
            }
            string externalUserId = "";
            if (!request.AuthToken.StartsWith("ey"))
            {
                // is an accesstoken not id
                HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.AuthToken);
                var userInfoResponse = await client.GetStringAsync("https://www.googleapis.com/oauth2/v1/userinfo?alt=json");
                Console.WriteLine(userInfoResponse);    
                var userInfo = JsonSerializer.Deserialize<GoogleUserInfo>(userInfoResponse);
                externalUserId = userInfo.Id;
            }
            else
            {
            var instance = FirebaseAuth.DefaultInstance ?? throw new ApiException("firebase_not_initialized", "Firebase admin not initialized");
                // is an idtoken
                FirebaseToken decodedToken = await instance
                    .VerifyIdTokenAsync(request.AuthToken);
                externalUserId = decodedToken.Subject;
            }
            var user = await authService.GetUser(externalUserId!);
            var userId = user?.Id ?? default;
            if (user == default)
            {
                userId = await authService.CreateUser(externalUserId!, null, null, null);
            }
            else
            {
                await authService.UpdateUserLastSeenAt(user);
            }
            var response = new TokenContainer { AuthToken = authService.CreateTokenFor(userId!) };
            return Results.Ok(response);
        })
        .WithName("LoginFirebase")
        .WithDisplayName("Login with Firebase")
        .WithTags("Auth")
        .WithOpenApi(op =>
        {
            op.OperationId = "LoginFirebase";
            return op;
        })
        .Produces<TokenContainer>(StatusCodes.Status200OK); // Define 200 OK with a response model

        app.UseAuthentication();
        app.UseAuthorization();
    }

    public class GoogleUserInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
        [JsonPropertyName("email")]
        public string Email { get; set; }
        [JsonPropertyName("verified_email")]
        public bool VerifiedEmail { get; set; }
        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("given_name")]
        public string GivenName { get; set; }
        [JsonPropertyName("family_name")]
        public string FamilyName { get; set; }
        [JsonPropertyName("picture")]
        public string Picture { get; set; }
    }

    public static Guid GetUserId(this ControllerBase controller)
    {
        return Guid.Parse(controller.User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? throw new ApiException("missing_user_id", "User id not found in claims"));
    }
}

public class TokenContainer
{
    public string AuthToken { get; set; }
}
