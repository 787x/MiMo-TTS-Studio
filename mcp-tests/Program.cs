using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(90));
await using var client=await McpClient.CreateAsync(new StdioClientTransport(new(){Command=args[0],Arguments=args.Skip(1).ToArray(),Name="MiMo PR7 smoke"}),cancellationToken:budget.Token);
var tools=await client.ListToolsAsync(cancellationToken:budget.Token);
if(tools.Count<30||tools.Any(t=>t.Name is "import_sample" or "download_asset"))throw new Exception("tool discovery");
var result=await client.CallToolAsync("preview_synthesis",new Dictionary<string,object?>{["text"]="原文 é👋",["parameters"]=new{tags="温柔 清亮",mode="combined",character="旁白",style="自然"}},cancellationToken:budget.Token);
if(result.IsError==true||result.StructuredContent is not {} preview||!preview.GetProperty("success").GetBoolean())throw new Exception("structured result");
if(preview.GetProperty("data").GetProperty("prefix").GetString()!="(温柔 清亮)")throw new Exception("Composition");
var invalid=await client.CallToolAsync("preview_synthesis",new Dictionary<string,object?>{["text"]="test",["owner"]="other"},cancellationToken:budget.Token);
if(invalid.IsError!=true||invalid.StructuredContent?.GetProperty("code").GetString()!="invalid_input")throw new Exception("error result");
Console.WriteLine($"PASS official MCP SDK initialize, {tools.Count} tools, structured preview and error; no cloud");
if(Environment.GetEnvironmentVariable("MIMO_MCP_LIVE")=="1")
{
    var submitted=await client.CallToolAsync("synthesize_audio",new Dictionary<string,object?>{["text"]="你好，这是通过远程 SSH 标准 MCP 客户端生成的真实语音。",["requestId"]="mcp-"+Guid.NewGuid().ToString("N")},cancellationToken:budget.Token);
    if(submitted.IsError==true)throw new Exception("live submission failed");
    string id=submitted.StructuredContent!.Value.GetProperty("data").GetProperty("job").GetString()!;
    while(true)
    {
        var state=await client.CallToolAsync("get_job_status",new Dictionary<string,object?>{["id"]=id},cancellationToken:budget.Token);
        var job=state.StructuredContent!.Value.GetProperty("data");var status=job.GetProperty("state").GetString();
        if(status=="succeeded")
        {
            string asset=job.GetProperty("assetId").GetString()!;
            var saved=await client.CallToolAsync("save_audio_asset",new Dictionary<string,object?>{["id"]=asset},cancellationToken:budget.Token);
            if(saved.IsError==true)throw new Exception("live save failed");
            Console.WriteLine("PASS real MCP synthesis -> shared Job -> saved asset "+asset);break;
        }
        if(status is not("running" or "canceling"))throw new Exception("live job failed");
        await Task.Delay(500,budget.Token);
    }
}
