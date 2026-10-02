using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LayoutParserLowCodeRunner.Service.Http
{
    internal sealed class HttpRequestData
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public string Query { get; set; }
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = new byte[0];
    }

    internal sealed class HttpResponseData
    {
        public int Status { get; set; } = 200;
        public string ContentType { get; set; } = "application/json; charset=utf-8";
        public byte[] Body { get; set; } = new byte[0];
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal delegate Task<HttpResponseData> RequestHandler(HttpRequestData request, CancellationToken clientGone);

    /// <summary>
    /// Servidor HTTP/1.1 mínimo sobre TcpListener (sem ASP.NET Core; sem urlacl/admin). Por que não
    /// HttpListener: ele NÃO expõe a desconexão do cliente, e o contrato exige que quem desiste não segure
    /// slot (o token <c>clientGone</c> é cancelado quando o socket fecha). Uma requisição por conexão
    /// (Connection: close); só Content-Length (chunked => 411). Limite de corpo => 413 sem ler o corpo.
    /// </summary>
    internal sealed class MiniHttpServer
    {
        private const int MaxHeaderBytes = 16 * 1024;
        private readonly RequestHandler _handler;
        private readonly int _maxBodyBytes;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private TcpListener _listener;
        private Task _acceptLoop;
        private int _active;

        public int Port { get; private set; }
        public int ActiveConnections { get { return Volatile.Read(ref _active); } }

        public MiniHttpServer(RequestHandler handler, int maxBodyBytes)
        {
            _handler = handler;
            _maxBodyBytes = maxBodyBytes;
        }

        public void Start(string prefix)
        {
            var uri = new Uri(prefix);
            IPAddress ip;
            if (uri.Host == "+" || uri.Host == "*" || uri.Host == "0.0.0.0") ip = IPAddress.Any;
            else if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) ip = IPAddress.Loopback;
            else if (!IPAddress.TryParse(uri.Host, out ip)) throw new ArgumentException("ListenPrefix invalido: host " + uri.Host);

            _listener = new TcpListener(ip, uri.Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoop);
        }

        /// <summary>Para de aceitar, espera as requisições em andamento até <paramref name="grace"/> e então chama <paramref name="force"/>.</summary>
        public async Task StopAsync(TimeSpan grace, Action force)
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }

            var deadline = DateTime.UtcNow + grace;
            while (ActiveConnections > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100).ConfigureAwait(false);

            if (ActiveConnections > 0)
            {
                if (force != null) force();
                var end = DateTime.UtcNow.AddSeconds(5);
                while (ActiveConnections > 0 && DateTime.UtcNow < end)
                    await Task.Delay(100).ConfigureAwait(false);
            }
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { break; }
                Interlocked.Increment(ref _active);
                _ = Task.Run(() => HandleConnection(client));
            }
        }

        private async Task HandleConnection(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    stream.ReadTimeout = 30000;
                    stream.WriteTimeout = 30000;

                    HttpRequestData req;
                    try
                    {
                        req = await ReadRequest(stream).ConfigureAwait(false);
                    }
                    catch (HttpProtocolException pe)
                    {
                        await Write(stream, Simple(pe.Status, pe.Message)).ConfigureAwait(false);
                        await Drain(stream, pe.DrainBytes).ConfigureAwait(false);
                        return;
                    }
                    if (req == null)
                        return;

                    using (var gone = new CancellationTokenSource())
                    {
                        var monitor = MonitorDisconnect(client.Client, gone);
                        HttpResponseData resp;
                        try
                        {
                            resp = await _handler(req, gone.Token).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            resp = Simple(500, "Erro interno.");
                        }
                        gone.Cancel(); // encerra o monitor
                        try { await monitor.ConfigureAwait(false); } catch { }

                        await Write(stream, resp).ConfigureAwait(false);
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        /// <summary>Após o corpo lido não deve chegar mais nada: socket legível com 0 bytes = FIN/RST do cliente.</summary>
        private static async Task MonitorDisconnect(Socket socket, CancellationTokenSource gone)
        {
            try
            {
                while (!gone.IsCancellationRequested)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    if (gone.IsCancellationRequested) return;
                    bool closed;
                    try { closed = socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0; }
                    catch { closed = true; }
                    if (closed)
                    {
                        gone.Cancel();
                        return;
                    }
                }
            }
            catch { }
        }

        private async Task<HttpRequestData> ReadRequest(NetworkStream stream)
        {
            var buf = new byte[MaxHeaderBytes];
            int len = 0, headerEnd = -1;
            while (headerEnd < 0)
            {
                if (len == buf.Length)
                    throw new HttpProtocolException(431, "Cabecalhos grandes demais.");
                int n = await stream.ReadAsync(buf, len, buf.Length - len).ConfigureAwait(false);
                if (n == 0)
                    return null; // cliente fechou antes de enviar a requisição
                len += n;
                headerEnd = IndexOfHeaderEnd(buf, len);
            }

            var head = Encoding.ASCII.GetString(buf, 0, headerEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 3)
                throw new HttpProtocolException(400, "Requisicao invalida.");

            var req = new HttpRequestData { Method = first[0].ToUpperInvariant() };
            var target = first[1];
            var q = target.IndexOf('?');
            req.Path = q < 0 ? target : target.Substring(0, q);
            req.Query = q < 0 ? "" : target.Substring(q + 1);

            for (int i = 1; i < lines.Length; i++)
            {
                var c = lines[i].IndexOf(':');
                if (c > 0) req.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }

            string te;
            if (req.Headers.TryGetValue("Transfer-Encoding", out te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new HttpProtocolException(411, "Use Content-Length.");

            long contentLength = 0;
            string cl;
            if (req.Headers.TryGetValue("Content-Length", out cl)
                && !long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
                throw new HttpProtocolException(400, "Content-Length invalido.");

            if (contentLength > _maxBodyBytes)
                throw new HttpProtocolException(413, "Corpo excede o limite configurado.") { DrainBytes = contentLength - Math.Min(len - (headerEnd + 4), contentLength) };

            string expect;
            if (contentLength > 0 && req.Headers.TryGetValue("Expect", out expect)
                && expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(cont, 0, cont.Length).ConfigureAwait(false);
            }

            var body = new byte[contentLength];
            int already = Math.Min(len - (headerEnd + 4), body.Length);
            if (already > 0) Buffer.BlockCopy(buf, headerEnd + 4, body, 0, already);
            int read = already;
            while (read < body.Length)
            {
                int n = await stream.ReadAsync(body, read, body.Length - read).ConfigureAwait(false);
                if (n == 0)
                    return null;
                read += n;
            }
            req.Body = body;
            return req;
        }

        /// <summary>
        /// Descarta (sem guardar) o corpo rejeitado: fechar com bytes pendentes gera RST e o cliente perderia a
        /// resposta 413. Limitado a 256 MB e ao ReadTimeout do stream.
        /// </summary>
        private static async Task Drain(NetworkStream stream, long bytes)
        {
            var scratch = new byte[64 * 1024];
            long left = Math.Min(bytes, 256L * 1024 * 1024);
            try
            {
                while (left > 0)
                {
                    int n = await stream.ReadAsync(scratch, 0, (int)Math.Min(scratch.Length, left)).ConfigureAwait(false);
                    if (n == 0) break;
                    left -= n;
                }
            }
            catch { }
        }

        private static int IndexOfHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n')
                    return i;
            return -1;
        }

        /// <summary>Erro de protocolo no MESMO formato JSON do contrato ({error,exitCode,code,correlationId}): a API monta o cliente a partir dele.</summary>
        private static HttpResponseData Simple(int status, string message)
        {
            var corr = Guid.NewGuid().ToString("N");
            var resp = new HttpResponseData
            {
                Status = status,
                Body = LayoutParserLowCodeRunner.Service.Application.Json.Serialize(
                    new LayoutParserLowCodeRunner.Service.Application.ErrorResponse { Error = message, ExitCode = 0, Code = LayoutParserLowCodeRunner.Service.Application.ErrorCodes.ForProtocolStatus(status), CorrelationId = corr })
            };
            resp.Headers["X-Correlation-ID"] = corr;
            return resp;
        }

        private static async Task Write(NetworkStream stream, HttpResponseData r)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(r.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n");
            foreach (var h in r.Headers)
                sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            sb.Append("\r\n");

            var head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
            if (r.Body.Length > 0)
                await stream.WriteAsync(r.Body, 0, r.Body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 422: return "Unprocessable Entity";
                case 431: return "Request Header Fields Too Large";
                case 499: return "Client Closed Request";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Error";
            }
        }

        private sealed class HttpProtocolException : Exception
        {
            public int Status { get; }
            public long DrainBytes { get; set; }
            public HttpProtocolException(int status, string message) : base(message) { Status = status; }
        }
    }
}
