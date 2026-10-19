using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Studio;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (InputError) { Check(true, name); return; } throw new Exception("FAIL " + name); }
string longText = string.Concat(Enumerable.Repeat("你好👨‍👩‍👧‍👦e\u0301。[轻笑]Hello!\r\n", 150));
var request = new ScriptRequest(longText, "冰糖", "自然温柔", "combined", "讲述者", "夜晚", "放慢", "清亮", true, true, 75);
var plan = Composition.Plan(request);
Check(string.Concat(plan.Segments) == longText, "segmentation preserves every byte and newline");
Check(plan.Assistant.All(s => Encoding.UTF8.GetByteCount(s) <= 3000), "UTF8 budget includes prefix");
Check(plan.Segments.All(s => !s.StartsWith('\u200d') && !s.StartsWith('\u0301') && !s.StartsWith('\n')), "grapheme and CRLF boundaries");
Check(plan.Segments.All(s => s.Count(c => c=='[')==s.Count(c => c==']')), "annotations never split");
Check(plan.Prefix == "(唱歌)(清亮)" && plan.User == "自然温柔\n角色：讲述者\n场景：夜晚\n指导：放慢", "explicit director and singing composition");
var payload = JsonSerializer.SerializeToElement(Mimo.Payload(request, plan, plan.Segments[0]));
Check(payload.GetProperty("model").GetString()=="mimo-v2.5-tts" && !payload.GetProperty("stream").GetBoolean(), "nonstreaming preset protocol");
Check(payload.GetProperty("messages")[0].GetProperty("role").GetString()=="user" && payload.GetProperty("messages")[1].GetProperty("role").GetString()=="assistant", "message roles");
Check(payload.GetProperty("audio").GetProperty("voice").GetString()=="冰糖", "official voice ID");
Reject(()=>Composition.Plan(request with { Split=false }), "no hidden segmentation when disabled");
Reject(()=>Composition.Plan(request with { Text=new string('x',60001) }), "text size cap");
Reject(()=>Composition.Plan(request with { Tags="(坏标签)" }), "invalid overall tag rejected");
Reject(()=>Composition.Plan(request with { Text="["+new string('中',1100)+"]" }), "oversized atomic annotation rejected");
var normal = Composition.Plan(new("中文 English 👋"));
Check(normal.User=="" && normal.Assistant[0]=="中文 English 👋", "default has no implicit text rewrite");
byte[] wave = Wave.Merge([new(24000,[0,0,10,0]),new(24000,[20,0,30,0])]);
Check(wave.Length==52 && Wave.Decode(wave).Data.SequenceEqual(new byte[]{0,0,10,0,20,0,30,0}), "one valid RIFF with ordered PCM");
Reject(()=>Wave.Decode([1,2,3]), "truncated WAV rejected");
var truncated=wave[..^1]; Reject(()=>Wave.Decode(truncated), "partial WAV rejected");
var invalid=(byte[])wave.Clone();invalid[32]=99;Reject(()=>Wave.Decode(invalid), "invalid block alignment rejected");
Reject(()=>Wave.Merge([new(24000,[0,0]),new(48000,[0,0])]), "sample rate mismatch fails closed");
byte[] stereo;
using(var stream=new MemoryStream()) { using(var w=new BinaryWriter(stream,Encoding.ASCII,true)){
 w.Write("RIFF"u8);w.Write(40);w.Write("WAVEfmt "u8);w.Write(16);w.Write((ushort)1);w.Write((ushort)2);w.Write(24000);w.Write(96000);w.Write((ushort)4);w.Write((ushort)16);w.Write("data"u8);w.Write(4);w.Write((short)1000);w.Write((short)3000); } stereo=stream.ToArray(); }
