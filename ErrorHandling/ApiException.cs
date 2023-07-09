namespace Coflnet.Core;

/// <summary>
/// Represents an exception that is returned to the client
/// </summary>
public class ApiException : Exception
{
    public string Slug;
    public string Trace;

    public ApiException(string slug, string message) : base(message)
    {
        Slug = slug;
    }

    public override bool Equals(object obj)
    {
        return obj is ApiException ex
        && ex.Trace == Trace
        && ex.Message == Message
        && ex.Slug == Slug;
    }

    public override Exception GetBaseException()
    {
        return base.GetBaseException();
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Slug, Message, Trace);
    }

    public override string ToString()
    {
        return $"{Message} ({Trace}, {Slug})\n{StackTrace}";
    }
}
