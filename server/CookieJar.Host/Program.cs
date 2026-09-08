using System.Text.Json.Nodes;
using CookieJar.Host;

if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
{
    return InstallerCommands.Dispatch(args);
}

// Native-messaging mode: launched by Edge with stdio as the transport.
// We *also* host the local HTTP API so the agent can reach us.
var token = TokenStore.LoadOrCreate();
var listenAddress = Environment.GetEnvironmentVariable("COOKIEJAR_LISTEN") ?? "http://127.0.0.1:47891";

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };

var channel = new NativeMessagingChannel(stdin, stdout);
var broker = new CookieBroker(channel);

var readerTask = channel.RunReaderAsync(lifetime.Token);
var pumpTask = broker.RunResponsePumpAsync(lifetime.Token);

// When stdin closes (Edge died), shut everything down.
_ = readerTask.ContinueWith(_ => lifetime.Cancel(), TaskScheduler.Default);

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls(listenAddress);
builder.Services.AddSingleton(broker);

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/health")
    {
        await next();
        return;
    }

    var auth = ctx.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!auth.StartsWith(prefix, StringComparison.Ordinal) ||
        !CryptographicEquals(auth.AsSpan(prefix.Length), token))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("""{"error":"unauthorized"}""");
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    extensionConnected = broker.ExtensionConnected,
    browser = "edge",
}));

app.MapGet("/cookies", async (string domain, string? name, bool? includeSubdomains, CookieBroker br) =>
{
    if (string.IsNullOrWhiteSpace(domain))
    {
        return Results.BadRequest(new { error = "domain_required" });
    }

    var request = new JsonObject
    {
        ["op"] = "getCookies",
        ["domain"] = domain,
        ["includeSubdomains"] = includeSubdomains ?? true,
    };
    if (!string.IsNullOrEmpty(name))
    {
        request["name"] = name;
    }

    JsonObject? response;
    try
    {
        response = await br.RequestAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);
    }
    catch (TimeoutException)
    {
        return Results.Json(new { error = "extension_timeout" }, statusCode: 504);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = "extension_disconnected", detail = ex.Message }, statusCode: 503);
    }

    if (response is null)
    {
        return Results.Json(new { error = "extension_disconnected" }, statusCode: 503);
    }

    if (response["ok"]?.GetValue<bool>() != true)
    {
        var err = response["error"]?.GetValue<string>() ?? "extension_error";
        return Results.Json(new { error = err }, statusCode: 502);
    }

    var cookies = response["cookies"] as JsonArray ?? new JsonArray();
    if (cookies.Count == 0)
    {
        return Results.Json(new { error = "no_cookies", domain }, statusCode: 404);
    }

    var header = string.Join("; ", cookies
        .OfType<JsonObject>()
        .Select(c => $"{c["name"]?.GetValue<string>()}={c["value"]?.GetValue<string>()}"));

    return Results.Json(new
    {
        domain,
        fetchedAt = DateTimeOffset.UtcNow,
        header,
        curlB = header,
        cookies,
    });
});

app.MapGet("/domains", async (CookieBroker br) =>
{
    JsonObject? response;
    try
    {
        response = await br.RequestAsync(
            new JsonObject { ["op"] = "listDomains" },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
    }
    catch (TimeoutException)
    {
        return Results.Json(new { error = "extension_timeout" }, statusCode: 504);
    }

    if (response is null)
    {
        return Results.Json(new { error = "extension_disconnected" }, statusCode: 503);
    }

    return Results.Json(new
    {
        domains = response["domains"] ?? new JsonArray(),
    });
});

app.MapGet("/request-auth", async (string domain, CookieBroker br) =>
{
    if (string.IsNullOrWhiteSpace(domain))
    {
        return Results.BadRequest(new { error = "domain_required" });
    }

    JsonObject? response;
    try
    {
        response = await br.RequestAsync(
            new JsonObject
            {
                ["op"] = "getRequestAuth",
                ["domain"] = domain,
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
    }
    catch (TimeoutException)
    {
        return Results.Json(new { error = "extension_timeout" }, statusCode: 504);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(
            new { error = "extension_disconnected", detail = ex.Message },
            statusCode: 503);
    }

    if (response is null)
    {
        return Results.Json(new { error = "extension_disconnected" }, statusCode: 503);
    }

    if (response["ok"]?.GetValue<bool>() != true)
    {
        var error = response["error"]?.GetValue<string>() ?? "extension_error";
        var status = error == "no_request_auth" ? 404 : 502;
        return Results.Json(new { error }, statusCode: status);
    }

    return Results.Json(new
    {
        domain,
        url = response["url"],
        capturedAt = response["capturedAt"],
        headers = response["headers"],
    });
});

await app.RunAsync(lifetime.Token);
return 0;

static bool CryptographicEquals(ReadOnlySpan<char> a, string b)
{
    if (a.Length != b.Length) return false;
    var diff = 0;
    for (var i = 0; i < a.Length; i++)
    {
        diff |= a[i] ^ b[i];
    }

    return diff == 0;
}