Check(BitConverter.ToInt16(Wave.Decode(stereo).Data)==2000, "stereo converted to mono PCM16");
string folder=Path.Combine(Path.GetTempPath(),"mimo-tests-"+Guid.NewGuid().ToString("N"));
try
{
 var storage=new Storage(Path.Combine(folder,"config"),Path.Combine(folder,"data"),Path.Combine(folder,"tmp"));
 storage.SetKey("test-only-key-not-a-secret");
 Check(storage.ReadKey()=="test-only-key-not-a-secret", "backend private Key read");
 if(OperatingSystem.IsLinux()) Check(File.GetUnixFileMode(Path.Combine(storage.Config,"mimo-key"))==(UnixFileMode.UserRead|UnixFileMode.UserWrite), "key mode 0600");
 var a=storage.Commit("1000",request,wave);
 Check(storage.Get(a.Id,"1001")==null && storage.Get("../config","1000")==null, "asset ownership and traversal");
 Check(storage.Save(a.Id,"1000")?.Saved==true, "save existing result without synthesis");
 if(OperatingSystem.IsLinux())File.SetUnixFileMode(Path.Combine(storage.Config,"mimo-key"),UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
 var reopened=new Storage(storage.Config,storage.Data,storage.Temp);
 if(OperatingSystem.IsLinux())Check(File.GetUnixFileMode(Path.Combine(storage.Config,"mimo-key"))==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"retained Key mode hardened after platform ACL reset");
 Check(reopened.List("1000").Length==1 && reopened.ReadKey()!=null, "saved assets and Key persist across restart");
 var handler=new MockMimo(wave);
 var mimo=new Mimo(new HttpClient(handler));
 var shortRequest=new ScriptRequest(new string('a',51)+"。"+new string('b',51),SegmentCharacters:50);
 var shortPlan=Composition.Plan(shortRequest);
 var pcm=await mimo.Generate(storage.ReadKey()!,request,plan,plan.Segments[0],default);
 Check(pcm.Rate==24000 && handler.Calls==1 && handler.Requests[0].Contains("assistant"), "controlled HTTP returns validated WAV");
 foreach(var broken in new[]{"not json","{}","{\"choices\":[{\"message\":{\"audio\":{\"data\":\"!\"}}}]}","{\"choices\":[{\"message\":{\"audio\":{\"data\":\"YWJj\"}}}]}"}) {
  handler.Raw=broken;try{await mimo.Generate("mock-key",request,plan,plan.Segments[0],default);throw new Exception("accepted invalid response");}catch(InputError){}
 }
 Check(true,"invalid JSON, fields, Base64 and WAV responses rejected");handler.Raw=null;
 handler.Status=HttpStatusCode.Unauthorized;
 try{await mimo.Generate("mock-key",request,plan,plan.Segments[0],default);throw new Exception("accepted HTTP error");}catch(InputError e){Check(!e.Message.Contains("mock-key"),"upstream error body and Key not exposed");}
 handler.Status=HttpStatusCode.OK;
 handler.Oversized=true;
 try{await mimo.Generate("mock-key",request,plan,plan.Segments[0],default);throw new Exception("accepted huge response");}catch(InputError){Check(true,"oversize response rejected before read");}handler.Oversized=false;
 var jobs=new Jobs(storage,mimo,new Lifetime());
 var j=jobs.Start("1000",shortRequest);
 while(jobs.Get(j.Id,"1000")!.State=="running")await Task.Delay(5);
 var done=jobs.Get(j.Id,"1000")!;
 Check(done.State=="succeeded"&&done.Completed==shortPlan.Segments.Length,"multi-segment task succeeds");
 int calls=handler.Calls;
 for(int n=0;n<3;n++){using var f=storage.Open(done.AssetId!,"1000");Check(f?.Length>44,"reused audio read "+n);}
 storage.Save(done.AssetId!,"1000");Check(handler.Calls==calls,"play download save cause zero additional MiMo calls");
 handler.Delay=true;
 var canceled=jobs.Start("1000",new("取消任务"));
 try{jobs.Start("1000",new("双重任务"));throw new Exception("concurrency bypass");}catch(BusyError){Check(true,"one global generation slot");}
 Check(jobs.Get(canceled.Id,"1001")==null,"job ownership");
 jobs.Cancel(canceled.Id,"1000");
 while(jobs.Get(canceled.Id,"1000")!.State=="canceling")await Task.Delay(5);
 Check(jobs.Get(canceled.Id,"1000") is {State:"canceled",AssetId:null},"cancel confirms no successful asset");
 Check(!Directory.GetDirectories(storage.Data,".stage-*").Any(),"no partial committed assets");
 handler.Delay=false;handler.Raw="{}";
 var failed=jobs.Start("1000",new("失败任务"));while(jobs.Get(failed.Id,"1000")!.State=="running")await Task.Delay(5);
 Check(jobs.Get(failed.Id,"1000") is {State:"failed",AssetId:null},"failed task has no result");
 Check(storage.Delete(a.Id,"1000")&&storage.Get(a.Id,"1000")==null,"asset deletion");
 storage.SetKey(null);Check(storage.ReadKey()==null,"Key clear");
}
finally{Directory.Delete(folder,true);}
var ctx=new DefaultHttpContext();ctx.Request.Headers["X-Trim-Userid"]="1000";ctx.Request.Headers["X-Trim-Isadmin"]="true";ctx.Request.Headers["X-Trim-Username"]="admin";
Check(!Gateway.Admin(ctx),"forged headers alone cannot authenticate");ctx.Features.Set(new GatewayPeer(true));Check(Gateway.Admin(ctx),"trusted root peer admin accepted");
ctx.Request.Headers["X-Trim-Isadmin"]="false";Check(!Gateway.Admin(ctx),"non-admin rejected");ctx.Request.Headers.Remove("X-Trim-Userid");Check(!Gateway.Admin(ctx),"missing identity rejected");
ctx.Request.Host=new HostString("nas.test");ctx.Request.Headers.Origin="http://nas.test:5666";ctx.Request.Headers.Referer="http://nas.test:5666/app/mimotts/";
Check(Gateway.SafeOrigin(ctx.Request),"fnOS stripped port accepted with matching browser referer");
ctx.Request.Headers.Origin="http://evil.test";Check(!Gateway.SafeOrigin(ctx.Request),"cross-origin rejected");
ctx.Request.Headers.Origin="null";Check(!Gateway.SafeOrigin(ctx.Request),"opaque origin rejected");
ctx.Request.Headers.Origin="http://nas.test:1234";Check(!Gateway.SafeOrigin(ctx.Request),"mismatched referer port rejected");
await Pr2Tests.Run(Check, Reject);
await Pr3Tests.Run(Check, Reject);
await Pr4Tests.Run(Check);
Console.WriteLine($"{passed} checks passed");

sealed class MockMimo(byte[] wave):HttpMessageHandler
{
 public int Calls; public bool Delay, Oversized; public string? Raw; public HttpStatusCode Status=HttpStatusCode.OK; public List<string> Requests=[];
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  Calls++;Requests.Add(await request.Content!.ReadAsStringAsync(ct));
  if(Delay)await Task.Delay(Timeout.Infinite,ct);
  var response=new HttpResponseMessage(Status){Content=new StringContent(Raw??JsonSerializer.Serialize(new{choices=new[]{new{message=new{audio=new{data=Convert.ToBase64String(wave)}}}}}))};
  if(Oversized)response.Content.Headers.ContentLength=100*1024*1024;
  return response;
 }
}
sealed class Lifetime:IHostApplicationLifetime
{
 public CancellationToken ApplicationStarted=>CancellationToken.None;
 public CancellationToken ApplicationStopping=>CancellationToken.None;
 public CancellationToken ApplicationStopped=>CancellationToken.None;
 public void StopApplication(){}
}
