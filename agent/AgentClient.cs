using System.Net.Sockets;
using System.Text.Json;
using Studio.Agent;
namespace MiMoStudio.Agent;
public sealed class AgentClient(string socketPath,string credentialFile)
{
    AgentCredential Credential()
    {
        using var f=LinuxIdentity.OpenPrivate(credentialFile,4096);
        return JsonSerializer.Deserialize<AgentCredential>(f,Wire.Json)??throw new UnauthorizedAccessException();
    }
    public async Task<AgentReply> Call(string tool,JsonElement args,CancellationToken ct,byte[]? upload=null,Stream? download=null)
    {
        try
        {
            var credential=Credential();
            using var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
            using(var connect=CancellationTokenSource.CreateLinkedTokenSource(ct)){connect.CancelAfter(TimeSpan.FromSeconds(5));await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath),connect.Token);}
            if(LinuxIdentity.Peer(socket)!=LinuxIdentity.getuid())throw new UnauthorizedAccessException();
            using var stream=new NetworkStream(socket,false);
            byte[] request=JsonSerializer.SerializeToUtf8Bytes(new AgentRequest(Wire.Version,credential,tool,args),Wire.Json);
            await Wire.Write(stream,request,128*1024,ct);
            if(upload!=null)await Wire.Write(stream,upload,7400000,ct);
            var reply=JsonSerializer.Deserialize<AgentReply>(await Wire.Read(stream,Wire.MaxJson,ct),Wire.Json)??throw new InvalidDataException();
            if(download!=null&&reply.Success)
            {
                long left=reply.Data.GetProperty("bytes").GetInt64();if(left<44||left>64*1024*1024)throw new InvalidDataException();
                byte[] buffer=new byte[65536];
                while(left>0){int n=await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,left)),ct);if(n==0)throw new EndOfStreamException();await download.WriteAsync(buffer.AsMemory(0,n),ct);left-=n;}
            }
            return reply;
        }
        catch(UnauthorizedAccessException){return AgentReply.Fail("permission_denied");}
        catch(OperationCanceledException){return AgentReply.Fail("timeout");}
        catch(Exception e) when(e is SocketException or IOException){return AgentReply.Fail("service_unavailable");}
        catch(JsonException){return AgentReply.Fail("incompatible_service");}
    }
}
