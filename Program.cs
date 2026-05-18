using System.Diagnostics;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/run", async (RunRequest req) =>
{
    var results = new List<RequestResult>();

    var dynamicParams = req.QueryParams.Where(p => p.Values.Length > 1).ToList();
    int rowCount = dynamicParams.Count > 0 ? dynamicParams.Max(p => p.Values.Length) : 1;

    var method = string.IsNullOrWhiteSpace(req.Method) ? "GET" : req.Method.ToUpperInvariant();

    for (int i = 0; i < rowCount; i++)
    {
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
            using var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false };
            using var client = new HttpClient(handler);

            var request = new HttpRequestMessage(new HttpMethod(method), url);

            foreach (var (key, value) in req.Headers)
            {
                if (key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.TryAddWithoutValidation(key, value);
            }

            if (method is "POST" or "PUT" or "PATCH")
            {
                var contentType = req.Headers
                    .FirstOrDefault(kv => kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value
                    ?? "application/json";
                request.Content = new StringContent(req.Body ?? "", Encoding.UTF8, contentType);
            }

            var response = await client.SendAsync(request);
            sw.Stop();

            var body = await response.Content.ReadAsStringAsync();
            results.Add(new RequestResult(
                url,
                (int)response.StatusCode,
                response.StatusCode.ToString(),
                sw.ElapsedMilliseconds,
                body
            ));
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
