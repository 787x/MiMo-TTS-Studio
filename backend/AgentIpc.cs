using System.Net.Sockets;
using System.Text.Json;
using Studio.Agent;
namespace Studio;
// Dedicated private UDS; never routed through the Web gateway or client-provided headers.
public sealed class AgentIpc(AgentExecution execution,AgentStore grants,IHostApplicationLifetime lifetime):IHostedService
{
    Socket? listener; Task[] workers=[]; readonly CancellationTokenSource stopping=new();
    internal TimeSpan OperationBudget { get; init; } = TimeSpan.FromMinutes(4);
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
                using var shutdown=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token,lifetime.ApplicationStopping);
                var response=new ResponseWriter(stream,shutdown.Token);
                bool reading=true;
                try
                {
                    var bytes=await Wire.Read(stream,128*1024,budget.Token);
                    var request=JsonSerializer.Deserialize<AgentRequest>(bytes,Wire.Json)??throw new ArgumentException();
                    if(request.Version!=Wire.Version){await response.Send(AgentReply.Fail("incompatible_service"),budget.Token);continue;}
                    var spec=Catalog.Find(request.Tool);Catalog.Validate(request.Arguments,spec.Schema);
                    grants.Authorized(request.Credential,spec.Scope,_=>true);
                    budget.CancelAfter(OperationBudget);
                    if(request.Tool=="download_asset")
                    {
                        reading=false;
                        using var file=execution.Download(request);if(file.Length>Wave.MaxBytes)throw new InputError("资源过大。");
                        await response.Send(AgentReply.Ok(new{bytes=file.Length}),budget.Token);await file.CopyToAsync(stream,65536,budget.Token);continue;
                    }
                    byte[]? upload=request.Tool=="import_sample"?await Wire.Read(stream,SampleAudio.MaxBytes,budget.Token):null;
                    reading=false;
                    await response.Send(await execution.Execute(request,upload,budget.Token),budget.Token);
                }
                catch(Exception e)
                {
                    var error=budget.IsCancellationRequested?new OperationCanceledException(budget.Token):e;
                    await response.TryFailure(error,reading&&(e is SocketException or IOException));
                }
            }
        }
    }
    // Once a write has been attempted its frame may be partial: never append another reply.
    internal sealed class ResponseWriter(Stream stream,CancellationToken shutdown,TimeSpan? errorBudget=null)
    {
        bool started;
        public async Task Send(AgentReply reply,CancellationToken ct)
        {
            byte[] data=JsonSerializer.SerializeToUtf8Bytes(reply,Wire.Json);
            if(data.Length>Wire.MaxJson)data=JsonSerializer.SerializeToUtf8Bytes(AgentReply.Fail("response_too_large"),Wire.Json);
            ct.ThrowIfCancellationRequested();
            shutdown.ThrowIfCancellationRequested();
            if(started)throw new InvalidOperationException("Response already started.");
            started=true;
            await Wire.Write(stream,data,Wire.MaxJson,ct);
        }
        public async Task TryFailure(Exception error,bool disconnected=false)
        {
            if(started||disconnected||shutdown.IsCancellationRequested)return;
            // Only delivery gets a fresh budget. The business operation is never resumed or retried.
            using var sending=CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            sending.CancelAfter(errorBudget??TimeSpan.FromSeconds(2));
            try{await Send(Failure(error),sending.Token);}catch(Exception){}
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
