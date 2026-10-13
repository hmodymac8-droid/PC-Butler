// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace PCButler.Core;

/// <summary>
/// One request that came in over a path, and the answer being made for it. Courier's
/// handler reads the request from here and sets the answer; the answer is written once the
/// handler is done.
/// </summary>
public sealed class PortalExchange
{
    private readonly Dictionary<string, string> query;
    private byte[] answer = [];

    internal PortalExchange(string method, string path, Dictionary<string, string> query, Stream body, X509Certificate2? client, IPEndPoint via, CancellationToken aborted)
    {
        (Method, Path, this.query, Body, ClientCertificate, Via, Aborted) = (method, path, query, body, client, via, aborted);
    }

    public string Method { get; }
    public string Path { get; }
    /// <summary>What the caller sent after the request line; reads end where its body ends.</summary>
    public Stream Body { get; }
    /// <summary>The certificate the caller showed: who it is.</summary>
    public X509Certificate2? ClientCertificate { get; }
    /// <summary>The caller's address at the other end of the path across the internet.</summary>
    public IPEndPoint Via { get; }
    public CancellationToken Aborted { get; }
    public int Status { get; set; } = 200;
    internal string ContentType { get; private set; } = "";
    internal byte[] Answer => answer;

    public string? Query(string name) => query.GetValueOrDefault(name);

    public async Task<JsonElement> ReadJson() => await JsonSerializer.DeserializeAsync<JsonElement>(Body, cancellationToken: Aborted);

    public Task Json(int status, object body)
    {
        (Status, ContentType, answer) = (status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(body));
        return Task.CompletedTask;
    }

    /// <summary>An answer with nothing but its status (an error found half-way replaces anything set before).</summary>
    public void Fail(int status) => (Status, ContentType, answer) = (status, "", []);
}

/// <summary>
/// The server side of Courier's encrypted connection: HTTP/1.1 over TLS, spoken straight
/// on a stream that a path across the internet carries. Nothing listens on a network port.
/// Every caller must show its certificate; who it is gets checked by fingerprint afterwards.
/// </summary>
public static class PortalHttp
{
    private const int MaxLine = 8192, MaxHeaders = 64;

    /// <summary>Answer requests on one connection until the caller closes it or Courier stops.</summary>
    public static async Task Serve(Stream transport, X509Certificate2 certificate, IPEndPoint via, Func<PortalExchange, Task> handle, CancellationToken stop)
    {
        await using var tls = new SslStream(transport, leaveInnerStreamOpen: false);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(30));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                // None is signed by an authority: any certificate is let through here, and its fingerprint decides what it may do.
                RemoteCertificateValidationCallback = (_, shown, _, _) => shown is not null,
            }, handshake.Token);
        }
        using var client = tls.RemoteCertificate is { } shown ? shown as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(shown.GetRawCertData()) : null;
        var input = new Reader(tls);
        while (!stop.IsCancellationRequested)
        {
            var line = await input.Line(stop);
            if (line is null)
                return; // the caller is done with this connection
            if (line.Length == 0)
                continue;
            var parts = line.Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                return;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var count = 0; ; count++)
            {
                var header = await input.Line(stop);
                if (header is null || count > MaxHeaders)
                    return;
                if (header.Length == 0)
                    break;
                var colon = header.IndexOf(':');
                if (colon > 0)
                    headers[header[..colon].Trim()] = header[(colon + 1)..].Trim();
            }
            var (path, query) = SplitTarget(parts[1]);
            Stream body = headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase)
                ? new ChunkedBody(input)
                : new LengthBody(input, headers.TryGetValue("Content-Length", out var length) && long.TryParse(length, out var size) && size > 0 ? size : 0);
            var exchange = new PortalExchange(parts[0], path, query, body, client, via, stop);
            try
            {
                await handle(exchange);
            }
            catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
            {
                exchange.Fail(500);
            }
            // Whatever the handler did not read is skipped, so the next request starts where it should.
            await body.CopyToAsync(Stream.Null, stop);
            var close = headers.TryGetValue("Connection", out var connection) && connection.Equals("close", StringComparison.OrdinalIgnoreCase);
            var head = new StringBuilder($"HTTP/1.1 {exchange.Status} {Reason(exchange.Status)}\r\nContent-Length: {exchange.Answer.Length}\r\n");
            if (exchange.ContentType.Length > 0)
                head.Append($"Content-Type: {exchange.ContentType}\r\n");
            if (close)
                head.Append("Connection: close\r\n");
            head.Append("\r\n");
            await tls.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), stop);
            await tls.WriteAsync(exchange.Answer, stop);
            await tls.FlushAsync(stop);
            if (close)
                return;
        }
    }

    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var mark = target.IndexOf('?');
        if (mark < 0)
            return (Uri.UnescapeDataString(target), query);
        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0)
                query[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return (Uri.UnescapeDataString(target[..mark]), query);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 410 => "Gone", 429 => "Too Many Requests",
        507 => "Insufficient Storage", _ => "Error",
    };

    /// <summary>Lines and bytes from the connection, through one buffer, so a body can follow its headers without anything lost.</summary>
    internal sealed class Reader(Stream source)
    {
        private readonly byte[] buffer = new byte[64 * 1024];
        private int start, end;

        public async Task<bool> Fill(CancellationToken cancel)
        {
            if (start < end)
                return true;
            (start, end) = (0, await source.ReadAsync(buffer, cancel));
            return end > 0;
        }

        public async Task<string?> Line(CancellationToken cancel)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (!await Fill(cancel))
                    return line.Length == 0 ? null : line.ToString();
                var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
                var stop = newline < 0 ? end : newline;
                line.Append(Encoding.ASCII.GetString(buffer, start, stop - start));
                start = newline < 0 ? end : newline + 1;
                if (line.Length > MaxLine)
                    throw new IOException("A line of the request is too long.");
                if (newline >= 0)
                    return line.ToString().TrimEnd('\r');
            }
        }

        public async Task<int> Read(Memory<byte> into, CancellationToken cancel)
        {
            if (!await Fill(cancel))
                return 0;
            var count = Math.Min(into.Length, end - start);
            buffer.AsMemory(start, count).CopyTo(into);
            start += count;
            return count;
        }
    }

    /// <summary>A body of a known length.</summary>
    private sealed class LengthBody(Reader input, long length) : Stream
    {
        private long left = length;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            if (left <= 0)
                return 0;
            var read = await input.Read(buffer[..(int)Math.Min(buffer.Length, left)], cancel);
            if (read == 0)
                throw new IOException("The connection closed in the middle of a request.");
            left -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body sent in chunks, each with its length in front (what HttpClient does when it doesn't know the length beforehand).</summary>
    private sealed class ChunkedBody(Reader input) : Stream
    {
        private long left;
        private bool over;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            if (over)
                return 0;
            if (left == 0)
            {
                var size = await input.Line(cancel) ?? throw new IOException("The connection closed in the middle of a request.");
                left = Convert.ToInt64(size.Split(';')[0].Trim(), 16);
                if (left == 0)
                {
                    // The last chunk: then any trailer lines, up to an empty one.
                    while ((await input.Line(cancel)) is { Length: > 0 })
                    {
                    }
                    over = true;
                    return 0;
                }
            }
            var read = await input.Read(buffer[..(int)Math.Min(buffer.Length, left)], cancel);
            if (read == 0)
                throw new IOException("The connection closed in the middle of a request.");
            left -= read;
            if (left == 0)
                await input.Line(cancel); // the line break after each chunk
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
