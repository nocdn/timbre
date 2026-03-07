using System.Net;

namespace timbre.Models;

public sealed class TranscriptionException : Exception
{
    public TranscriptionException(string message, bool isTransient, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
        StatusCode = statusCode;
    }

    public bool IsTransient { get; }

    public HttpStatusCode? StatusCode { get; }
}
