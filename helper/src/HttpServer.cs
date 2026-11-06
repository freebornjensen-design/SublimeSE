using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SeHelper
{
    public class HttpResult
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body;
        public bool Cacheable;

        public static HttpResult Json(object o)
        {
            return new HttpResult { Body = Encoding.UTF8.GetBytes(Js.Serialize(o)) };
        }

        public static HttpResult Error(int status, string msg)
        {
            var r = Json(new Dictionary<string, object> { { "error", msg } });
            r.Status = status;
            return r;
        }
    }

    public static class Js
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        public static string Serialize(object o) { return S.Serialize(o); }
        public static Dictionary<string, object> Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new Dictionary<string, object>();
            return S.Deserialize<Dictionary<string, object>>(s);
        }
        public static object ParseAny(string s) { return S.DeserializeObject(s); }
    }

    // Минимальный HTTP/1.1 сервер на TcpListener: не требует прав администратора и URL ACL,
    // слушает только 127.0.0.1.
    public class HttpServer
    {
        readonly int _port;
        readonly string _webDir;
        readonly Func<string, string, string, string, HttpResult> _api;

        public HttpServer(int port, string webDir, Func<string, string, string, string, HttpResult> api)
        {
            _port = port; _webDir = webDir; _api = api;
        }

        public void Run()
        {
            var listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();
            while (true)
            {
                var client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = 10000;
                    var head = ReadHead(stream);
                    if (head == null) return;
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var parts = lines[0].Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0], target = parts[1];
                    int len = 0;
                    foreach (var l in lines)
                        if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(l.Substring(15).Trim(), out len);
                    var body = new byte[len];
                    int read = 0;
                    while (read < len)
                    {
                        int n = stream.Read(body, read, len - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    string path = target, query = "";
                    int q = target.IndexOf('?');
                    if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }
                    path = Uri.UnescapeDataString(path);

                    HttpResult res;
                    try
                    {
                        if (method == "OPTIONS") res = new HttpResult { Body = new byte[0] };
                        else if (path.StartsWith("/api/")) res = _api(method, path, query, Encoding.UTF8.GetString(body, 0, read));
                        else res = Static(path);
                    }
                    catch (Exception ex)
                    {
                        res = HttpResult.Error(500, ex.ToString());
                    }
                    Write(stream, res);
                }
            }
            catch (Exception) { /* клиент отвалился, это нормально */ }
        }

        static string ReadHead(Stream s)
        {
            var buf = new List<byte>();
            int b;
            while ((b = s.ReadByte()) >= 0)
            {
                buf.Add((byte)b);
                int c = buf.Count;
                if (c >= 4 && buf[c - 4] == 13 && buf[c - 3] == 10 && buf[c - 2] == 13 && buf[c - 1] == 10)
                    return Encoding.ASCII.GetString(buf.ToArray(), 0, c - 4);
                if (c > 65536) return null;
            }
            return null;
        }

        static void Write(Stream s, HttpResult r)
        {
            var body = r.Body ?? new byte[0];
            var head = "HTTP/1.1 " + r.Status + " " + (r.Status == 200 ? "OK" : "Error") + "\r\n" +
                       "Content-Type: " + r.ContentType + "\r\n" +
                       "Content-Length: " + body.Length + "\r\n" +
                       (r.Cacheable ? "Cache-Control: max-age=86400\r\n" : "Cache-Control: no-store\r\n") +
                       "Access-Control-Allow-Origin: *\r\n" +
                       "Access-Control-Allow-Headers: Content-Type\r\n" +
                       "Connection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
        }

        HttpResult Static(string path)
        {
            if (path == "/" || path == "") path = "/index.html";
            var full = Path.GetFullPath(Path.Combine(_webDir, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(_webDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return HttpResult.Error(404, "not found");
            var ext = Path.GetExtension(full).ToLowerInvariant();
            string ct = ext == ".html" ? "text/html; charset=utf-8"
                      : ext == ".js" ? "application/javascript; charset=utf-8"
                      : ext == ".css" ? "text/css; charset=utf-8"
                      : ext == ".svg" ? "image/svg+xml"
                      : ext == ".png" ? "image/png" : "application/octet-stream";
            return new HttpResult { ContentType = ct, Body = File.ReadAllBytes(full) };
        }
    }
}
