namespace Coflnet.Auth;

public class User
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Locale { get; set; }
    /// <summary>
    /// Based on the auth provider, this is the ID of the user in the auth provider's system.
    /// </summary>
    public string AuthProviderId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}