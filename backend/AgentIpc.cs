using System.Net.Sockets;
using System.Text.Json;
using Studio.Agent;
namespace Studio;
// Dedicated private UDS; never routed through the Web gateway or client-provided headers.
public sealed class AgentIpc(AgentExecution execution,AgentStore grants,IHostApplicationLifetime lifetime):IHostedService
{
    Socket? listener; Task[] workers=[]; readonly CancellationTokenSource stopping=new();
    public string? PathName {get;private set;}
    public Task StartAsync(CancellationToken ct)
    {
        if(!OperatingSystem.IsLinux())return Task.CompletedTask;
        string? dest=Environment.GetEnvironmentVariable("TRIM_APPDEST");if(string.IsNullOrEmpty(dest))return Task.CompletedTask;
        PathName=Path.Combine(dest,"agent.sock");File.Delete(PathName);
        listener=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);listener.Bind(new UnixDomainSocketEndPoint(PathName));
        File.SetUnixFileMode(PathName,UnixFileMode.UserRead|UnixFileMode.UserWrite);listener.Listen(4);
        workers=Enumerable.Range(0,4).Select(_=>Task.Run(Worker)).ToArray();return Task.CompletedTask;
    }
    async Task Worker()
    {
        while(!stopping.IsCancellationRequested)
        {
            Socket socket;
            try{socket=await listener!.AcceptAsync(stopping.Token);}catch(OperationCanceledException){break;}catch(ObjectDisposedException){break;}
            using(socket)
            {
                if(LinuxIdentity.Peer(socket)!=LinuxIdentity.getuid())continue;
                using var stream=new NetworkStream(socket,false);
                using var budget=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token,lifetime.ApplicationStopping);budget.CancelAfter(TimeSpan.FromSeconds(5));
                bool replied=false;
                try
                {
                    var bytes=await Wire.Read(stream,128*1024,budget.Token);
                    var request=JsonSerializer.Deserialize<AgentRequest>(bytes,Wire.Json)??throw new ArgumentException();
                    if(request.Version!=Wire.Version){await Reply(AgentReply.Fail("incompatible_service"));continue;}
                    var spec=Catalog.Find(request.Tool);Catalog.Validate(request.Arguments,spec.Schema);
                    grants.Authorized(request.Credential,spec.Scope,_=>true);
                    budget.CancelAfter(TimeSpan.FromMinutes(4));
                    if(request.Tool=="download_asset")
                    {
                        using var file=execution.Download(request);if(file.Length>Wave.MaxBytes)throw new InputError("资源过大。");
                        await Reply(AgentReply.Ok(new{bytes=file.Length}));await file.CopyToAsync(stream,65536,budget.Token);continue;
                    }
                    byte[]? upload=request.Tool=="import_sample"?await Wire.Read(stream,SampleAudio.MaxBytes,budget.Token):null;
                    await Reply(await execution.Execute(request,upload,budget.Token));
                }
                catch(Exception e)
                {
                    if(!replied)try{await Reply(Failure(e));}catch(Exception){}
                }
                async Task Reply(AgentReply reply)
                {
                    byte[] data=JsonSerializer.SerializeToUtf8Bytes(reply,Wire.Json);
                    if(data.Length>Wire.MaxJson)data=JsonSerializer.SerializeToUtf8Bytes(AgentReply.Fail("response_too_large"),Wire.Json);
                    await Wire.Write(stream,data,Wire.MaxJson,budget.Token);replied=true;
                }
            }
        }
    }
    public static AgentReply Failure(Exception e)=>AgentReply.Fail(e switch
    {
        UnauthorizedAccessException=>"permission_denied",BusyError=>"busy",AgentConflict=>"idempotency_conflict",
        OperationCanceledException=>"timeout",InputError input when input.Message.StartsWith("WAV 已完整提交",StringComparison.Ordinal)=>"export_committed_check_target",
        InputError or JsonException or ArgumentException or InvalidDataException=>"invalid_input",_=>"execution_failed"
    });
    public async Task StopAsync(CancellationToken ct){stopping.Cancel();listener?.Dispose();try{await Task.WhenAll(workers).WaitAsync(ct);}finally{if(PathName!=null)File.Delete(PathName);}}
}
