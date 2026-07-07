using System.Diagnostics;
using System.Net;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/run", async (RunRequest req, CancellationToken ct) =>
{
    var results = new List<RequestResult>();

    var dynamicParams = req.QueryParams.Where(p => p.Values.Length > 1).ToList();
    int rowCount = dynamicParams.Count > 0 ? dynamicParams.Max(p => p.Values.Length) : 1;

    var method = string.IsNullOrWhiteSpace(req.Method) ? "GET" : req.Method.ToUpperInvariant();

    // One handler/client for the whole batch — creating one per request exhausts sockets.
    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    };
    using var client = new HttpClient(handler);

    // Headers that are auto-managed by HttpClient / the content, or that would
    // break the request if copied verbatim from the pasted curl.
    var skipHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "content-type", "content-length", "accept-encoding", "host", "connection"
    };

    for (int i = 0; i < rowCount; i++)
    {
        if (ct.IsCancellationRequested) break;   // user hit Stop → abandon remaining requests

        var queryParts = new List<string>();
        foreach (var p in req.QueryParams)
        {
            var val = p.Values.Length == 1 ? p.Values[0] : (i < p.Values.Length ? p.Values[i] : p.Values[^1]);
            queryParts.Add($"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(val)}");
        }

        var url = queryParts.Count > 0 ? $"{req.BaseUrl}?{string.Join("&", queryParts)}" : req.BaseUrl;
        var sw = Stopwatch.StartNew();

        try
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url);

            foreach (var (key, value) in req.Headers)
            {
                if (skipHeaders.Contains(key)) continue;
                request.Headers.TryAddWithoutValidation(key, value);
            }

            if (!string.IsNullOrEmpty(req.Body) && method is "POST" or "PUT" or "PATCH" or "DELETE")
            {
                var contentType = req.Headers
                    .FirstOrDefault(kv => kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value;
                if (string.IsNullOrWhiteSpace(contentType)) contentType = "application/json";

                var content = new StringContent(req.Body, Encoding.UTF8);
                // Set the content-type as a raw string so parameters (e.g. "; charset=utf-8")
                // don't throw the way the StringContent(…, mediaType) overload does.
                content.Headers.Remove("Content-Type");
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                request.Content = content;
            }

            var response = await client.SendAsync(request, ct);
            sw.Stop();

            var body = await response.Content.ReadAsStringAsync(ct);
            results.Add(new RequestResult(
                url,
                (int)response.StatusCode,
                response.StatusCode.ToString(),
                sw.ElapsedMilliseconds,
                body
            ));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            break;   // client disconnected / Stop pressed — no point continuing
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new RequestResult(url, 0, ex.Message, sw.ElapsedMilliseconds, ""));
        }
    }

    return Results.Json(results);
});

app.Run();

record QueryParam(string Key, string[] Values);
record RunRequest(string BaseUrl, Dictionary<string, string> Headers, QueryParam[] QueryParams, string Method = "GET", string? Body = null);
record RequestResult(string Url, int Status, string StatusText, long Ms, string Body);
