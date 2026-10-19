using System.Net.Http.Headers;
using System.Text.Json;

namespace Studio;

public sealed class Mimo(HttpClient http)
{
    public const string Endpoint = "https://api.xiaomimimo.com/v1/chat/completions";
    public static object Payload(ScriptRequest input, ScriptPlan plan, string segment)
    {
        var messages = new List<object>();
        if (plan.User.Length > 0) messages.Add(new { role = "user", content = plan.User });
        messages.Add(new { role = "assistant", content = plan.Prefix + segment });
        return new { model = "mimo-v2.5-tts", stream = false, messages, audio = new { format = "wav", voice = input.Voice } };
    }
    public async Task<Pcm> Generate(string key, ScriptRequest input, ScriptPlan plan, string segment, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(Payload(input, plan, segment));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new InputError($"MiMo 请求失败（HTTP {(int)response.StatusCode}），请检查 Key、余额或稍后重试。未自动重试。");
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
            return Wave.Decode(Convert.FromBase64String(encoded));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        { throw new InputError("MiMo 返回的 JSON、Base64 或音频字段无效。"); }
    }
}
