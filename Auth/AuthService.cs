using Cassandra.Data.Linq;
using Cassandra.Mapping;
using ISession = Cassandra.ISession;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Auth;

public class AuthService
{
    private readonly Table<User> userDb;
    private readonly IConfiguration config;
    private readonly ILogger<AuthService> logger;

    public AuthService(ISession session, IConfiguration config, ILogger<AuthService> logger)
    {
        var mapping = new MappingConfiguration()
            .Define(new Map<User>()
            .PartitionKey(t => t.AuthProviderId)
            .Column(t => t.Id, cm => cm.WithSecondaryIndex())
            .Column(t => t.Email, cm => cm.WithSecondaryIndex())
        );
        userDb = new Table<User>(session, mapping, "users");
        userDb.CreateIfNotExists();
        this.config = config;
        this.logger = logger;
    }

    public async Task<Guid> GetUserId(string authProviderId)
    {
        return (await userDb.Where(u => u.AuthProviderId == authProviderId).Select(u => u.Id).ExecuteAsync()).FirstOrDefault();
    }

    public async Task<Guid> CreateUser(string authProviderId, string? name = null, string? email = null, string? locale = null)
    {
        var user = new User()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Email = email,
            AuthProviderId = authProviderId,
            Locale = locale,
            CreatedAt = DateTime.UtcNow,
        };
        user.LastSeenAt = user.CreatedAt;
        await userDb.Insert(user).ExecuteAsync();
        return user.Id;
    }

    public User? GetUser(Guid userId)
    {
        return userDb.Where(u => u.Id == userId).Execute().FirstOrDefault();
    }

    public async Task<User?> GetUser(string authProviderId)
    {
        return (await userDb.Where(u => u.AuthProviderId == authProviderId).ExecuteAsync()).FirstOrDefault();
    }

    public async Task UpdateUserLastSeenAt(User user)
    {
        user.LastSeenAt = DateTime.UtcNow;
        await userDb.Insert(user).ExecuteAsync();
    }

    public string CreateTokenFor(Guid userId, int validForDays = 30)
    {
        string key = config["jwt:secret"] ?? throw new Exception("jwt:secret not set"); //Secret key which will be used later during validation
        var issuer = config["jwt:issuer"] ?? throw new Exception("jwt:secret not set");

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        //Create a List of Claims, Keep claims name short    
        var permClaims = new List<Claim>
        {
            new (JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new (JwtRegisteredClaimNames.Sub, userId.ToString())
        };

        //Create Security Token object by giving required parameters    
        var token = new JwtSecurityToken(issuer, //Issure    
            issuer, //Audience    
            permClaims,
            expires: DateTime.Now.AddDays(validForDays),
            signingCredentials: credentials);
        var jwt_token = new JwtSecurityTokenHandler().WriteToken(token);
        return jwt_token;
    }
}
