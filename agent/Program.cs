using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using MiMoStudio.Agent;
using Studio.Agent;
using var canceled=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;canceled.Cancel();};
void Output(AgentReply reply){var json=JsonSerializer.Serialize(reply,Wire.Json);if(args.Length>0&&args[0]=="mcp")Console.Error.WriteLine(json);else Console.WriteLine(json);}
try
{
    var options=await Cli.Parse(args,canceled.Token);
    if(options.Help){Console.WriteLine(options.Tool==""||options.Tool=="mcp"?Cli.Help:Catalog.Find(options.Tool).Schema.GetRawText());return 0;}
    var client=new AgentClient(options.Socket,options.Credential);
    using var slots=new SemaphoreSlim(4,4);
    if(options.Tool=="mcp")
    {
        var builder=Host.CreateApplicationBuilder();builder.Logging.ClearProviders();
        builder.Services.AddMcpServer(o=>o.ServerInfo=new(){Name="MiMoStudio.Agent",Version="0.7.1"})
            .WithStreamServerTransport(new BoundedInput(Console.OpenStandardInput()),Console.OpenStandardOutput())
            .WithListToolsHandler(async (_,ct)=>
            {
                var capabilities=await client.Call("get_capabilities",Wire.Element(new{}),ct);
                var allowed=capabilities.Success?capabilities.Data.GetProperty("tools").EnumerateArray().Select(t=>t.GetString()).ToHashSet():[];
                return new ListToolsResult{Tools=Catalog.Tools.Where(t=>!t.CliOnly&&allowed.Contains(t.Name)).Select(t=>new Tool{Name=t.Name,Description=t.Description,InputSchema=t.Schema,Annotations=new(){ReadOnlyHint=t.ReadOnly,DestructiveHint=t.Destructive,IdempotentHint=t.ReadOnly,OpenWorldHint=t.Scope=="generate"}}).ToList()};
            })
            .WithCallToolHandler(async (request,ct)=>
            {
                AgentReply reply;
                bool entered=false;
                try
                {
                    entered=await slots.WaitAsync(0,ct);
                    if(!entered)throw new InvalidOperationException("busy");
                }
                catch{ return new CallToolResult{IsError=true,StructuredContent=Wire.Element(AgentReply.Fail("busy")),Content=[new TextContentBlock{Text="{\"success\":false,\"code\":\"busy\",\"data\":{}}"}]};}
                try
                {
                    string name=request.Params?.Name??"";var spec=Catalog.Find(name);if(spec.CliOnly)throw new ArgumentException();
                    var a=Wire.Element(request.Params?.Arguments??new Dictionary<string,JsonElement>());Catalog.Validate(a,spec.Schema);
                    using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromMinutes(4));
                    reply=await client.Call(name,a,budget.Token);
                }
                catch(OperationCanceledException){reply=AgentReply.Fail("canceled");}catch{reply=AgentReply.Fail("invalid_input");}
                finally{if(entered)slots.Release();}
                var json=Wire.Element(reply);
                return new CallToolResult{IsError=!reply.Success,StructuredContent=json,Content=[new TextContentBlock{Text=json.GetRawText()}]};
            });
        await builder.Build().RunAsync(canceled.Token);return 0;
    }
    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(canceled.Token);timeout.CancelAfter(TimeSpan.FromSeconds(options.Timeout));
    AgentReply result;
    if(options.Input!=null)
    {
        using var file=LinuxIdentity.OpenPrivate(options.Input,7400000);using var content=new MemoryStream();
        byte[] block=new byte[65536];int read;while((read=await file.ReadAsync(block,timeout.Token))>0){if(content.Length+read>7400000)throw new ArgumentException();content.Write(block,0,read);}
        if(content.Length>7400000)throw new ArgumentException();result=await client.Call(options.Tool,options.Arguments,timeout.Token,content.ToArray());
    }
    else if(options.Output!=null)
    {
        var path=Path.GetFullPath(options.Output);var temp=path+"."+Guid.NewGuid().ToString("N")+".partial";
        var create=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
        if(OperatingSystem.IsLinux())create.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        try
        {
            using(var file=new FileStream(temp,create)){result=await client.Call(options.Tool,options.Arguments,timeout.Token,download:file);if(result.Success)file.Flush(true);}
            if(result.Success)File.Move(temp,path,false);
        }
        finally{File.Delete(temp);}
    }
    else result=await client.Call(options.Tool,options.Arguments,timeout.Token);
    if(options.Wait&&result.Success)
    {
        string id=options.Tool=="synthesize_audio"?result.Data.GetProperty("job").GetString()!:options.Arguments.GetProperty("id").GetString()!;
        while(true)
        {
            result=await client.Call("get_job_status",Wire.Element(new{id}),timeout.Token);if(!result.Success)break;
            string state=result.Data.GetProperty("state").GetString()!;
            if(state is not("running" or "canceling")){if(state!="succeeded")result=result with{Success=false,Code=state=="canceled"?"canceled":"execution_failed"};break;}
            await Task.Delay(500,timeout.Token);
        }
    }
    Output(result);return result.ExitCode;
}
catch(OperationCanceledException){Output(AgentReply.Fail(canceled.IsCancellationRequested?"canceled":"timeout"));return 7;}
catch(UnauthorizedAccessException){Output(AgentReply.Fail("permission_denied"));return 3;}
catch(Exception e) when(e is ArgumentException or JsonException or FormatException or OverflowException){Output(AgentReply.Fail("invalid_input"));return 2;}
catch{Output(AgentReply.Fail("execution_failed"));return 6;}
