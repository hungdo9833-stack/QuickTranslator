using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class OllamaClient : IOllamaBackendClient
{
    public static Uri DefaultEndpoint { get; } = new("http://127.0.0.1:11434/api/chat");
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly bool _ownsHttpClient;
    private readonly OllamaBackendKind _backendKind;
    private readonly string? _bearerToken;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public OllamaClient(HttpClient httpClient, Uri? endpoint = null, bool ownsHttpClient = false)
        : this(httpClient, endpoint ?? DefaultEndpoint, OllamaBackendKind.Local, null, ownsHttpClient) { }

    public OllamaClient(HttpClient httpClient, Uri endpoint, OllamaBackendKind backendKind,
        string? bearerToken = null, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _backendKind = backendKind;
        _endpoint = backendKind == OllamaBackendKind.Local
            ? ValidateLocalEndpoint(endpoint)
            : ValidateRemoteEndpoint(endpoint);
        if (backendKind == OllamaBackendKind.Local && !string.IsNullOrEmpty(bearerToken))
            throw new ArgumentException("Ollama local không dùng token.", nameof(bearerToken));
        if (bearerToken?.Any(char.IsControl) == true) throw new ArgumentException("Token chứa ký tự không hợp lệ.", nameof(bearerToken));
        _bearerToken = string.IsNullOrEmpty(bearerToken) ? null : bearerToken;
        _ownsHttpClient = ownsHttpClient;
    }

    public static OllamaClient CreateLocal()
    {
        // Local traffic must never be sent to a system proxy or redirected to a remote server.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        return new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, ownsHttpClient: true);
    }

    public static IOllamaBackendClient Create(OllamaBackendProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        OllamaBackendProfile copy = profile.Clone();
        copy.Validate();
        if (copy.Kind == OllamaBackendKind.Local) return CreateLocal();
        var handler = CreateRemoteHandler();
        return new OllamaClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new(copy.Endpoint),
            OllamaBackendKind.Remote, copy.Token, ownsHttpClient: true);
    }

    internal static SocketsHttpHandler CreateRemoteHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(8)
        // Keep the platform TLS validator. In particular, never assign
        // SslOptions.RemoteCertificateValidationCallback here.
    };

    public static Uri ValidateLocalEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "http" || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/api/chat")
            throw new ArgumentException("Ollama phải dùng HTTP localhost và đường dẫn /api/chat.");
        string host = endpoint.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return new UriBuilder(endpoint) { Host = "127.0.0.1" }.Uri;
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("Chỉ cho phép Ollama trên máy này (127.0.0.1 hoặc ::1); không gửi chương lên cloud.");
        return endpoint;
    }

    public static Uri ValidateRemoteEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Remote Ollama cần URL HTTP(S) tuyệt đối, không chứa user/password, query hoặc fragment.");
        string path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.Length > 0 && path is not "/api" and not "/api/chat")
            throw new ArgumentException("Remote URL chỉ nhận địa chỉ server, /api hoặc /api/chat.");
        if (endpoint.Scheme == "http" && !IsPrivateHost(endpoint.Host))
            throw new ArgumentException("HTTP chỉ được phép cho địa chỉ loopback hoặc IP mạng riêng. Server Internet phải dùng HTTPS hợp lệ.");
        return new UriBuilder(endpoint) { Path = "/api/chat", Query = "", Fragment = "" }.Uri;
    }

    private static bool IsPrivateHost(string host)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out IPAddress? address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
        return address.IsIPv6LinkLocal || bytes[0] is 0xFC or 0xFD;
    }

    public async Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        options.Validate();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // A localhost server can proxy cloud models. Check metadata before sending any chapter text.
                await VerifyInstalledLocalModelAsync(options.Model, timeout.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = JsonContent.Create(new
                    {
                        model = options.Model,
                        messages = new[] { new { role = "user", content = prompt } },
                        stream = false,
                        // Translation uses final answers only; don't spend output tokens on model thinking.
                        think = false,
                        options = new
                        {
                            temperature = options.Temperature,
                            num_ctx = options.ContextSize,
                            num_predict = options.MaximumOutputTokens
                        }
                    })
                };
                AddAuthorization(request);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string detail = response.StatusCode == HttpStatusCode.NotFound
                        ? "Không tìm thấy model hoặc API. Kiểm tra tên model bằng ollama list."
                        : "Ollama không xử lý được yêu cầu. Kiểm tra Ollama và mức sử dụng bộ nhớ.";
                    throw new OllamaRequestException(OllamaErrorKind.Http, $"HTTP {(int)response.StatusCode}: {detail}");
                }
                var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: timeout.Token).ConfigureAwait(false);
                if (body is null || body.Message is null || body.Error is not null)
                    throw new OllamaRequestException(OllamaErrorKind.InvalidResponse, "Phản hồi Ollama thiếu message hoặc có lỗi; bản nháp hiện có vẫn được giữ.");
                return new(body.Message.Content ?? "", body.Done, body.DoneReason, new()
                {
                    InputTokens = body.PromptEvalCount,
                    OutputTokens = body.EvalCount,
                    TotalDurationNanoseconds = body.TotalDuration,
                    LoadDurationNanoseconds = body.LoadDuration,
                    PromptEvaluationDurationNanoseconds = body.PromptEvalDuration,
                    EvaluationDurationNanoseconds = body.EvalDuration,
                    ClientElapsed = stopwatch.Elapsed
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Timeout,
                    $"Ollama quá thời gian chờ ({options.RequestTimeout.TotalSeconds:0} giây). Có thể tăng timeout hoặc giảm kích thước đoạn/context.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Connection, _backendKind == OllamaBackendKind.Local
                    ? "Không kết nối được Ollama tại localhost:11434. Hãy mở Ollama hoặc chạy ollama serve; không có dịch vụ cloud thay thế."
                    : "Không kết nối được remote Ollama. Kiểm tra URL, TLS, firewall, VPN và trạng thái máy GPU; không tự chuyển provider.", ex);
            }
            catch (JsonException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.InvalidResponse, "Ollama trả dữ liệu sai định dạng JSON.", ex);
            }
        }
        finally { _requestGate.Release(); }
    }

    public async Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try { await VerifyInstalledLocalModelAsync(model.Trim(), linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Timeout, "Ollama không phản hồi kiểm tra kết nối đúng thời hạn.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Connection, _backendKind == OllamaBackendKind.Local
                    ? "Không kết nối được Ollama tại localhost:11434. Hãy mở Ollama hoặc chạy ollama serve."
                    : "Không kết nối được remote Ollama. Kiểm tra URL, TLS, token và firewall.", ex);
            }
        }
        finally { _requestGate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            ModelList list = await ReadModelsAsync(linked.Token).ConfigureAwait(false);
            return list.Models!.Where(IsInstalledModel).Select(model => model.Name ?? model.Model!)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OllamaRequestException(OllamaErrorKind.Timeout, "Ollama không trả danh sách model đúng thời hạn."); }
        catch (HttpRequestException ex)
        { throw new OllamaRequestException(OllamaErrorKind.Connection, "Không kết nối được Ollama để lấy danh sách model.", ex); }
        finally { _requestGate.Release(); }
    }

    private async Task VerifyInstalledLocalModelAsync(string model, CancellationToken cancellationToken)
    {
        ModelList tags = await ReadModelsAsync(cancellationToken).ConfigureAwait(false);
        var installed = tags.Models!.FirstOrDefault(m => (m.Name == model || m.Model == model) && IsInstalledModel(m));
        if (installed is null)
            throw new OllamaRequestException(OllamaErrorKind.Http,
                "Model chưa sẵn sàng trên server đã chọn. Kiểm tra đúng tên bằng /api/tags hoặc ollama list; chưa gửi nguyên văn.");
    }

    private async Task<ModelList> ReadModelsAsync(CancellationToken cancellationToken)
    {
        var tagsEndpoint = new UriBuilder(_endpoint) { Path = "/api/tags" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, tagsEndpoint);
        AddAuthorization(request);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OllamaRequestException(OllamaErrorKind.Http,
                $"HTTP {(int)response.StatusCode}: không kiểm tra được model qua /api/tags; chưa gửi nguyên văn.");
        var tags = await response.Content.ReadFromJsonAsync<ModelList>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (tags?.Models is null)
            throw new OllamaRequestException(OllamaErrorKind.InvalidResponse, "Ollama không trả danh sách model hợp lệ; chưa gửi nguyên văn.");
        return tags;
    }

    private static bool IsInstalledModel(InstalledModel model) =>
        string.IsNullOrWhiteSpace(model.RemoteHost) && string.IsNullOrWhiteSpace(model.RemoteModel) &&
        model.Size is > 0 && !(model.Name ?? model.Model ?? "").Contains("cloud", StringComparison.OrdinalIgnoreCase);

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (_bearerToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
        _requestGate.Dispose();
    }

    private sealed class ChatResponse
    {
        public ChatMessage? Message { get; set; }
        public bool? Done { get; set; }
        public string? Error { get; set; }
        [JsonPropertyName("done_reason")] public string? DoneReason { get; set; }
        [JsonPropertyName("prompt_eval_count")] public int? PromptEvalCount { get; set; }
        [JsonPropertyName("eval_count")] public int? EvalCount { get; set; }
        [JsonPropertyName("total_duration")] public long? TotalDuration { get; set; }
        [JsonPropertyName("load_duration")] public long? LoadDuration { get; set; }
        [JsonPropertyName("prompt_eval_duration")] public long? PromptEvalDuration { get; set; }
        [JsonPropertyName("eval_duration")] public long? EvalDuration { get; set; }
    }
    private sealed class ChatMessage { public string? Content { get; set; } }
    private sealed class ModelList { public List<InstalledModel>? Models { get; set; } }
    private sealed class InstalledModel
    {
        public string? Name { get; set; }
        public string? Model { get; set; }
        public long? Size { get; set; }
        [JsonPropertyName("remote_host")] public string? RemoteHost { get; set; }
        [JsonPropertyName("remote_model")] public string? RemoteModel { get; set; }
    }
}
