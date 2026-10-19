using System.Net;
using System.Text.Json;
using Studio;

static class Pr4Tests
{
 public static async Task Run(Action<bool,string> check)
 {
  string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
  try
  {
   var storage=new Storage(Path.Combine(root,"etc"),Path.Combine(root,"audio"),Path.Combine(root,"tmp"));
   var wave=Wave.Merge([new(24000,new byte[48000])]);
   var tts=new ProbeHandler{Body=JsonSerializer.Serialize(new{choices=new[]{new{message=new{audio=new{data=Convert.ToBase64String(wave)}}}}})};
   var chat=new ProbeHandler{Body="{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"OK\"}}]}"};
   var llm=new Llm(storage,new HttpClient(chat));var mimo=new Mimo(new HttpClient(tts));var diagnostics=new Diagnostics(storage,mimo,llm);
   DiagnosticInput Input(string service,string? sample=null)=>new(service,sample,diagnostics.Check(new(service,sample),"1000").Revision,Guid.NewGuid().ToString("N"));
   check(!diagnostics.Check(new("preset"),"1000").Ready&&tts.Calls==0,"probe precheck missing Key sends nothing");
   var missing=await diagnostics.Run(Input("preset"),"1000",default);check(missing.Category=="missing_key"&&tts.Calls==0,"probe missing Key cannot call upstream");
   storage.SetKey("diagnostic-secret");
   check(diagnostics.Check(new("preset"),"1000").Ready&&tts.Calls==0,"probe configuration ready is offline only");
   foreach(var mode in new[]{"preset","design"})
   {
    var result=await diagnostics.Run(Input(mode),"1000",default);var body=JsonSerializer.Deserialize<JsonElement>(tts.Requests.Last());
    check(result.State=="succeeded"&&body.GetProperty("model").GetString()==Composition.Model(mode),"probe "+mode+" validates actual WAV");
    check(mode=="preset"?body.GetProperty("audio").GetProperty("voice").GetString()=="冰糖":!body.GetProperty("audio").TryGetProperty("voice",out _),"probe "+mode+" exact voice protocol");
   }
   check(!diagnostics.Check(new("clone"),"1000").Ready,"clone probe requires explicit sample");
   var sample=storage.CreateSample("1000","probe reference","wav",wave);
   check(!diagnostics.Check(new("clone",sample.Id),"2000").Ready,"clone probe denies another owner");
   var clone=await diagnostics.Run(Input("clone",sample.Id),"1000",default);
   check(clone.State=="succeeded"&&tts.Requests.Last().Contains("data:audio/wav;base64,"),"clone probe resolves private sample snapshot");
   int calls=tts.Calls;
   var stale=Input("preset");storage.SetKey("diagnostic-updated-secret");
   check((await diagnostics.Run(stale,"1000",default)).State=="expired"&&calls==tts.Calls,"stale credential revision prevents upstream request");
   foreach(var (status,category) in new[]{(401,"authentication"),(403,"authentication"),(429,"rate_limit"),(400,"request_rejected"),(422,"request_rejected"),(503,"upstream")})
   {tts.Status=(HttpStatusCode)status;calls=tts.Calls;var result=await diagnostics.Run(Input("preset"),"1000",default);check(result.Category==category&&tts.Calls==calls+1&&!JsonSerializer.Serialize(result).Contains("secret"),"probe structured HTTP "+status+" no retry or secret");}
   tts.Status=HttpStatusCode.OK;var valid=tts.Body;
   foreach(var bad in new[]{"{}","{\"choices\":[{\"message\":{\"audio\":{\"data\":\"\"}}}]}","{\"choices\":[{\"message\":{\"audio\":{\"data\":\"AA==\"}}}]}"})
   {tts.Body=bad;check((await diagnostics.Run(Input("preset"),"1000",default)).State=="failed","probe rejects missing/empty/invalid WAV");}
   tts.Body=valid;tts.Pause=true;
   var runningInput=Input("preset");var running=diagnostics.Run(runningInput,"1000",default);
   check(!await diagnostics.CancelAndWait(runningInput.Id!,"2000"),"probe cancellation owner isolation");
   check((await diagnostics.Run(Input("preset"),"1000",default)).Category=="busy","bounded probe slot without queue reports diagnostic busy");
   check(await diagnostics.CancelAndWait(runningInput.Id!,"1000")&&(await running).State=="canceled","probe cancel waits for upstream cancellation completion");
   tts.Pause=false;tts.Gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
   runningInput=Input("preset");running=diagnostics.Run(runningInput,"1000",default);
   var cancelWait=diagnostics.CancelAndWait(runningInput.Id!,"1000");check(!cancelWait.IsCompleted,"cancel endpoint waits for real upstream completion even if handler ignores cancellation");
   tts.Gate.SetResult();await cancelWait;check((await running).State=="canceled","late valid WAV after cancellation cannot report success");tts.Gate=null;tts.Pause=true;
   var timeout=new Diagnostics(storage,mimo,llm,TimeSpan.FromMilliseconds(15));var ti=new DiagnosticInput("preset",Revision:timeout.Check(new("preset"),"1000").Revision,Id:Guid.NewGuid().ToString("N"));
   check((await timeout.Run(ti,"1000",default)).Category=="timeout","probe total timeout distinct from normal cancellation");
   runningInput=Input("preset");running=diagnostics.Run(runningInput,"1000",default);storage.SetKey("new-secret-again");await diagnostics.CancelAndWait(runningInput.Id!,"1000");check((await running).State=="expired","configuration change makes late probe obsolete");tts.Pause=false;
   storage.SetLlm(new(new(Thinking:"mimo-enabled",Streaming:false,ReuseTtsKey:true)));
   check((await diagnostics.Run(Input("llm"),"1000",default)).State=="succeeded","LLM probe complete JSON");
   var lp=JsonSerializer.Deserialize<JsonElement>(chat.Requests.Last());check(lp.GetProperty("thinking").GetProperty("type").GetString()=="enabled"&&!lp.TryGetProperty("audio",out _)&&lp.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("可用性检测"),"LLM probe fixed prompt and saved Thinking");
   storage.SetLlm(new(new(Thinking:"effort-low",Streaming:true,ReuseTtsKey:true)));
   string sse="data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
   chat.Body=sse;check((await diagnostics.Run(Input("llm"),"1000",default)).State=="succeeded"&&chat.Requests.Last().Contains("reasoning_effort"),"LLM probe saved SSE and Thinking protocol");
   foreach(var bad in new[]{sse.Replace("data: [DONE]\n\n",""),sse.Replace("stop","length"),sse.Replace("content","reasoning_content"),"data: [DONE]\n\n"})
   {chat.Body=bad;check((await diagnostics.Run(Input("llm"),"1000",default)).Category=="invalid_response","LLM probe rejects partial, truncated, reasoning-only or empty stream");}
   check(storage.List("1000").Length==0&&storage.ListSamples("1000").Length==1&&Directory.GetFiles(storage.Temp).Length==0,"all probes create zero audio assets samples or temporary files");
   // Normal TTS is a separate slot and survives diagnostic cancellation.
   var jobs=new Jobs(storage,mimo,new Lifetime());tts.Pause=true;var job=jobs.Start("1000",new("normal task"));runningInput=Input("preset");running=diagnostics.Run(runningInput,"1000",default);await diagnostics.CancelAndWait(runningInput.Id!,"1000");await running;
   check(jobs.Get(job.Id,"1000")!.State=="running","probe cancellation does not cancel normal TTS");jobs.Cancel(job.Id,"1000");while(jobs.Get(job.Id,"1000")!.State=="canceling")await Task.Delay(5);
   tts.Pause=false;chat.Pause=true;using var creativeCancel=new CancellationTokenSource();
   var creative=llm.Generate(new("script",new ScriptRequest("normal creative task")),creativeCancel.Token);
   runningInput=Input("llm");running=diagnostics.Run(runningInput,"1000",default);await diagnostics.CancelAndWait(runningInput.Id!,"1000");await running;
   check(!creative.IsCompleted,"probe cancellation does not cancel normal AI creative request");creativeCancel.Cancel();try{await creative;}catch(InputError){}

  }
  finally{Directory.Delete(root,true);}
 }
 sealed class ProbeHandler:HttpMessageHandler
 {
  public TaskCompletionSource? Gate;public string Body="";public HttpStatusCode Status=HttpStatusCode.OK;public bool Pause;public int Calls;public List<string> Requests=[];
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {Calls++;Requests.Add(await request.Content!.ReadAsStringAsync(ct));if(Pause)await Task.Delay(Timeout.Infinite,ct);if(Gate!=null)await Gate.Task;return new(Status){Content=new StringContent(Body)};}
 }
}
