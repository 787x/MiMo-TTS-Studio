using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Studio;
using Studio.Agent;
using MiMoStudio.Agent;

static class Pr7FeedbackTests
{
    public static async Task Run(Action<bool,string> check,string? fixtures=null)
    {
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        using(var stream=new MemoryStream())
        {
            var writer=new AgentIpc.ResponseWriter(stream,default);
            try{await writer.Send(AgentReply.Ok(new{}),canceled.Token);}catch(OperationCanceledException){}
            await writer.TryFailure(new OperationCanceledException(canceled.Token));
            stream.Position=0;
            var reply=JsonSerializer.Deserialize<AgentReply>(await Wire.Read(stream,Wire.MaxJson,default),Wire.Json)!;
            check(!reply.Success&&reply.Code=="timeout"&&reply.ExitCode==7&&stream.Position==stream.Length,"feedback canceled operation token still delivers exactly one complete timeout frame");
        }
        foreach(int failAt in new[]{1,2})
        {
            using var stream=new FailingStream(failAt);var writer=new AgentIpc.ResponseWriter(stream,default);
            try{await writer.Send(AgentReply.Ok(new{}),default);}catch(IOException){}
            int writes=stream.Writes;await writer.TryFailure(new IOException());
            check(writes==failAt&&stream.Writes==writes,"feedback write failure at header/body never appends second response "+failAt);
        }
        using(var stream=new FailingStream(int.MaxValue))
        {
            var writer=new AgentIpc.ResponseWriter(stream,default);await writer.TryFailure(new EndOfStreamException(),disconnected:true);
            check(stream.Writes==0,"feedback known client disconnect does not attempt a response");
        }
        using(var stream=new MemoryStream())
        {
            await new AgentIpc.ResponseWriter(stream,default).TryFailure(new IOException("private storage sentinel"));
            stream.Position=0;var reply=JsonSerializer.Deserialize<AgentReply>(await Wire.Read(stream,Wire.MaxJson,default),Wire.Json)!;
            check(reply.Code=="execution_failed"&&!reply.Success,"feedback business IO failure still returns its original safe error semantics");
        }
        using(var stream=new BlockingStream())
        {
            var elapsed=Stopwatch.StartNew();var writer=new AgentIpc.ResponseWriter(stream,default,TimeSpan.FromMilliseconds(50));
            await writer.TryFailure(new OperationCanceledException());
            check(stream.Writes==1&&elapsed.Elapsed<TimeSpan.FromSeconds(2),"feedback unresponsive writer bounded by independent short send budget");
            await writer.TryFailure(new OperationCanceledException());check(stream.Writes==1,"feedback timed-out error write never retried");
        }
        using(var stream=new MemoryStream())
        {
            await new AgentIpc.ResponseWriter(stream,canceled.Token).TryFailure(new OperationCanceledException());
            check(stream.Length==0,"feedback application already stopping suppresses error response");
        }
        using(var shutdown=new CancellationTokenSource())
        using(var stream=new BlockingStream())
        {
            var sending=new AgentIpc.ResponseWriter(stream,shutdown.Token).TryFailure(new InputError("test"));
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));shutdown.Cancel();await sending.WaitAsync(TimeSpan.FromSeconds(2));
            check(stream.Writes==1,"feedback shutdown interrupts an error write promptly");
        }
        string path=Path.Combine(Path.GetTempPath(),"mimo-fb-"+Guid.NewGuid().ToString("N"));
        string? previous=Environment.GetEnvironmentVariable("TRIM_APPDEST");
        try
        {
            var storage=new Storage(Path.Combine(path,"etc"),Path.Combine(path,"audio"),Path.Combine(path,"tmp"));
            var store=new AgentStore(Path.Combine(path,"agent"));var grant=store.Create("1000",new("feedback",Catalog.Scopes));
            string credentialPath=Path.Combine(path,"agent",grant.Id+".credential");
            var credential=JsonSerializer.Deserialize<AgentCredential>(File.ReadAllBytes(credentialPath),Wire.Json)!;
            var handler=new ProbeHttp();var mimo=new Mimo(new HttpClient(handler));var llm=new Llm(storage,new HttpClient(handler));
            using var lifetime=new TestLifetime();var jobs=new Jobs(storage,mimo,lifetime);
            var execution=new AgentExecution(storage,jobs,llm,new Diagnostics(storage,mimo,llm),new NasExport(storage,FnOsDirectories.Create()),store);
            AgentRequest Request(string tool)=>new(1,credential,tool,Wire.Element(new{service="preset"}));
            var cases=new Dictionary<string,AgentReply>();
            foreach(var (state,code,exit) in new[]{("succeeded","ok",0),("failed","probe_failed",6),("expired","probe_expired",6),("canceled","canceled",7)})
            {
                storage.SetKey(state=="expired"?null:"feedback-private-key");handler.Mode=state=="failed"?"failure":"success";
                int calls=handler.Calls;
                var reply=await execution.Execute(Request("probe_service"),null,state=="canceled"?canceled.Token:default);
                check(reply.Success==(state=="succeeded")&&reply.Code==code&&reply.ExitCode==exit,"feedback actual Diagnostics execution maps "+state+" to "+code+" / exit "+exit);
                check(reply.Data.GetProperty("state").GetString()==state&&reply.Data.TryGetProperty("category",out _)&&reply.Data.TryGetProperty("reason",out _)&&reply.Data.GetProperty("elapsedMs").GetInt64()>=0&&reply.Data.TryGetProperty("configuration",out _),"feedback "+state+" preserves complete safe diagnostic result");
                check(!reply.Data.GetRawText().Contains("feedback-private-key")&&!reply.Data.GetRawText().Contains("RAW_UPSTREAM_SENTINEL")&&!reply.Data.GetRawText().Contains("base64"),"feedback "+state+" does not expose credential, audio or raw upstream body");
                check(handler.Calls-calls==(state is "expired" or "canceled"?0:1),"feedback "+state+" no additional upstream request or retry");
                cases.Add(state,reply);
            }
            storage.SetKey(null);int before=handler.Calls;
            var precheck=await execution.Execute(Request("check_service_configuration"),null,default);
            check(precheck.Success&&precheck.Code=="ok"&&!precheck.Data.GetProperty("ready").GetBoolean()&&handler.Calls==before,"feedback offline precheck remains successful operation without implying readiness");
            cases.Add("check",precheck);
            if(fixtures!=null)File.WriteAllBytes(fixtures,JsonSerializer.SerializeToUtf8Bytes(cases,Wire.Json));
            if(OperatingSystem.IsLinux())
            {
                storage.SetKey("feedback-private-key");handler.Mode="delay";
                Storage.PrivateDirectory(Path.Combine(path,"target"));Environment.SetEnvironmentVariable("TRIM_APPDEST",Path.Combine(path,"target"));
                var ipc=new AgentIpc(execution,store,lifetime){OperationBudget=TimeSpan.FromMilliseconds(150)};
                await ipc.StartAsync(default);
                try
                {
                    var client=new AgentClient(ipc.PathName!,credentialPath);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    before=handler.Calls;
                    var timeout=await client.Call("probe_service",Wire.Element(new{service="preset"}),deadline.Token);
                    check(!timeout.Success&&timeout.Code=="timeout"&&timeout.ExitCode==7&&handler.Calls==before+1,"feedback real UDS operation budget expires; AgentClient receives timeout rather than service_unavailable");
                    using(var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified))
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(ipc.PathName!));
                        using var stream=new NetworkStream(socket,false);
                        await Wire.Write(stream,JsonSerializer.SerializeToUtf8Bytes(Request("probe_service"),Wire.Json),128*1024,default);
                    }
                    await Task.Delay(300);
                    check((await client.Call("get_capabilities",Wire.Element(new{}),deadline.Token)).Success,"feedback disconnected operation releases worker and service remains usable");
                    handler.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var active=client.Call("probe_service",Wire.Element(new{service="preset"}),deadline.Token);
                    await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));lifetime.StopApplication();
                    await ipc.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
                    var stopped=await active.WaitAsync(TimeSpan.FromSeconds(2));
                    check(!stopped.Success&&!File.Exists(ipc.PathName),"feedback stop during operation closes connection and removes socket promptly");
                }
                finally{await ipc.StopAsync(default);}
            }
        }
        finally{Environment.SetEnvironmentVariable("TRIM_APPDEST",previous);if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    sealed class ProbeHttp:HttpMessageHandler
    {
        public string Mode="success";public int Calls;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);Entered.TrySetResult();
            if(Mode=="delay")await Task.Delay(Timeout.Infinite,ct);
            if(Mode=="failure")return new(HttpStatusCode.Unauthorized){Content=new StringContent("RAW_UPSTREAM_SENTINEL feedback-private-key")};
            var wave=Wave.Merge([new(24000,new byte[24000])]);
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{audio=new{data=Convert.ToBase64String(wave)}}}}}))};
        }
    }
    sealed class TestLifetime:IHostApplicationLifetime,IDisposable
    {
        readonly CancellationTokenSource stop=new();public CancellationToken ApplicationStarted=>default;public CancellationToken ApplicationStopped=>default;public CancellationToken ApplicationStopping=>stop.Token;
        public void StopApplication()=>stop.Cancel();public void Dispose()=>stop.Dispose();
    }
    sealed class FailingStream(int failAt):MemoryStream
    {
        public int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default)
        {if(++Writes==failAt)throw new IOException("controlled write failure");return base.WriteAsync(buffer,ct);}
    }
    sealed class BlockingStream:MemoryStream
    {
        public int Writes;public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default){Writes++;Entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}
    }
}
