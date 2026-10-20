using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Studio;

static class Pr5Tests
{
    public static async Task Run(Action<bool,string> check)
    {
        foreach (var name in new[] { "../escape", "/etc/test", "a/b", "a\\b", "a\n", ".hidden", "a..wav", " a", "a:", "a\u202ewav" })
        {
            try { NasExport.FileName(name); throw new Exception("unsafe name accepted"); }
            catch (InputError) { check(true, "export rejects filename " + JsonSerializer.Serialize(name)); }
        }
        check(NasExport.FileName("声音.WAV") == "声音.wav" && NasExport.FileName("声音") == "声音.wav", "export normalizes final WAV extension");
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MIMO_EXPORT_TEST_ROOT") is not { } root)
        { Console.WriteLine("SKIP Linux export filesystem checks: set MIMO_EXPORT_TEST_ROOT to an owned /volNNN/test directory"); return; }
        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "private"), target = Path.Combine(root, "target"), outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(target); Directory.CreateDirectory(outside);
        var storage = new Storage(Path.Combine(data, "etc"), Path.Combine(data, "audio"), Path.Combine(data, "tmp"));
        byte[] bytes = Wave.Merge([new(24000, new byte[48000])]);
        var asset = storage.Commit("1000", new("private text must never become a filename"), bytes);
        var handler = new PlatformHandler { Paths = [target] };
        var exports = new NasExport(storage, new(new HttpClient(handler), () => "test-platform-token"));
        async Task Deny(Func<Task> operation, string message)
        {
            try { await operation(); throw new Exception("FAIL " + message); }
            catch (InputError) { check(true, message); }
        }
        var targets = await exports.Targets("1000", default);
        check(targets.Length == 1 && targets[0].Writable && targets[0].Id != target, "live platform directory becomes opaque writable ID");
        var input = new ExportInput(targets[0].Id, "first.wav");
        var receipt = await exports.Copy(asset.Id, "1000", input, default);
        check(receipt.Bytes == bytes.Length && File.ReadAllBytes(Path.Combine(target, "first.wav")).SequenceEqual(bytes), "bounded export target bytes match source hash");
        check(!storage.Get(asset.Id, "1000")!.Saved && File.ReadAllBytes(Path.Combine(storage.Data, asset.Id, "audio.wav")).SequenceEqual(bytes), "export leaves source bytes and Saved unchanged");
        File.SetUnixFileMode(root,UnixFileMode.UserExecute);
        await exports.Copy(asset.Id,"1000",input with{FileName="traverse.wav"},default);
        check(File.Exists(Path.Combine(target,"traverse.wav")),"traverse-only ancestors work without broadening parent permissions");
        File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        await Deny(() => exports.Copy(asset.Id, "1000", input, default), "existing file is never replaced");
        await Deny(() => exports.Copy(asset.Id, "2000", input with { FileName = "other.wav" }, default), "another user's asset is denied");
        await Deny(() => exports.Copy(asset.Id, "1000", input with { DirectoryId = target }, default), "absolute directory supplied as ID is denied");
        handler.Paths = [outside];
        await Deny(() => exports.Copy(asset.Id, "1000", input, default), "revoked cached directory ID denied");
        handler.Paths=[target+"-missing"];
        var missing=(await exports.Targets("1000",default))[0];
        check(!missing.Writable,"missing authorized directory disabled");
        await Deny(()=>exports.Copy(asset.Id,"1000",new(missing.Id,"missing.wav"),default),"missing authorized directory fails closed");
        handler.Paths = [target]; handler.Write = false;
        check(!(await exports.Targets("1000", default))[0].Writable, "platform read-only target disabled");
        await Deny(() => exports.Copy(asset.Id, "1000", input, default), "platform user ACL read-only denied");
        handler.Write = true;
        File.SetUnixFileMode(target,UnixFileMode.UserRead|UnixFileMode.UserExecute);
        check(!(await exports.Targets("1000",default))[0].Writable,"actual process write permission required despite platform ACL approval");
        await Deny(()=>exports.Copy(asset.Id,"1000",input with{FileName="readonly.wav"},default),"actual read-only directory refuses export");
        File.SetUnixFileMode(target,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(target,"link.wav"),Path.Combine(outside,"victim"));
        await Deny(() => exports.Copy(asset.Id,"1000",input with {FileName="link.wav"},default), "existing file symlink is not followed or replaced");
        check(!File.Exists(Path.Combine(outside,"victim")), "file symlink cannot escape target");
        Directory.Move(target,target+"-old");Directory.CreateSymbolicLink(target,outside);
        await Deny(() => exports.Copy(asset.Id,"1000",input with {FileName="escape.wav"},default), "directory symlink rejected");
        Directory.Delete(target);Directory.Move(target+"-old",target);
        handler.OnAcl = count => { if(count==2) handler.Paths=[]; }; handler.AclCalls=0;
        await Deny(() => exports.Copy(asset.Id,"1000",input with {FileName="revoked.wav"},default), "revocation during copy prevents publication");
        // Revocation after the query's snapshot is caught by the next live query.
        handler.OnAcl=null; handler.Paths=[target];
        handler.OnPaths=count=>{if(count==3)handler.Paths=[];};handler.PathCalls=0;
        await Deny(() => exports.Copy(asset.Id,"1000",input with {FileName="revoked2.wav"},default), "authorization revoked at commit query denied");
        handler.OnPaths=null;handler.Paths=[target];handler.AclCalls=0;
        handler.OnAcl=count=>{if(count==2){Directory.Move(target,target+"-old");Directory.CreateDirectory(target);}};
        await Deny(() => exports.Copy(asset.Id,"1000",input with {FileName="race.wav"},default), "directory inode replacement at commit detected");
        check(!Directory.EnumerateFiles(target).Any()&&!File.Exists(Path.Combine(target+"-old","race.wav")), "replacement neither receives output nor commits anonymous partial");
        Directory.Delete(target);Directory.Move(target+"-old",target);handler.OnAcl=null;
        handler.AclCalls=0;using var midCancel=new CancellationTokenSource();
        handler.OnAcl=count=>{if(count==2)midCancel.Cancel();};
        await Deny(()=>exports.Copy(asset.Id,"1000",input with{FileName="mid-cancel.wav"},midCancel.Token),"cancellation before publication discards fully copied anonymous file");
        check(!File.Exists(Path.Combine(target,"mid-cancel.wav")),"cancellation cannot leave a success-looking WAV");
        handler.OnAcl=null;
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        try{await exports.Copy(asset.Id,"1000",input with {FileName="cancel.wav"},canceled.Token);throw new Exception("canceled accepted");}catch(OperationCanceledException){check(true,"canceled request cannot create output");}
        // Several independent instances emulate separate processes racing to publish.
        var copies=Enumerable.Range(0,8).Select(async _=>{
            var instance=new NasExport(storage,new(new HttpClient(handler),()=>"test-platform-token"));
            var id=(await instance.Targets("1000",default))[0].Id;
            try{await instance.Copy(asset.Id,"1000",new(id,"concurrent.wav"),default);return true;}catch(InputError){return false;}
        });
        check((await Task.WhenAll(copies)).Count(x=>x)==1,"concurrent independent exporters commit exactly one same-name WAV");
        check(Directory.GetFiles(target).All(p=>!Path.GetFileName(p).Contains("partial")) && !File.Exists(Path.Combine(target,"revoked2.wav")), "failed exports leave no named temporary or partial WAV");
        handler.Paths=[target+"-adjacent",storage.Config,"/etc", "/vol1/@appconf/mimotts", root];
        Directory.CreateDirectory(target+"-adjacent");
        var adjacent=await exports.Targets("1000",default);
        check(adjacent.Length==1 && adjacent[0].Path==target+"-adjacent", "exact roots only: private ancestors and system paths rejected");
        check(handler.Requests.All(r=>r.GetProperty("appName").GetString()=="mimotts") && handler.Owners.All(uid=>uid==1000), "platform protocol uses app identity and trusted current UID");
        if(Environment.GetEnvironmentVariable("MIMO_EXPORT_CROSS_ROOT") is {} cross)
        {
            handler.Paths=[cross];var id=(await exports.Targets("1000",default))[0].Id;
            await exports.Copy(asset.Id,"1000",new(id,"cross.wav"),default);
            check(File.ReadAllBytes(Path.Combine(cross,"cross.wav")).SequenceEqual(bytes),"different-filesystem source and target copy hashes match");
        }
        if(Environment.GetEnvironmentVariable("MIMO_EXPORT_FULL_ROOT") is {} full)
        {
            var large=storage.Commit("1000",new("full disk test"),Wave.Merge([new(24000,new byte[2*1024*1024])]));
            handler.Paths=[full];var id=(await exports.Targets("1000",default))[0].Id;
            await Deny(()=>exports.Copy(large.Id,"1000",new(id,"full.wav"),default),"real ENOSPC fails without publication");
            check(!Directory.EnumerateFileSystemEntries(full).Any(),"real ENOSPC releases anonymous temporary with zero named remnants");
        }
    }
    private sealed class PlatformHandler:HttpMessageHandler
    {
        public string[] Paths=[];public bool Write=true;public int PathCalls,AclCalls;
        public Action<int>? OnPaths,OnAcl;public List<JsonElement> Requests=[];public List<int> Owners=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var json=body.RootElement.Clone();Requests.Add(json);
            if(request.Headers.Authorization?.Parameter!="test-platform-token")throw new Exception("platform credential missing");
            object data;
            if(json.GetProperty("req").GetString()=="trim.file.getSharedAccessibleFolders") {OnPaths?.Invoke(++PathCalls);data=new{paths=Paths};}
            else {OnAcl?.Invoke(++AclCalls);Owners.Add(json.GetProperty("data").GetProperty("uid").GetInt32());string path=json.GetProperty("data").GetProperty("path").GetString()!;data=new[]{new{path,writable=Write}};}
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{code=0,data})};
        }
    }
}
