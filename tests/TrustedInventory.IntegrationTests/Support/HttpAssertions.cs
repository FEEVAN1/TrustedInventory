using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrustedInventory.IntegrationTests.Support;

public static class HttpAssertions
{
    public static async Task<JsonObject> ExpectAsync(this HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status} but got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>Asserts an error body (section 15) with the given status and wire code.</summary>
    public static async Task<JsonObject> ExpectProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.ExpectAsync(status);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (string?)body["code"]);
        Assert.False(string.IsNullOrEmpty((string?)body["traceId"]));
        return body;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body, string? ifMatch = null) =>
        SendJsonAsync(client, HttpMethod.Post, url, body, "application/json", ifMatch);

    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient client, string url, object body, string? ifMatch) =>
        SendJsonAsync(client, HttpMethod.Patch, url, body, "application/merge-patch+json", ifMatch);

    public static Task<HttpResponseMessage> PostEmptyAsync(this HttpClient client, string url, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string url, object body,
        string contentType, string? ifMatch)
    {
        var json = body as string ?? JsonSerializer.Serialize(body, JsonSerializerOptions.Web);
        var request = new HttpRequestMessage(method, url) { Content = new StringContent(json, Encoding.UTF8, contentType) };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }

    public static string ETag(this HttpResponseMessage response) =>
        response.Headers.ETag?.Tag ?? throw new Xunit.Sdk.XunitException("Response has no ETag.");

    public static Task<JsonObject?> ReadObjectAsync(this HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonObject>();
}
