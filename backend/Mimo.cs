using System.Net.Http.Headers;
using System.Text.Json;

namespace Studio;

public sealed class Mimo(HttpClient http)
{
    public const string Endpoint = "https://api.xiaomimimo.com/v1/chat/completions";
    public static object Payload(ScriptRequest input, ScriptPlan plan, string segment, string? sampleDataUrl = null)
    {
        var messages = new List<object>();
        if (plan.User.Length > 0) messages.Add(new { role = "user", content = plan.User });
        messages.Add(new { role = "assistant", content = plan.Prefix + segment });
        var audio = new Dictionary<string, object> { ["format"] = "wav" };
        if (input.SynthesisMode == "preset") audio["voice"] = input.Voice;
        else if (input.SynthesisMode == "design") audio["optimize_text_preview"] = false;
        else if (input.SynthesisMode == "clone")
        {
            if (sampleDataUrl == null || !(sampleDataUrl.StartsWith("data:audio/wav;base64,", StringComparison.Ordinal) || sampleDataUrl.StartsWith("data:audio/mpeg;base64,", StringComparison.Ordinal)) ||
                sampleDataUrl.Length > SampleAudio.MaxTransportBytes) throw new InputError("参考音频快照无效或过大。");
            audio["voice"] = sampleDataUrl;
        }
        else throw new InputError("无效的合成模式。");
        return new { model = Composition.Model(input.SynthesisMode), stream = false, messages, audio };
    }
    public async Task<Pcm> Generate(string key, ScriptRequest input, ScriptPlan plan, string segment, CancellationToken ct, string? sampleDataUrl = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(Payload(input, plan, segment, sampleDataUrl));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new UpstreamError((int)response.StatusCode, $"MiMo 请求失败（HTTP {(int)response.StatusCode}），请检查 Key、余额或稍后重试。未自动重试。");
        const int maxResponse = 90 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxResponse) throw new InputError("MiMo 响应超出大小上限。");
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream(); var block = new byte[65536]; int n;
        while ((n = await body.ReadAsync(block, timeout.Token)) > 0)
        {
            if (buffer.Length + n > maxResponse) throw new InputError("MiMo 响应超出大小上限。");
            buffer.Write(block, 0, n);
        }
        try
        {
            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            string? encoded = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("audio").GetProperty("data").GetString();
            if (encoded == null || encoded.Length > (Wave.MaxBytes + 2L) / 3 * 4) throw new InputError("MiMo 音频超出大小上限。");
            if (encoded.Length == 0) throw new EmptyAudioError();
            var pcm = Wave.Decode(Convert.FromBase64String(encoded));
            if (pcm.Data.Length == 0) throw new EmptyAudioError();
            return pcm;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        { throw new InputError("MiMo 返回的 JSON、Base64 或音频字段无效。"); }
    }
}
