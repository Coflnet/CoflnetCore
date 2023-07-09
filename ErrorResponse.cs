namespace Coflnet.Core;

public class ErrorResponse
{
    /// <summary>
    /// Unique slug for the error
    /// </summary>
    public string Slug { get; set; }
    /// <summary>
    /// Human readable message
    /// </summary>
    public string Message { get; set; }
    /// <summary>
    /// Opentelemetry trace id 
    /// </summary>
    public string Trace { get; set; }
}
