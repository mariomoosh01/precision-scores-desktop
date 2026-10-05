using System.Net.Http;
using Microsoft.AspNetCore.Http;

namespace PrecisionScoresDesktop.Host;

// DI-registered forwarder between the Kestrel's /match/scan-multi
// endpoint and the bundled scanner sidecar on 127.0.0.1:34568. Streams
// the multipart body straight through so we don't buffer the whole
// photo in-memory a second time.
public sealed class ScannerProxy
{
    private readonly HttpClient _http;
    private readonly ScannerConfig _config;

    public ScannerProxy(HttpClient http, ScannerConfig config)
    {
        _http = http;
        _config = config;
    }

    public bool IsAvailable => _config.Enabled;

    public async Task<IResult> ForwardAsync(HttpRequest request, CancellationToken ct)
    {
        using var upstream = new HttpRequestMessage(HttpMethod.Post,
            _config.BaseUrl.TrimEnd('/') + "/scan");
        upstream.Headers.Add("X-Scanner-Key", _config.ApiKey);

        // Pipe the incoming multipart body straight to the sidecar.
        var contentType = request.ContentType ?? "application/octet-stream";
        upstream.Content = new StreamContent(request.Body);
        upstream.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (request.ContentLength is long len)
        {
            upstream.Content.Headers.ContentLength = len;
        }

        using var resp = await _http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return Results.Content(body, resp.Content.Headers.ContentType?.ToString() ?? "application/json",
            statusCode: (int)resp.StatusCode);
    }

    // DI container registers the config as a singleton. Shell writes it
    // once at startup after the scanner sidecar is up (or stays disabled).
    public sealed class ScannerConfig
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }
}
