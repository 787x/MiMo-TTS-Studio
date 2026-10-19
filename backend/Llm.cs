using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Studio;
public sealed record LlmSettings(string BaseUrl = "https://api.xiaomimimo.com/v1", string Model = "mimo-v2.6-flash", bool Streaming = false, string Thinking = "mimo-disabled", bool ReuseTtsKey = false, bool AllowLocal = false);
public sealed record LlmUpdate(LlmSettings Settings, string? Key = null, bool ClearKey = false);
public sealed partial class Storage
{
    private string LlmPath => Path.Combine(Config,"llm-private");
    public (LlmSettings Settings, string? Key) ReadLlm()
    {
        lock(gate) { if(!File.Exists(LlmPath))return(new(),null); if(OperatingSystem.IsLinux())File.SetUnixFileMode(LlmPath,UnixFileMode.UserRead|UnixFileMode.UserWrite); var saved=JsonSerializer.Deserialize<LlmUpdate>(File.ReadAllText(LlmPath),Json)!; return(saved.Settings,saved.Key); }
    }
    public object LlmStatus() { var (s,k)=ReadLlm(); return new { settings=s, configured=s.ReuseTtsKey?ReadKey()!=null:k!=null }; }
    public void SetLlm(LlmUpdate update)
    {
        Llm.ValidateSettings(update.Settings);
        lock(gate)
        {
            var (old,key)=ReadLlm();
            if(old.BaseUrl!=update.Settings.BaseUrl || update.ClearKey || update.Settings.ReuseTtsKey)key=null;
            if(!string.IsNullOrEmpty(update.Key)) { if(update.Key.Length is <8 or >512 || update.Key.Any(c=>c<33||c>126))throw new InputError("LLM Key 必须为 8–512 个无空白 ASCII 字符。"); key=update.Key; }
            if(update.Settings.ReuseTtsKey && key!=null)throw new InputError("复用模式不接受独立 Key。");
            AtomicWrite(LlmPath,JsonSerializer.SerializeToUtf8Bytes(new LlmUpdate(update.Settings,key),Json));
            llmRevision=Guid.NewGuid().ToString("N");
        }
    }
}
public sealed class Llm(Storage storage, HttpClient client, TimeSpan? totalTimeout = null, TimeSpan? idleTimeout = null)
{
    private readonly SemaphoreSlim slot = new(1,1);
    public static bool Official(string url)=>url=="https://api.xiaomimimo.com/v1";
    public static void ValidateSettings(LlmSettings s)
    {
        if(!Uri.TryCreate(s.BaseUrl,UriKind.Absolute,out var u)||u.Scheme is not ("http" or "https")||u.UserInfo!=""||u.Query!=""||u.Fragment!=""||s.BaseUrl.EndsWith('/'))throw new InputError("Base URL 必须为无认证信息、查询或结尾斜线的 HTTP(S) 地址（包含 /v1）。");
        if(u.Scheme=="http"&&!s.AllowLocal)throw new InputError("HTTP 需管理员明确启用本地/不安全传输。");
        if(s.ReuseTtsKey&&!Official(s.BaseUrl))throw new InputError("仅官方 MiMo LLM 地址可明确复用 TTS Key。");
        if(string.IsNullOrWhiteSpace(s.Model)||s.Model.Length>150||s.Model.Any(char.IsControl)||s.Model.Contains("tts",StringComparison.OrdinalIgnoreCase))throw new InputError("请填写有效的 LLM 模型，不能使用 TTS 模型。");
        if(s.Thinking is not ("omit" or "mimo-enabled" or "mimo-disabled" or "enable-thinking" or "disable-thinking" or "effort-low" or "effort-medium" or "effort-high"))throw new InputError("不支持此 Thinking 协议。");
    }
    public static object Payload(LlmSettings s,CreativeRequest r)
    {
        var body=new Dictionary<string,object>{["model"]=s.Model,["stream"]=s.Streaming,["messages"]=new[]{new{role="system",content=Creative.Prompt(r.Task)},new{role="user",content=Creative.Input(r)}}};
        if(s.Thinking.StartsWith("mimo-"))body["thinking"]=new{type=s.Thinking[5..]};
        if(s.Thinking is "enable-thinking" or "disable-thinking")body["enable_thinking"]=s.Thinking=="enable-thinking";
        if(s.Thinking.StartsWith("effort-"))body["reasoning_effort"]=s.Thinking[7..];
        return body;
    }
    public async Task<CreativeResult> Generate(CreativeRequest r,CancellationToken cancellation)
    {
        if(!await slot.WaitAsync(0,cancellation))throw new InputError("已有 AI 创作请求，请等待取消确认后重试。");
        using var total=CancellationTokenSource.CreateLinkedTokenSource(cancellation);total.CancelAfter(totalTimeout ?? TimeSpan.FromMinutes(3));
        try
        {
            var (settings,key)=storage.ReadLlm();ValidateSettings(settings);
            if(settings.ReuseTtsKey)key=storage.ReadKey();
            if(key==null)throw new InputError("请先配置 LLM Key，或明确选择官方地址复用。");
            string input=Creative.Input(r);
            if(string.IsNullOrWhiteSpace(input)||Encoding.UTF8.GetByteCount(input)>60000)throw new InputError("创作需求不能为空且最多 60000 UTF-8 字节。");
            string text=await Send(settings,key,Payload(settings,r),total.Token);
            total.Token.ThrowIfCancellationRequested();return Creative.Validate(r,text);
        }
        catch(OperationCanceledException) { throw new InputError(cancellation.IsCancellationRequested?"AI 创作已取消，原稿未改变。":"AI 创作超时，原稿未改变；未重试。"); }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException) { throw new InputError("LLM 响应无效或不完整，原稿未改变。"); }
        catch(IOException) { throw new InputError("LLM 连接中断，原稿未改变；未重试。"); }
        catch(HttpRequestException) { throw new InputError("LLM 连接失败，原稿未改变；未重试。"); }
        finally { slot.Release(); }
    }
    // Shared production transport/parser; diagnostics supply only fixed prompts and saved snapshots.
    public async Task<string> Diagnose(LlmSettings settings, string key, CancellationToken token)
    {
        ValidateSettings(settings);
        var payload = (Dictionary<string,object>)Payload(settings,new CreativeRequest("script",new ScriptRequest("请只回答：OK")));
        payload["messages"] = new[] { new { role="system", content="这是服务可用性检测，请返回简短的正常回答。" }, new { role="user", content="请只回答：OK" } };
        return await Send(settings,key,payload,token);
    }
    private async Task<string> Send(LlmSettings settings,string key,object payload,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,settings.BaseUrl+"/chat/completions");
        request.Options.Set(new HttpRequestOptionsKey<bool>("AllowLocal"), settings.AllowLocal);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        request.Content=JsonContent.Create(payload);
        using var headersTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        headersTimeout.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(30));
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,headersTimeout.Token);
        if(!response.IsSuccessStatusCode)throw new UpstreamError((int)response.StatusCode, $"LLM 返回 HTTP {(int)response.StatusCode}，请检查认证、模型、Thinking 协议或限额；未重试。");
        if(response.Content.Headers.ContentLength>1024*1024)throw new InputError("LLM 响应超出 1 MiB。");
        using var stream=await response.Content.ReadAsStreamAsync(token);
        using var buffer=new MemoryStream();var chunk=new byte[4096];
        while(true)
        {
            using var idle=CancellationTokenSource.CreateLinkedTokenSource(token);idle.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(30));
            int count=await stream.ReadAsync(chunk,idle.Token);if(count==0)break;
            if(buffer.Length+count>1024*1024)throw new InputError("LLM 响应超出 1 MiB。");buffer.Write(chunk,0,count);
        }
        var raw=new UTF8Encoding(false,true).GetString(buffer.ToArray());
        string text=settings.Streaming?ParseSse(raw):ParseJson(raw);
        token.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(text))throw new InputError("LLM 最终回答为空。");
        return text;
    }
    public static string ParseJson(string raw)
    {
        using var doc=JsonDocument.Parse(raw);var choices=doc.RootElement.GetProperty("choices");
        if(choices.GetArrayLength()!=1)throw new InputError("LLM 必须返回一个回答。");var c=choices[0];
        if(c.GetProperty("finish_reason").GetString()!="stop")throw new InputError("LLM 未正常结束（截断、拒绝或工具调用），未应用。");
        var message=c.GetProperty("message");
        if(message.TryGetProperty("tool_calls",out var tools)&&tools.ValueKind!=JsonValueKind.Null&&(tools.ValueKind!=JsonValueKind.Array||tools.GetArrayLength()>0) || message.TryGetProperty("refusal",out var refusal)&&refusal.ValueKind!=JsonValueKind.Null&&(refusal.ValueKind!=JsonValueKind.String||!string.IsNullOrEmpty(refusal.GetString())))throw new InputError("LLM 返回拒绝或工具调用，未应用。");
        return message.GetProperty("content").GetString()??"";
    }
    public static string ParseSse(string raw)
    {
        var text=new StringBuilder(); bool done=false,finished=false;var data=new List<string>();
        void Event()
        {
            if(data.Count==0)return;string value=string.Join("\n",data);data.Clear();
            if(done)throw new InputError("LLM 结束后仍返回事件。");
            if(value=="[DONE]"){done=true;return;}
            using var doc=JsonDocument.Parse(value);if(doc.RootElement.TryGetProperty("error",out _))throw new InputError("LLM 流返回错误，未应用。");
            foreach(var c in doc.RootElement.GetProperty("choices").EnumerateArray())
            {
                if(c.GetProperty("index").GetInt32()!=0 || finished)throw new InputError("LLM 流选择或顺序无效。");
                var delta=c.GetProperty("delta");
                if(delta.TryGetProperty("tool_calls",out var calls)&&calls.ValueKind!=JsonValueKind.Null&&(calls.ValueKind!=JsonValueKind.Array||calls.GetArrayLength()>0))throw new InputError("不支持工具调用。");
                if(delta.TryGetProperty("content",out var part)&&part.ValueKind==JsonValueKind.String)text.Append(part.GetString());
                if(c.TryGetProperty("finish_reason",out var reason)&&reason.ValueKind!=JsonValueKind.Null){if(reason.GetString()!="stop")throw new InputError("LLM 流截断或异常结束。");finished=true;}
            }
        }
        using var reader=new StringReader(raw);string? line;
        while((line=reader.ReadLine())!=null){if(line=="")Event();else if(line.StartsWith("data:"))data.Add(line[5..].TrimStart(' '));}
        if(data.Count>0||!done||!finished)throw new InputError("LLM SSE 不完整，未应用。");return text.ToString();
    }
    public static SocketsHttpHandler Handler()=>new(){AllowAutoRedirect=false,UseProxy=false,PooledConnectionLifetime=TimeSpan.Zero,ConnectTimeout=TimeSpan.FromSeconds(15),
        ConnectCallback=async (context,token)=>
        {
            context.InitialRequestMessage.Options.TryGetValue(new HttpRequestOptionsKey<bool>("AllowLocal"),out bool local);
            var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,token);
            if(addresses.Length==0 || !local&&addresses.Any(ip=>!PublicAddress(ip)))throw new HttpRequestException("Address denied");
            var socket=new Socket(SocketType.Stream,ProtocolType.Tcp);
            try { await socket.ConnectAsync(addresses,context.DnsEndPoint.Port,token);return new NetworkStream(socket,true); } catch {socket.Dispose();throw;}
        }};
    public static bool PublicAddress(IPAddress ip)
    {
        if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();var b=ip.GetAddressBytes();
        if(IPAddress.IsLoopback(ip))return false;
        return ip.AddressFamily==AddressFamily.InterNetwork ? !(b[0] is 0 or 10 or 127 || b[0]>=224 || b[0]==169&&b[1]==254 || b[0]==172&&b[1]>=16&&b[1]<=31 || b[0]==192&&b[1]==168 || b[0]==100&&b[1]>=64&&b[1]<=127) : !ip.IsIPv6Multicast&&!ip.IsIPv6LinkLocal&&!ip.IsIPv6SiteLocal&&!(b[0]>=0xfc&&b[0]<=0xfd)&&!ip.Equals(IPAddress.IPv6Any);
    }
}
