using System.Net;
using System.Text;

namespace KillSwitch;

/// <summary>
/// Lightweight parsers for the bits of traffic that reveal "what site": DNS queries/answers,
/// the TLS SNI hostname in a ClientHello, and the Host/path of a plaintext HTTP request.
/// All are defensive — malformed input just returns null/empty.
/// </summary>
public static class Inspect
{
    // ---------------- DNS ----------------

    public static (string? Query, List<(IPAddress Ip, string Host)> Answers) ParseDns(byte[] p)
    {
        var answers = new List<(IPAddress, string)>();
        if (p.Length < 12) return (null, answers);

        int qd = (p[4] << 8) | p[5];
        int an = (p[6] << 8) | p[7];
        int off = 12;
        string? first = null;

        for (int i = 0; i < qd; i++)
        {
            var nm = ReadName(p, ref off);
            if (i == 0) first = nm;
            off += 4; // qtype + qclass
            if (off > p.Length) return (first, answers);
        }

        for (int i = 0; i < an; i++)
        {
            ReadName(p, ref off); // owner name (often a pointer)
            if (off + 10 > p.Length) break;
            int type = (p[off] << 8) | p[off + 1];
            int rdlen = (p[off + 8] << 8) | p[off + 9];
            off += 10;
            if (off + rdlen > p.Length) break;

            string host = first ?? "";
            if (type == 1 && rdlen == 4)
                answers.Add((new IPAddress(new[] { p[off], p[off + 1], p[off + 2], p[off + 3] }), host));
            else if (type == 28 && rdlen == 16)
                answers.Add((new IPAddress(p.AsSpan(off, 16).ToArray()), host));

            off += rdlen;
        }
        return (first, answers);
    }

    private static string ReadName(byte[] p, ref int off)
    {
        var sb = new StringBuilder();
        int safety = 0, returnTo = -1;
        bool jumped = false;

        while (off < p.Length)
        {
            int len = p[off];
            if (len == 0) { off++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (off + 1 >= p.Length) break;
                int ptr = ((len & 0x3F) << 8) | p[off + 1];
                if (!jumped) returnTo = off + 2;
                jumped = true;
                off = ptr;
                if (++safety > 64) break;
                continue;
            }
            off++;
            if (off + len > p.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(p, off, len));
            off += len;
            if (++safety > 64) break;
        }
        if (jumped && returnTo >= 0) off = returnTo;
        return sb.ToString();
    }

    // ---------------- TLS SNI (ClientHello) ----------------

    public static string? ParseSni(byte[] b)
    {
        try
        {
            int n = b.Length;
            if (n < 43 || b[0] != 0x16) return null;       // not a TLS handshake record
            int pos = 5;                                    // skip record header
            if (b[pos] != 0x01) return null;                // not a ClientHello
            pos += 4;                                        // handshake type(1) + length(3)
            pos += 2 + 32;                                  // client version(2) + random(32)
            if (pos >= n) return null;

            int sid = b[pos]; pos += 1 + sid;               // session id
            if (pos + 2 > n) return null;
            int cs = (b[pos] << 8) | b[pos + 1]; pos += 2 + cs; // cipher suites
            if (pos + 1 > n) return null;
            int comp = b[pos]; pos += 1 + comp;             // compression methods
            if (pos + 2 > n) return null;
            int extLen = (b[pos] << 8) | b[pos + 1]; pos += 2;
            int end = Math.Min(n, pos + extLen);

            while (pos + 4 <= end)
            {
                int type = (b[pos] << 8) | b[pos + 1];
                int len = (b[pos + 2] << 8) | b[pos + 3];
                pos += 4;
                if (type == 0x00)                            // server_name
                {
                    if (pos + 5 > n) return null;
                    int nameLen = (b[pos + 3] << 8) | b[pos + 4];
                    int nameStart = pos + 5;
                    if (nameStart + nameLen > n) return null;
                    return Encoding.ASCII.GetString(b, nameStart, nameLen);
                }
                pos += len;
            }
        }
        catch { }
        return null;
    }

    // ---------------- HTTP (plaintext) ----------------

    private static readonly string[] Methods = { "GET ", "POST", "PUT ", "HEAD", "DELE", "OPTI", "PATC", "TRAC", "CONN" };

    public static bool LooksLikeHttp(byte[] b, int off)
    {
        if (off + 4 > b.Length) return false;
        string s = Encoding.ASCII.GetString(b, off, 4);
        return Methods.Contains(s);
    }

    public static (string Method, string Host, string Path, string Headers)? ParseHttp(byte[] b)
    {
        try
        {
            string text = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 2048));
            int firstEol = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstEol < 0) return null;
            string requestLine = text[..firstEol];
            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2) return null;
            string method = parts[0];
            string path = parts[1];

            string host = "";
            foreach (var line in text.Split("\r\n"))
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    host = line[5..].Trim();
                    break;
                }
            }
            return (method, host, path, text);
        }
        catch { return null; }
    }
}
