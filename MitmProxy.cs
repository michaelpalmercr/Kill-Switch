using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace KillSwitch;

/// <summary>One inspected HTTP/HTTPS request+response.</summary>
public sealed class HttpTxn
{
    public DateTime Time;
    public string Method = "";
    public string Host = "";
    public string Url = "";
    public int Status;
    public string ReqContentType = "";
    public string RespContentType = "";
    public long RespLength;
    public string ReqBody = "";
    public string RespBody = "";
}

/// <summary>
/// Opt-in HTTPS inspector via Titanium.Web.Proxy: installs a local root cert, runs a system proxy,
/// and decrypts traffic so requests/responses can be viewed. Invasive — off by default.
/// </summary>
public sealed class MitmProxy
{
    private ProxyServer? _proxy;
    private ExplicitProxyEndPoint? _endpoint;

    private readonly object _lock = new();
    private readonly Queue<HttpTxn> _txns = new();
    private const int Cap = 1000;

    public bool Running { get; private set; }

    public static bool CertInstalled()
    {
        try { return new ProxyServer().CertificateManager.RootCertificate != null; }
        catch { return false; }
    }

    public string? Start(int port)
    {
        if (Running) return null;
        try
        {
            _proxy = new ProxyServer();
            _proxy.CertificateManager.CreateRootCertificate(persistToFile: true);
            _proxy.CertificateManager.TrustRootCertificate(machineTrusted: true);

            _endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, port <= 0 ? 8888 : port, decryptSsl: true);
            _proxy.AddEndPoint(_endpoint);
            _proxy.Start();
            _proxy.SetAsSystemProxy(_endpoint, ProxyProtocolType.AllHttp);

            _proxy.BeforeRequest += OnRequest;
            _proxy.BeforeResponse += OnResponse;
            Running = true;
            return null;
        }
        catch (Exception ex)
        {
            Stop();
            return ex.Message;
        }
    }

    public void Stop()
    {
        try
        {
            if (_proxy != null)
            {
                try { _proxy.BeforeRequest -= OnRequest; } catch { }
                try { _proxy.BeforeResponse -= OnResponse; } catch { }
                try { _proxy.RestoreOriginalProxySettings(); } catch { }
                try { if (_proxy.ProxyRunning) _proxy.Stop(); } catch { }
                try { _proxy.Dispose(); } catch { }
                _proxy = null;
            }
        }
        finally { Running = false; }
    }

    public void RemoveCertificate()
    {
        try
        {
            var p = _proxy ?? new ProxyServer();
            p.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: true);
        }
        catch { }
    }

    private async Task OnRequest(object sender, SessionEventArgs e)
    {
        var req = e.HttpClient.Request;
        var t = new HttpTxn
        {
            Time = DateTime.Now,
            Method = req.Method,
            Url = req.Url,
            Host = req.RequestUri?.Host ?? req.Host ?? "",
            ReqContentType = req.ContentType ?? "",
        };
        try
        {
            if (req.HasBody && IsTexty(req.ContentType))
            {
                e.HttpClient.Request.KeepBody = true;
                t.ReqBody = Truncate(await e.GetRequestBodyAsString());
            }
        }
        catch { }

        e.UserData = t;
        lock (_lock) { _txns.Enqueue(t); while (_txns.Count > Cap) _txns.Dequeue(); }
    }

    private async Task OnResponse(object sender, SessionEventArgs e)
    {
        if (e.UserData is not HttpTxn t) return;
        var resp = e.HttpClient.Response;
        t.Status = resp.StatusCode;
        t.RespContentType = resp.ContentType ?? "";
        t.RespLength = resp.ContentLength;
        try
        {
            if (resp.HasBody && IsTexty(resp.ContentType))
                t.RespBody = Truncate(await e.GetResponseBodyAsString());
        }
        catch { }
    }

    public List<HttpTxn> Snapshot() { lock (_lock) return _txns.ToList(); }
    public void Clear() { lock (_lock) _txns.Clear(); }

    private static bool IsTexty(string? ct) => ct != null &&
        (ct.Contains("text") || ct.Contains("json") || ct.Contains("xml")
         || ct.Contains("form-urlencoded") || ct.Contains("javascript"));

    private static string Truncate(string s) => s.Length > 8000 ? s[..8000] + "\n…[truncated]" : s;
}
