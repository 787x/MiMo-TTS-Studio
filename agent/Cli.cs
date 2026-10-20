using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Studio.Agent;
namespace MiMoStudio.Agent;
public sealed record CliArguments(string Tool,JsonElement Arguments,string Socket,string Credential,bool Wait,int Timeout,string? Input,string? Output,bool Help=false);
public static class Cli
{
    public const string Help="""
MiMoStudio.Agent 0.7.1 — CLI / MCP stdio for the running NAS backend
Usage: MiMoStudio.Agent <tool> [options]
       MiMoStudio.Agent mcp --credential-file <private-file>
       MiMoStudio.Agent --help
Global: --credential-file FILE (or MIMO_AGENT_CREDENTIAL_FILE), --socket FILE
Input: --text TEXT | --file UTF8_FILE | --stdin (60000 UTF-8 bytes, no truncation)
       --parameters JSON (TTS fields), --preset NAME, --request-id ID
       --wait, --timeout SECONDS (1..1200; waiting never cancels submitted Job)
Other tool arguments: --id ID, --name NAME, --service preset|design|clone|llm,
       --sample-id ID, --directory-id ID, --file-name NAME, --offset N, --clear true|false
TTS fields also accept flags: --synthesis-mode, --voice, --voice-description,
       --source-sample-id, --style, --mode natural|director|combined,
       --character, --scene, --direction, --tags, --singing true|false,
       --split true|false, --segment-characters 50..3000.
CLI file transfer: import_sample --input FILE --name NAME --format wav|mp3
                   download_asset --id ID --output NEW_FILE
Tools: get_capabilities, list_voices, list_style_presets, list_inline_tags,
 get_service_status, get_agent_defaults, set_agent_defaults, list_presets,
 get_preset, save_preset, delete_preset, preview_synthesis, synthesize_audio,
 list_jobs, get_job_status, cancel_job, generate_voice_description,
 generate_style_instruction, generate_director_plan, generate_script,
 generate_lyrics, annotate_inline_tags, list_voice_samples, get_voice_sample,
 create_sample_from_asset, rename_voice_sample, delete_voice_sample,
 list_audio_assets, get_audio_asset, save_audio_asset, delete_audio_asset,
 list_nas_export_targets, export_audio_to_nas, check_service_configuration,
 probe_service. Use <tool> --help to print the exact JSON schema.
Examples:
 MiMoStudio.Agent preview_synthesis --text '你好' --voice 冰糖 --tags '温柔 清亮'
 MiMoStudio.Agent synthesize_audio --stdin --request-id my-run-1 --wait
 MiMoStudio.Agent get_job_status --id JOB_ID
 MiMoStudio.Agent cancel_job --id JOB_ID
Cloud tools may charge and transmit text/style (clone also uploads the chosen sample).
JSON result: success/code/data. Exit: 0 success, 2 input, 3 permission, 4 service,
5 busy, 6 execution failure/conflict, 7 timeout/cancel. WAV is not NAS sound-card playback.
""";
    static string Camel(string value)=>string.Concat(value.Split('-').Select((s,i)=>i==0?s:char.ToUpperInvariant(s[0])+s[1..]));
    public static async Task<CliArguments> Parse(string[] argv,CancellationToken ct)
    {
        if(argv.Length==0||argv is ["--help"]||argv is ["-h"])return new("",Wire.Element(new{}),"","",false,1200,null,null,true);
        string tool=argv[0].Replace('-','_');if(tool!="mcp")_=Catalog.Find(tool);
        var args=new JsonObject();var parameters=new JsonObject();var seen=new HashSet<string>();
        string socket=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","agent.sock"));
        string credential=Environment.GetEnvironmentVariable("MIMO_AGENT_CREDENTIAL_FILE")??"";
        bool wait=false,stdin=false,help=false;int timeout=1200;string? file=null,input=null,output=null;
        for(int i=1;i<argv.Length;i++)
        {
            string option=argv[i];if(!option.StartsWith("--")||!seen.Add(option))throw new ArgumentException();
            if(option=="--help"){help=true;continue;}if(option=="--wait"){wait=true;continue;}if(option=="--stdin"){stdin=true;continue;}
            if(++i>=argv.Length)throw new ArgumentException();string value=argv[i];
            switch(option)
            {
                case "--socket":socket=Path.GetFullPath(value);break;
                case "--credential-file":credential=Path.GetFullPath(value);break;
                case "--timeout":if(!int.TryParse(value,out timeout)||timeout<1||timeout>1200)throw new ArgumentException();break;
                case "--file":file=value;break;case "--input":input=value;break;case "--output":output=value;break;
                case "--parameters":
                    using(var doc=JsonDocument.Parse(value)){Catalog.Validate(doc.RootElement,Catalog.Parameters);foreach(var p in doc.RootElement.EnumerateObject()){if(parameters.ContainsKey(p.Name))throw new ArgumentException();parameters[p.Name]=JsonNode.Parse(p.Value.GetRawText());}}break;
                default:
                    string key=Camel(option[2..]);bool field=Catalog.Fields.ContainsKey(key);var target=field?parameters:args;
                    if(target.ContainsKey(key))throw new ArgumentException();
                    target[key]=key is "singing" or "split" or "clear"?JsonValue.Create(bool.Parse(value)):key is "segmentCharacters" or "offset"?JsonValue.Create(int.Parse(value,System.Globalization.CultureInfo.InvariantCulture)):JsonValue.Create(value);break;
            }
        }
        if(tool=="mcp")
        {if(seen.Any(s=>s is not("--credential-file" or "--socket" or "--help")))throw new ArgumentException();return new(tool,Wire.Element(new{}),socket,credential,false,timeout,null,null,help);}
        if(help)return new(tool,Wire.Element(new{}),socket,credential,false,timeout,null,null,true);
        if((args.ContainsKey("text")?1:0)+(file!=null?1:0)+(stdin?1:0)>1)throw new ArgumentException();
        if(stdin||file!=null)
        {
            using var stream=file!=null?LinuxIdentity.OpenPrivate(file,60000):Console.OpenStandardInput();
            using var content=new MemoryStream();byte[] buffer=new byte[8192];int n;
            while((n=await stream.ReadAsync(buffer,ct))>0){if(content.Length+n>60000)throw new ArgumentException();content.Write(buffer,0,n);}
            string decoded=new UTF8Encoding(false,true).GetString(content.ToArray());args["text"]=decoded.StartsWith('\uFEFF')?decoded[1..]:decoded;
        }
        if(parameters.Count>0||seen.Contains("--parameters"))args["parameters"]=parameters;
        if(wait&&tool is not("synthesize_audio" or "get_job_status"))throw new ArgumentException();
        if((input!=null)!=(tool=="import_sample")||(output!=null)!=(tool=="download_asset"))throw new ArgumentException();
        var element=Wire.Element(args);if(!help)Catalog.Validate(element,Catalog.Find(tool).Schema);
        return new(tool,element,socket,credential,wait,timeout,input,output,help);
    }
}
