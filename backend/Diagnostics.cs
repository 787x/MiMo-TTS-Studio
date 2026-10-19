using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Studio;
public sealed record DiagnosticInput(string Service, string? SampleId = null, string? Revision = null, string? Id = null);
public sealed record DiagnosticCheck(string Revision, bool Ready, string Category, string Reason, string Target);
public sealed record DiagnosticResult(string State, string Category, string Reason, long ElapsedMs, DateTimeOffset TestedAt, DiagnosticCheck Configuration);

// One active probe, no queue, no assets or persistent health state. Separate from Jobs/creative cancellation.
public sealed class Diagnostics(Storage storage, Mimo mimo, Llm llm, TimeSpan? budget = null)
{
    private readonly object gate = new();
    private (string Id, string Owner, CancellationTokenSource Cancellation, TaskCompletionSource Done)? active;
    private static void Validate(DiagnosticInput input)
    {
        if(input.Service is not ("preset" or "design" or "clone" or "llm"))throw new InputError("未知检测服务。");
    }
    public DiagnosticCheck Check(DiagnosticInput input, string owner)
    {
        Validate(input);
        var config=storage.DiagnosticConfig(input.Service=="llm");
        string target;
        if(input.Service=="llm")
        {
            try { Llm.ValidateSettings(config.Settings); }
            catch(InputError) { return new(config.Revision,false,"configuration","请先保存合法的 LLM 配置。","LLM 配置无效"); }
            target=new Uri(config.Settings.BaseUrl).GetLeftPart(UriPartial.Authority)+"（路径隐藏） · "+config.Settings.Model+" · "+config.Settings.Thinking+" · "+(config.Settings.Streaming?"SSE":"JSON");
        }
        else target="api.xiaomimimo.com · "+Composition.Model(input.Service)+(input.Service=="preset"?" · 冰糖":input.Service=="design"?" · 固定短音色描述":"");
        string revision=config.Revision;
        if(input.Service=="clone")
        {
            var sample=input.SampleId==null?null:storage.GetSample(input.SampleId,owner);
            if(sample==null)return new(revision,false,"sample_required","请明确选择自己的参考样本；检测会上传该音频。",target);
            // Only metadata identity, never audio or a credential-derived representation.
            revision+="/"+sample.Id+"/"+sample.Sha256;
            target+=" · "+sample.Name+" · "+sample.Format+" · "+sample.DurationSeconds.ToString("F2")+" 秒";
        }
        return config.Key==null?new(revision,false,"missing_key","请先保存对应服务的 Key。",target):new(revision,true,"ready","配置就绪，尚不代表服务可用。",target);
    }
    public bool Cancel(string id,string owner)
    {
        lock(gate) { if(active is not {} run || run.Id!=id || run.Owner!=owner)return false;canceled.Add(owner+"/"+id);run.Cancellation.Cancel();return true; }
    }
    public async Task<bool> CancelAndWait(string id,string owner)
    {
        Task? done;
        lock(gate){if(active is not {} run||run.Id!=id||run.Owner!=owner)return false;done=run.Done.Task;Cancel(id,owner);}
        await done;return true;
    }
    public async Task<DiagnosticResult> Run(DiagnosticInput input,string owner,CancellationToken aborted)
    {
        if(input.Id==null||!Storage.ValidId(input.Id))throw new InputError("检测 ID 无效。");
        var check=Check(input,owner);
        if(!check.Ready||input.Revision!=check.Revision)return new("expired",check.Ready?"configuration_changed":check.Category,check.Ready?"配置已变化，请重新预检查。":check.Reason,0,DateTimeOffset.UtcNow,check);
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cancellation.CancelAfter(budget??TimeSpan.FromSeconds(90));
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(gate) { if(active!=null)return new("failed","busy","已有服务检测正在运行或取消中，请等待确认。",0,DateTimeOffset.UtcNow,check);active=(input.Id,owner,cancellation,done); }
        var elapsed=Stopwatch.StartNew();
        string state="succeeded",category="success",reason="本次真实请求完整通过校验；未保存音频或候选。";
        try
        {
            var config=storage.DiagnosticConfig(input.Service=="llm");
            // Snapshot read must still match the local precheck; never send with another revision.
            if(!check.Revision.StartsWith(config.Revision,StringComparison.Ordinal))return new("expired","configuration_changed","配置已变化，请重新检测。",0,DateTimeOffset.UtcNow,check);
            if(input.Service=="llm")await llm.Diagnose(config.Settings,config.Key!,cancellation.Token);
            else
            {
                string? sample=null;
                if(input.Service=="clone")
                {
                    var snapshot=storage.SnapshotSample(input.SampleId!,owner);
                    if(!check.Revision.EndsWith("/"+snapshot.Metadata.Id+"/"+snapshot.Metadata.Sha256,StringComparison.Ordinal))throw new InputError("样本发生变化。");
                    sample=snapshot.DataUrl;
                }
                var request=new ScriptRequest("你好，语音服务检测。",Voice:input.Service=="preset"?"冰糖":"mimo_default",Split:false,SynthesisMode:input.Service,
                    VoiceDescription:input.Service=="design"?"清晰自然的成年女性声音，语气平静。":"",SourceSampleId:input.SampleId);
                var plan=Composition.Plan(request);
                await mimo.Generate(config.Key!,request,plan,plan.Segments.Single(),cancellation.Token,sample);
            }
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch(UpstreamError e)
        {
            state="failed";
            (category,reason)=e.Status switch {
                401 or 403=>("authentication","认证或服务权限被拒绝"),
                429=>("rate_limit","可能为限流或额度限制，请检查服务控制台"),
                400 or 404 or 422=>("request_rejected","服务拒绝参数、模型或协议"),
                >=500=>("upstream","上游服务异常"), _=>("http","上游 HTTP 请求失败") };
            reason+=$"（HTTP {e.Status}），未重试。";
        }
        catch(OperationCanceledException) { state=aborted.IsCancellationRequested||IsCanceled(input.Id,owner)?"canceled":"failed";category=state=="canceled"?"canceled":"timeout";reason=state=="canceled"?"检测已取消。":"检测超时；未重试。"; }
        catch(EmptyAudioError) { state="failed";category="empty_audio";reason="服务返回空音频。"; }
        catch(Exception e) when(e is HttpRequestException or IOException) {state="failed";category="connection";reason="连接失败或中断，未重试。";}
        catch(Exception e) when(e is InputError or JsonException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException or FormatException or IndexOutOfRangeException)
        { state="failed";category="invalid_response";reason="响应、音频或样本无效、不完整或为空。"; }
        finally { lock(gate) { active=null;canceled.Remove(owner+"/"+input.Id);done.TrySetResult(); } }
        if(Check(input,owner).Revision!=check.Revision){state="expired";category="configuration_changed";reason="配置或样本已变化，旧检测结果过期。";}
        return new(state,category,reason,elapsed.ElapsedMilliseconds,DateTimeOffset.UtcNow,check);
    }
    // User cancellation is tracked separately from the timeout token.
    private readonly HashSet<string> canceled = [];
    private bool IsCanceled(string id,string owner) { lock(gate)return canceled.Remove(owner+"/"+id); }
}
