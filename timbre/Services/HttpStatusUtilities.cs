using System.Net;

namespace timbre.Services;

internal static class HttpStatusUtilities
{
    public static bool IsTransient(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode == 408 || numericStatusCode == 429 || numericStatusCode >= 500;
    }
}
