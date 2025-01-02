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
                        Console.WriteLine(c.Error);
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
                            Console.WriteLine(context.Exception);
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
            var instance = FirebaseAuth.DefaultInstance ?? throw new ApiException("firebase_not_initialized", "Firebase admin not initialized");
            FirebaseToken decodedToken = await instance
                .VerifyIdTokenAsync(request.AuthToken);
            var user = await authService.GetUser(decodedToken.Subject);
            var userId = user?.Id ?? default;
            if (user == default)
            {
                userId = await authService.CreateUser(decodedToken.Subject, null, null, null);
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
        .WithGroupName("Auth")
        .Produces<TokenContainer>(StatusCodes.Status200OK); // Define 200 OK with a response model

        app.UseAuthentication();
        app.UseAuthorization();
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
