using System.Net.Http;

namespace KillSwitch;

/// <summary>
/// HTTP helpers for the app's OWN outbound calls (AI providers).
/// These must BYPASS the MITM HTTPS-inspection proxy: when inspection is on,
/// <see cref="MitmProxy"/> sets the system proxy to our local Titanium endpoint,
/// and the default HttpClient honors that system proxy — so our request to
/// api.anthropic.com / generativelanguage.googleapis.com would be routed back
/// through our own proxy and fail with an I/O exception. A direct client avoids
/// that loop while leaving inspection of all other apps untouched.
/// </summary>
public static class Net
{
    /// <summary>A new HttpClient that ignores any system/MITM proxy and connects directly.</summary>
    public static HttpClient CreateDirect(TimeSpan? timeout = null) =>
        new(new SocketsHttpHandler { UseProxy = false, Proxy = null })
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(2),
        };
}
