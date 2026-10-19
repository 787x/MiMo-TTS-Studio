using Studio;
using System.Net;
using System.Text;
using System.Text.Json;

static class Pr3Tests
{
 public static async Task Run(Action<bool,string> check,Action<Action,string> reject)
 {
  foreach(var (source,output) in new[]{("你好","[轻笑]你好[叹气]"),("[大笑]啊","[轻笑][大笑][大笑]啊"),("e\u0301👨‍👩‍👧‍👦\r\n","[轻笑]e\u0301[叹气]👨‍👩‍👧‍👦\r\n"),("[轻笑][轻笑]","[轻笑][轻笑][轻笑]")})check(Creative.OnlyTagInsertions(source,output),"legal tag insertion preserves "+source);
  foreach(var (source,output) in new[]{("原文","原稿"),("e\u0301","e[轻笑]\u0301"),("👨‍👩","👨[轻笑]‍👩"),("\r\n","\r[轻笑]\n"),("[原标签]","[原[轻笑]标签]"),("a b","ab"),("啊","[非法]啊"),("é","e\u0301")})check(!Creative.OnlyTagInsertions(source,output),"unsafe tag insertion rejected "+source);
  check(Creative.Prompt("tags").Contains("不要回答原稿"),"tags have a dedicated insertion-only prompt");
  var draft=new ScriptRequest("写一段欢迎词");
  check(Creative.Validate(new("script",draft),"欢迎。") .Text=="欢迎。","script validates without TTS");
  check(Creative.Validate(new("description",draft with{SynthesisMode="design"}),"温暖女声").Text=="温暖女声","design description without preexisting description");
  check(Creative.Validate(new("director",draft),"{\"role\":\"主持\",\"scene\":\"晚会\",\"direction\":\"轻快\"}").Role=="主持","director strict three fields");
  foreach(var bad in new[]{"{}","```json\n{}\n```","{\"role\":\"a\",\"scene\":\"b\",\"direction\":\"\"}","{\"role\":\"a\",\"scene\":\"b\",\"direction\":\"c\",\"extra\":1}"})reject(()=>Creative.Validate(new("director",draft),bad),"invalid director atomic rejection");
  reject(()=>Creative.Validate(new("style",draft),new string('中',2100)),"combined style byte limit");
  reject(()=>Creative.Validate(new("script",draft with{SynthesisMode="design",VoiceDescription="温暖"}),new string('中',1100)),"design script byte limit");
  reject(()=>Creative.Input(new("lyrics",draft)),"lyrics requires preset singing");
  reject(()=>Creative.Input(new("tags",draft with{SynthesisMode="design"})),"design tag operation forbidden");
  var settings=new LlmSettings();Llm.ValidateSettings(settings);
  foreach(var s in new[]{settings with{BaseUrl="https://evil.test/v1",ReuseTtsKey=true},settings with{BaseUrl="http://127.0.0.1/v1"},settings with{BaseUrl="https://key@evil.test/v1"},settings with{Model="mimo-v2.5-tts"}})reject(()=>Llm.ValidateSettings(s),"unsafe settings rejected");
  check(!Llm.PublicAddress(IPAddress.Parse("127.0.0.1"))&&!Llm.PublicAddress(IPAddress.Parse("::ffff:192.168.1.1")),"local address filtering");
  foreach(var thinking in new[]{"omit","mimo-enabled","mimo-disabled","enable-thinking","disable-thinking","effort-low"}) {var p=JsonSerializer.Serialize(Llm.Payload(settings with{Thinking=thinking},new("script",draft)));check(!p.Contains("audio")&&p.Contains("system"),"independent LLM payload "+thinking);}
  string json="{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"欢迎\",\"reasoning_content\":\"secret reasoning\"}}]}";
  check(Llm.ParseJson(json)=="欢迎","reasoning excluded");
  check(Llm.ParseJson(json.Replace("\"content\":", "\"tool_calls\":[],\"refusal\":null,\"content\":"))=="欢迎","empty optional tools are not a tool call");reject(()=>Llm.ParseJson(json.Replace("stop","length")),"truncation rejected");
  string sse="data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"欢迎\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
  check(Llm.ParseSse(sse)=="欢迎","complete SSE");reject(()=>Llm.ParseSse(sse.Replace("data: [DONE]\n\n","")),"missing DONE rejected");reject(()=>Llm.ParseSse(sse.Replace("stop","length")),"SSE truncation rejected");reject(()=>Llm.ParseSse(sse+"data: {\"error\":{}}\n\n"),"error after DONE rejected");
  string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
  try {
   var storage=new Storage(Path.Combine(root,"etc"),Path.Combine(root,"audio"),Path.Combine(root,"tmp"));storage.SetKey("tts-test-secret");storage.SetLlm(new(settings,"llm-test-secret"));
   check(!JsonSerializer.Serialize(storage.LlmStatus()).Contains("llm-test-secret"),"status never exposes Key");
   storage.SetLlm(new(settings with{BaseUrl="https://other.example/v1"}));check(storage.ReadLlm().Key==null&&storage.ReadKey()=="tts-test-secret","address switch clears independent Key without changing TTS");
   storage.SetLlm(new(settings,"llm-test-secret"));var reopened=new Storage(storage.Config,storage.Data,storage.Temp);check(reopened.ReadLlm().Key=="llm-test-secret","LLM private configuration persists");
   if(OperatingSystem.IsLinux())check(File.GetUnixFileMode(Path.Combine(storage.Config,"llm-private"))==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"LLM credential 0600");
   var mock=new LlmMock(json);var llm=new Llm(storage,new HttpClient(mock));
   check((await llm.Generate(new("script",draft),default)).Text=="欢迎"&&mock.Calls==1,"controlled HTTP LLM success");
   mock.Status=HttpStatusCode.TooManyRequests;try{await llm.Generate(new("script",draft),default);throw new Exception("accepted 429");}catch(InputError e){check(!e.Message.Contains("secret")&&mock.Calls==2,"429 no retry and redacted");}
   mock.Status=HttpStatusCode.OK;mock.Delay=true;using var cancel=new CancellationTokenSource();var running=llm.Generate(new("script",draft),cancel.Token);
   try{await llm.Generate(new("script",draft),default);throw new Exception("accepted concurrent request");}catch(InputError){check(true,"bounded concurrency");}
   cancel.Cancel();try{await running;throw new Exception("cancel failed");}catch(InputError){check(true,"cancel propagation");}
   mock.Delay=true;var timed=new Llm(storage,new HttpClient(mock),TimeSpan.FromMilliseconds(20),TimeSpan.FromMilliseconds(20));
   try{await timed.Generate(new("script",draft),default);throw new Exception("timeout failed");}catch(InputError){check(true,"total/header timeout without retry");}
   mock.Delay=false;
   foreach(var broken in new[]{"",json.Replace("stop","length"),json.Replace("欢迎",""),"{}",new string('x',1024*1024+1)}){
    mock.Raw=broken;int calls=mock.Calls;try{await llm.Generate(new("script",draft),default);throw new Exception("accepted broken response");}catch(InputError){check(mock.Calls==calls+1,"invalid/empty/oversize LLM result rejected without retry");}
   }
   check(storage.List("1000").Length==0&&storage.ListSamples("1000").Length==0,"AI creates no audio or samples");
  }finally{Directory.Delete(root,true);}
 }
 sealed class LlmMock(string json):HttpMessageHandler
 {
  public int Calls;public bool Delay;public string? Raw;public HttpStatusCode Status=HttpStatusCode.OK;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken token){Calls++;if(Delay)await Task.Delay(Timeout.Infinite,token);return new(Status){Content=new StringContent(Raw??json,Encoding.UTF8,"application/json")};}
 }
}
