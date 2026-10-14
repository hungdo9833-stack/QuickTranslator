using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class OllamaClientTests
{
    [Fact]
    public async Task ConnectionCheck_OnlyReadsLocalModelList_AndSendsNoChapter()
    {
        int calls = 0;
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            calls++;
            Assert.Equal("/api/tags", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(JsonResponse("{\"models\":[{\"name\":\"translategemma:12b\",\"size\":8000000000}]}"));
        }, autoLocalTags: false));
        using var client = new OllamaClient(http);

        await client.CheckConnectionAsync("translategemma:12b", TimeSpan.FromSeconds(1));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChatRequest_UsesLocalhostTranslateGemmaSettings_AndReadsMetrics()
    {
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            Assert.Equal(OllamaClient.DefaultEndpoint, request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            Assert.Equal("translategemma:12b", body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.False(body.GetProperty("think").GetBoolean());
            Assert.Single(body.GetProperty("messages").EnumerateArray());
            Assert.Equal("user", body.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("test prompt", body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal(0.1, body.GetProperty("options").GetProperty("temperature").GetDouble());
            Assert.Equal(4096, body.GetProperty("options").GetProperty("num_ctx").GetInt32());
            Assert.Equal(1536, body.GetProperty("options").GetProperty("num_predict").GetInt32());
            return JsonResponse("""
                {"message":{"role":"assistant","content":"Bản dịch thử."},"done":true,"done_reason":"stop",
                 "prompt_eval_count":80,"eval_count":45,"total_duration":1500000000,"load_duration":100000,
                 "prompt_eval_duration":200000,"eval_duration":300000}
                """);
        })) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OllamaClient(http);
        var result = await client.TranslateAsync("test prompt", new());
        Assert.Equal("Bản dịch thử.", result.Text);
        Assert.True(result.Done);
        Assert.Equal("stop", result.DoneReason);
        Assert.Equal(80, result.Metrics.InputTokens);
        Assert.Equal(45, result.Metrics.OutputTokens);
        Assert.Equal(1500000000L, result.Metrics.TotalDurationNanoseconds);
    }

    [Theory]
    [InlineData("https://127.0.0.1:11434/api/chat")]
    [InlineData("http://example.com:11434/api/chat")]
    [InlineData("http://192.168.1.2:11434/api/chat")]
    [InlineData("http://127.0.0.1:11434/api/chat?upload=true")]
    [InlineData("http://user:password@127.0.0.1:11434/api/chat")]
    public void RemoteOrUnexpectedEndpoints_AreRejected(string uri)
    {
        Assert.Throws<ArgumentException>(() => OllamaClient.ValidateLocalEndpoint(new(uri)));
    }

    [Fact]
    public void Localhost_IsNormalizedToLoopbackIp()
    {
        Assert.Equal("127.0.0.1", OllamaClient.ValidateLocalEndpoint(new("http://localhost:11434/api/chat")).Host);
    }

    [Fact]
    public async Task MissingModel_ReturnsClearError()
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        using var client = new OllamaClient(http);
        var error = await Assert.ThrowsAsync<OllamaRequestException>(() => client.TranslateAsync("prompt", new()));
        Assert.Equal(OllamaErrorKind.Http, error.Kind);
        Assert.Contains("ollama list", error.Message);
    }

    [Fact]
    public async Task RedirectResponse_IsRejected()
    {
        using var http = new HttpClient(new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("http://example.com/upload");
            return Task.FromResult(response);
        }));
        using var client = new OllamaClient(http);
        var error = await Assert.ThrowsAsync<OllamaRequestException>(() => client.TranslateAsync("prompt", new()));
        Assert.Equal(OllamaErrorKind.Http, error.Kind);
    }

    [Fact]
    public async Task ConnectionFailure_IsReported()
    {
        using var http = new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("synthetic connection failure")));
        using var client = new OllamaClient(http);
        var error = await Assert.ThrowsAsync<OllamaRequestException>(() => client.TranslateAsync("prompt", new()));
        Assert.Equal(OllamaErrorKind.Connection, error.Kind);
        Assert.Contains("ollama serve", error.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"done\":true}")]
    public async Task MalformedResponse_IsReported(string content)
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(content))));
        using var client = new OllamaClient(http);
        var error = await Assert.ThrowsAsync<OllamaRequestException>(() => client.TranslateAsync("prompt", new()));
        Assert.Equal(OllamaErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task Timeout_IsDifferentFromUserCancellation()
    {
        using var http = new HttpClient(new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return JsonResponse("{}");
        })) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OllamaClient(http);
        var error = await Assert.ThrowsAsync<OllamaRequestException>(() => client.TranslateAsync("prompt", new() { RequestTimeout = TimeSpan.FromMilliseconds(60) }));
        Assert.Equal(OllamaErrorKind.Timeout, error.Kind);
        using var cancellation = new CancellationTokenSource();
        var task = client.TranslateAsync("prompt", new(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Client_OnlyHasOneActiveHttpRequest()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0, calls = 0;
        using var http = new HttpClient(new FakeHandler(async (_, ct) =>
        {
            calls++;
            active++;
            maximum = Math.Max(maximum, active);
            firstStarted.TrySetResult();
            await release.Task.WaitAsync(ct);
            active--;
            return JsonResponse("{\"message\":{\"content\":\"Bản dịch.\"},\"done\":true}");
        }));
        using var client = new OllamaClient(http);
        var first = client.TranslateAsync("one", new());
        await firstStarted.Task;
        var second = client.TranslateAsync("two", new());
        Assert.Equal(1, calls);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, calls);
        Assert.Equal(1, maximum);
    }

    internal static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    [Theory]
    [InlineData("{\"models\":[]}")]
    [InlineData("{\"models\":[{\"name\":\"translategemma:12b\",\"size\":100,\"remote_host\":\"https://ollama.com\"}]}")]
    [InlineData("{\"models\":[{\"name\":\"translategemma:12b\",\"size\":100,\"remote_model\":\"remote-alias\"}]}")]
    [InlineData("{\"models\":[{\"name\":\"translategemma:12b\",\"size\":0}]}")]
    [InlineData("{}")]
    public async Task MissingOrRemoteModel_IsRejectedBeforeSendingChapter(string tags)
    {
        int chatCalls = 0;
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Null(request.Content);
                return Task.FromResult(JsonResponse(tags));
            }
            chatCalls++;
            return Task.FromResult(JsonResponse("{}"));
        }, autoLocalTags: false));
        using var client = new OllamaClient(http);
        await Assert.ThrowsAsync<OllamaRequestException>(() => client.TranslateAsync("private synthetic chapter", new()));
        Assert.Equal(0, chatCalls);
    }

    internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        bool autoLocalTags = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            autoLocalTags && request.RequestUri!.AbsolutePath == "/api/tags"
                ? Task.FromResult(JsonResponse("{\"models\":[{\"name\":\"translategemma:12b\",\"size\":8000000000}]}"))
                : respond(request, cancellationToken);
    }
}
