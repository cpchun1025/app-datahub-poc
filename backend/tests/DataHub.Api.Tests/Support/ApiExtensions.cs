using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Endpoints;
using Xunit.Sdk;

namespace DataHub.Api.Tests.Support;

/// <summary>RFC 9457 problem details 嘅最小子集；validation 錯誤喺 <see cref="Errors"/>。</summary>
internal sealed record ProblemBody(int Status, string? Title, string? Detail, Dictionary<string, string[]>? Errors);

internal sealed record ApiReply(
    HttpStatusCode Status,
    string? ContentType,
    string Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public ApiReply Expect(HttpStatusCode status)
    {
        if (Status != status)
        {
            throw new XunitException($"Expected HTTP {(int)status} but got {(int)Status}. Body: {Body}");
        }

        return this;
    }

    public ProblemBody ExpectProblem(HttpStatusCode status)
    {
        Expect(status);
        Assert.Equal("application/problem+json", ContentType);
        return Json<ProblemBody>();
    }

    public T Json<T>() => JsonSerializer.Deserialize<T>(Body, ApiExtensions.JsonOptions)
        ?? throw new XunitException($"Response body is not a {typeof(T).Name}: {Body}");
}

internal static class ApiExtensions
{
    public static readonly JsonSerializerOptions JsonOptions = DataHubJson.CreateOptions();

    public static async Task<T> GetJsonAsync<T>(this HttpClient client, string url)
        => (await client.GetReplyAsync(url)).Expect(HttpStatusCode.OK).Json<T>();

    public static Task<HealthResponse> GetHealthAsync(this HttpClient client)
        => client.GetJsonAsync<HealthResponse>("/api/health");

    public static Task<SourceListResponse> GetSourcesAsync(this HttpClient client)
        => client.GetJsonAsync<SourceListResponse>("/api/sources");

    public static Task<PriceListResponse> GetPricesAsync(this HttpClient client, string query = "")
        => client.GetJsonAsync<PriceListResponse>(query.Length == 0 ? "/api/prices" : "/api/prices?" + query);

    public static Task<ApiReply> GetReplyAsync(this HttpClient client, string url, params (string Name, string Value)[] headers)
        => SendReplyAsync(client, HttpMethod.Get, url, headers);

    public static Task<ApiReply> PostReplyAsync(this HttpClient client, string url)
        => SendReplyAsync(client, HttpMethod.Post, url, []);

    private static async Task<ApiReply> SendReplyAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var responseHeaders = response.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(",", header.Value),
            StringComparer.OrdinalIgnoreCase);

        return new ApiReply(response.StatusCode, response.Content.Headers.ContentType?.MediaType, body, responseHeaders);
    }

    /// <summary>
    /// 輪詢直到條件成立。用真實時間，所以逾時要寬鬆；逾時訊息會附上最後一次觀察到嘅內容方便診斷。
    /// </summary>
    public static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> until, string because, int timeoutMs = 20_000)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (until(value))
            {
                return value;
            }

            if (clock.ElapsedMilliseconds > timeoutMs)
            {
                throw new XunitException(
                    $"Timed out after {timeoutMs} ms waiting for {because}. Last observed: {JsonSerializer.Serialize(value, JsonOptions)}");
            }

            await Task.Delay(25);
        }
    }

    public static Task<SourceDto> WaitForSourceAsync(
        this HttpClient client,
        string sourceId,
        Func<SourceDto, bool> until,
        string because,
        int timeoutMs = 20_000)
        => WaitForAsync(
            async () => (await client.GetSourcesAsync()).Sources.Single(source => source.SourceId == sourceId),
            until,
            $"{sourceId}: {because}",
            timeoutMs);

    public static Task<SourceListResponse> WaitForLiveAsync(this HttpClient client, params string[] sourceIds)
        => WaitForAsync(
            client.GetSourcesAsync,
            list => sourceIds.All(id => list.Sources.Any(source => source.SourceId == id && source.Status == SourceStatus.Live)),
            $"{string.Join(", ", sourceIds)} to be Live");
}
