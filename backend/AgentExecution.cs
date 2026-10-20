using System.Security.Cryptography;
using System.Text.Json;
using Studio.Agent;
namespace Studio;
public sealed class AgentExecution(Storage storage,Jobs jobs,Llm llm,Diagnostics diagnostics,NasExport exports,AgentStore store)
{
    readonly Dictionary<string,(string Hash,string Job)> submissions=[];
    static object Job(JobView j)=>new{j.Id,j.State,j.Completed,j.Total,j.AssetId,error=j.Error==null?null:"generation_failed",j.SynthesisMode};
    static object AssetView(Asset a)=>new{a.Id,a.Created,a.Saved,a.Bytes,a.SynthesisMode,a.DurationSeconds,a.SampleEligible,a.SourceSampleId,format="wav",channels=1,bits=16};
    static object SampleView(VoiceSample s)=>new{s.Id,s.Name,s.Source,s.Format,s.Mime,s.Bytes,s.DurationSeconds,s.SampleRate,s.Channels,s.Created,s.SourceAssetId};
    static T Found<T>(T? item) where T:class=>item??throw new InputError("资源不存在或无权访问。");
    static string Str(JsonElement a,string name)=>a.GetProperty(name).GetString()??throw new ArgumentException();
    static object Page<T>(T[] values,JsonElement a,Func<T,object> view)
    {int offset=a.TryGetProperty("offset",out var p)?p.GetInt32():0;var items=values.Skip(offset).Take(100).Select(view).ToArray();return new{items,nextOffset=offset+items.Length<values.Length?(int?)(offset+items.Length):null};}
    public async Task<AgentReply> Execute(AgentRequest request,byte[]? upload,CancellationToken ct)
    {
        if(request.Version!=Wire.Version)return AgentReply.Fail("incompatible_service");
        var spec=Catalog.Find(request.Tool);Catalog.Validate(request.Arguments,spec.Schema);var a=request.Arguments;
        var pending=store.Authorized(request.Credential,spec.Scope,g=>
        {
            string owner=g.View.Owner;string Id()=>Str(a,"id");object result;
            // All synchronous side effects and submission admission occur under the grant/revocation lock.
            switch(request.Tool)
            {
                case "get_capabilities":result=new{version=Wire.Version,scopes=g.View.Scopes,tools=Catalog.Tools.Where(t=>g.View.Scopes.Contains(t.Scope)).Select(t=>t.Name),maxTextUtf8=60000,maxJson=Wire.MaxJson,concurrency=1,jobMinutes=20,idempotency="TTS only; 256 records until backend restart; never automatic retry"};break;
                case "list_voices":result=Composition.Voices;break;
                case "list_style_presets":result=StyleCatalog.Categories;break;
                case "list_inline_tags":result=Creative.Labels;break;
                case "get_service_status":var(s,k)=storage.ReadLlm();result=new{ttsConfigured=storage.ReadKey()!=null,llmConfigured=s.ReuseTtsKey?storage.ReadKey()!=null:k!=null,s.Model,s.Thinking,s.Streaming};break;
                case "get_agent_defaults":result=g.Defaults;break;
                case "list_presets":result=g.Presets.Keys.Order().ToArray();break;
                case "get_preset":result=g.Presets.TryGetValue(AgentStore.PresetName(Str(a,"name")),out var preset)?preset:throw new InputError("预设不存在。");break;
                case "set_agent_defaults":case "save_preset":case "delete_preset":result=store.Configure(request.Credential,request.Tool,a);break;
                case "preview_synthesis":
                    if(a.TryGetProperty("requestId",out _))throw new ArgumentException();
                    var script=AgentStore.Resolve(g,a);var plan=Composition.Plan(script);
                    if(script.SynthesisMode=="clone")Found(storage.GetSample(script.SourceSampleId!,owner));
                    result=new{model=Composition.Model(script.SynthesisMode),script.SynthesisMode,plan.User,plan.Prefix,plan.Segments,plan.Assistant,parameters=script with{Text=""}};break;
                case "synthesize_audio":
                    var r=AgentStore.Resolve(g,a);_ = Composition.Plan(r);
                    string? key=null,hash=null;
                    if(a.TryGetProperty("requestId",out var requestId))
                    {
                        string id=requestId.GetString()??throw new ArgumentException();
                        if(id.Length is <1 or >80 || id.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'&&c!='_'))throw new ArgumentException();
                        key=g.View.Id+"/"+id;hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(r,Wire.Json)));
                        if(submissions.TryGetValue(key,out var old))
                        {if(old.Hash!=hash)throw new AgentConflict();result=new{job=old.Job,replayed=true};break;}
                        if(submissions.Count>=256)throw new BusyError();
                    }
                    ct.ThrowIfCancellationRequested();var job=jobs.Start(owner,r);if(key!=null)submissions.Add(key,(hash!,job.Id));result=new{job=job.Id,replayed=false};break;
                case "list_jobs":result=Page(jobs.List(owner),a,Job);break;
                case "get_job_status":result=Job(Found(jobs.Get(Id(),owner)));break;
                case "cancel_job":result=Job(Found(jobs.Cancel(Id(),owner)));break;
                case "list_audio_assets":result=Page(storage.List(owner),a,AssetView);break;
                case "get_audio_asset":
                    var asset=Found(storage.Get(Id(),owner));using(var f=Found(storage.Open(Id(),owner))){byte[] header=new byte[44];f.ReadExactly(header);result=new{asset=AssetView(asset),sampleRate=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24,4))};}break;
                case "save_audio_asset":result=AssetView(Found(storage.Save(Id(),owner)));break;
                case "delete_audio_asset":if(!storage.Delete(Id(),owner))throw new InputError("资源不存在。");result=new{deleted=true};break;
                case "list_voice_samples":result=Page(storage.ListSamples(owner),a,SampleView);break;
                case "get_voice_sample":result=SampleView(Found(storage.GetSample(Id(),owner)));break;
                case "create_sample_from_asset":result=SampleView(Found(storage.SampleFromAsset(Id(),owner,Str(a,"name"))));break;
                case "rename_voice_sample":result=SampleView(Found(storage.RenameSample(Id(),owner,Str(a,"name"))));break;
                case "delete_voice_sample":if(!storage.DeleteSample(Id(),owner))throw new InputError("资源不存在。");result=new{deleted=true};break;
                case "import_sample":result=SampleView(storage.CreateSample(owner,Str(a,"name"),Str(a,"format"),upload??throw new ArgumentException(),ct:ct));break;
                case "check_service_configuration":result=diagnostics.Check(Diagnostic(a),owner);break;
                default:return ExecuteAsync(request,g,ct);
            }
            return Task.FromResult(AgentReply.Ok(result));
        });
        return await pending;
    }
    static DiagnosticInput Diagnostic(JsonElement a)=>new(Str(a,"service"),a.TryGetProperty("sampleId",out var sample)?sample.GetString():null);
    async Task<AgentReply> ExecuteAsync(AgentRequest request,AgentGrant grant,CancellationToken ct)
    {
        var a=request.Arguments;string owner=grant.View.Owner;
        switch(request.Tool)
        {
            case "list_nas_export_targets":return AgentReply.Ok((await exports.Targets(owner,ct)).Select(t=>new{t.Id,t.Writable,t.Reason}));
            case "export_audio_to_nas":
                var receipt=await exports.Copy(Str(a,"id"),owner,new(Str(a,"directoryId"),Str(a,"fileName")),ct);
                return AgentReply.Ok(new{receipt.FileName,receipt.Bytes});
            case "probe_service":var input=Diagnostic(a);var check=diagnostics.Check(input,owner);return AgentReply.Ok(await diagnostics.Run(input with{Revision=check.Revision,Id=Guid.NewGuid().ToString("N")},owner,ct));
            default:
                if(a.TryGetProperty("requestId",out _))throw new ArgumentException();
                string task=request.Tool switch{"generate_voice_description"=>"description","generate_style_instruction"=>"style","generate_director_plan"=>"director","generate_script"=>"script","generate_lyrics"=>"lyrics","annotate_inline_tags"=>"tags",_=>throw new ArgumentException()};
                return AgentReply.Ok(await llm.Generate(new(task,AgentStore.Resolve(grant,a)),ct));
        }
    }
    public FileStream Download(AgentRequest request)=>store.Authorized(request.Credential,"read",g=>Found(storage.Open(Str(request.Arguments,"id"),g.View.Owner)));
}
public sealed class AgentConflict:Exception;
public static class StyleCatalog
{
    public static readonly object[] Categories=
    [
        new{name="基础情绪",tags=new[]{"开心","悲伤","愤怒","恐惧","惊讶","兴奋","委屈","平静","冷漠"}},
        new{name="复合情绪",tags=new[]{"怅然","欣慰","无奈","愧疚","释然","嫉妒","厌倦","忐忑","动情"}},
        new{name="整体语调",tags=new[]{"温柔","高冷","活泼","严肃","慵懒","俏皮","深沉","干练","凌厉"}},
        new{name="音色定位",tags=new[]{"磁性","醇厚","清亮","空灵","稚嫩","苍老","甜美","沙哑","醇雅"}},
        new{name="人设腔调",tags=new[]{"夹子音","御姐音","正太音","大叔音","台湾腔"}},new{name="方言",tags=new[]{"东北话","四川话","河南话","粤语"}},
        new{name="角色扮演",tags=new[]{"孙悟空","林黛玉"}},new{name="特殊风格",tags=new[]{"唱歌"}}
    ];
}
