using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Studio.Agent;
namespace Studio;
public sealed record AgentGrantView(string Id, string Name, string Owner, string[] Scopes, DateTimeOffset Created);
public sealed record AgentGrant(AgentGrantView View, string Hash, JsonElement Defaults, Dictionary<string,JsonElement> Presets);
public sealed record AgentGrantInput(string Name, string[] Scopes);
public sealed class AgentStore
{
    readonly object gate=new(); readonly string directory, path;
    readonly Dictionary<string,AgentGrant> grants;
    public AgentStore(string directory)
    {
        this.directory=directory; Storage.PrivateDirectory(directory); path=Path.Combine(directory,"grants.json");
        if(File.Exists(path)&&new FileInfo(path).Length>256*1024)throw new InvalidDataException();
        grants=File.Exists(path)?JsonSerializer.Deserialize<Dictionary<string,AgentGrant>>(File.ReadAllBytes(path),Wire.Json)??throw new InvalidDataException():[];
        if(grants.Count>64)throw new InvalidDataException();
        if(OperatingSystem.IsLinux())foreach(var file in Directory.GetFiles(directory))File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    void Write(string file, byte[] bytes)
    {
        if(bytes.Length>256*1024)throw new InputError("Agent 配置超过上限。");
        string temp=file+".new";
        var options=new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,Share=FileShare.None};
        if(OperatingSystem.IsLinux())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        try { using(var f=new FileStream(temp,options)){f.Write(bytes);f.Flush(true);}File.Move(temp,file,true); }
        finally{File.Delete(temp);}
    }
    void Save(Dictionary<string,AgentGrant> next) { Write(path,JsonSerializer.SerializeToUtf8Bytes(next,Wire.Json));grants.Clear();foreach(var p in next)grants.Add(p.Key,p.Value); }
    public AgentGrantView[] List(string owner){lock(gate)return grants.Values.Where(g=>g.View.Owner==owner).Select(g=>g.View).ToArray();}
    public AgentGrantView Create(string owner, AgentGrantInput input)
    {
        string name=Storage.SampleName(input.Name);
        if(input.Scopes==null || input.Scopes.Length==0 || input.Scopes.Any(s=>!Catalog.Scopes.Contains(s)) || !input.Scopes.Contains("read"))throw new InputError("请选择合法权限并包含 read。");
        lock(gate)
        {
            if(grants.Count>=64)throw new InputError("最多 64 项授权。");
            string id=Guid.NewGuid().ToString("N"),secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var view=new AgentGrantView(id,name,owner,input.Scopes.Distinct().ToArray(),DateTimeOffset.UtcNow);
            var grant=new AgentGrant(view,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),Wire.Element(new{}),[]);
            var next=new Dictionary<string,AgentGrant>(grants){[id]=grant};
            // Secret is only placed in an application-private credential file, never a Web response.
            Write(Path.Combine(directory,id+".credential"),JsonSerializer.SerializeToUtf8Bytes(new AgentCredential(id,secret),Wire.Json));
            try{Save(next);}catch{File.Delete(Path.Combine(directory,id+".credential"));throw;}
            return view;
        }
    }
    public bool Revoke(string owner,string id)
    {
        lock(gate){if(!grants.TryGetValue(id,out var g)||g.View.Owner!=owner)return false;
            var next=new Dictionary<string,AgentGrant>(grants);next.Remove(id);Save(next);File.Delete(Path.Combine(directory,id+".credential"));return true;}
    }
    public AgentGrant Verify(AgentCredential credential)
    {
        lock(gate)
        {
            if(credential==null || credential.Id==null || credential.Secret==null || credential.Secret.Length!=64 || !grants.TryGetValue(credential.Id,out var grant) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(credential.Secret)),Convert.FromHexString(grant.Hash)))throw new UnauthorizedAccessException();
            return grant;
        }
    }
    // Permission verification and local commit/Job submission share the revocation lock.
    public T Authorized<T>(AgentCredential credential,string scope,Func<AgentGrant,T> action)
    {lock(gate){var g=Verify(credential);if(!g.View.Scopes.Contains(scope))throw new UnauthorizedAccessException();return action(g);}}
    public object Configure(AgentCredential credential,string operation,JsonElement args)
    {
        return Authorized(credential,operation=="delete_preset"?"delete":"configure",g=>
        {
            var presets=new Dictionary<string,JsonElement>(g.Presets);var defaults=g.Defaults;
            if(operation=="set_agent_defaults")
            {
                var node=args.TryGetProperty("clear",out var clear)&&clear.GetBoolean()?new JsonObject():JsonNode.Parse(defaults.GetRawText())!.AsObject();
                if(args.TryGetProperty("parameters",out var fields))foreach(var field in fields.EnumerateObject())node[field.Name]=JsonNode.Parse(field.Value.GetRawText());
                defaults=Wire.Element(node); ValidateFields(defaults);
            }
            else
            {
                string name=PresetName(args.GetProperty("name").GetString());
                if(operation=="delete_preset")presets.Remove(name);
                else{var p=args.GetProperty("parameters");ValidateFields(p);if(presets.Count>=100&&!presets.ContainsKey(name))throw new InputError("预设最多100项。");presets[name]=p.Clone();}
            }
            var next=new Dictionary<string,AgentGrant>(grants){[g.View.Id]=g with{Defaults=defaults,Presets=presets}};Save(next);return new{updated=true};
        });
    }
    public static string PresetName(string? name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>64||name.Any(c=>!char.IsLetterOrDigit(c)&&c!=' '&&c!='_'&&c!='-'))throw new InputError("预设名仅允许1–64位字母、数字、空格、下划线或连字符。");return name;
    }
    public static void ValidateFields(JsonElement fields)
    {
        Catalog.Validate(fields,Catalog.Parameters);
        // Partial presets are allowed; every stored field still receives value validation.
        var r=ResolveFields([fields],"试音");
        if(r.Style.Length>6000||r.VoiceDescription.Length>6000||!Composition.Voices.Contains(r.Voice)||r.Mode is not("natural" or "director" or "combined"))throw new InputError("无效预设。");
        _=Composition.Model(r.SynthesisMode);Composition.ValidateTags(r.Tags);
        if(r.SourceSampleId!=null&&!Storage.ValidId(r.SourceSampleId))throw new InputError("样本ID无效。");
    }
    public static ScriptRequest Resolve(AgentGrant grant,JsonElement args)
    {
        var layers=new List<JsonElement>{grant.Defaults};
        if(args.TryGetProperty("preset",out var preset)&&preset.ValueKind!=JsonValueKind.Null)
        {string name=PresetName(preset.GetString());if(!grant.Presets.TryGetValue(name,out var p))throw new InputError("预设不存在。");layers.Add(p);}
        if(args.TryGetProperty("parameters",out var explicitFields))layers.Add(explicitFields);
        return ResolveFields(layers,args.TryGetProperty("text",out var text)?text.GetString()??"":"");
    }
    static ScriptRequest ResolveFields(IEnumerable<JsonElement> layers,string text)
    {
        var node=JsonSerializer.SerializeToNode(new ScriptRequest(text),Wire.Json)!.AsObject();
        var safe=JsonSerializer.SerializeToNode(new ScriptRequest(text),Wire.Json)!.AsObject();
        foreach(var layer in layers){Catalog.Validate(layer,Catalog.Parameters);foreach(var p in layer.EnumerateObject())node[p.Name]=p.Value.ValueKind==JsonValueKind.Null?safe[p.Name]?.DeepClone():JsonNode.Parse(p.Value.GetRawText());}
        return node.Deserialize<ScriptRequest>(Wire.Json)!;
    }
}
