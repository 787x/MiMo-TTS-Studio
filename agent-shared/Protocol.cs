using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Studio.Agent;
public static class Wire
{
    public const int Version = 1, MaxJson = 256 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24 };
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Json);
    public static async Task<byte[]> Read(Stream stream, int max, CancellationToken ct)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        int n = BinaryPrimitives.ReadInt32BigEndian(header);
        if (n < 1 || n > max) throw new InvalidDataException();
        byte[] data = new byte[n]; await stream.ReadExactlyAsync(data, ct); return data;
    }
    public static async Task Write(Stream stream, byte[] bytes, int max, CancellationToken ct)
    {
        if (bytes.Length < 1 || bytes.Length > max) throw new InvalidDataException();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
}
public sealed record AgentCredential(string Id, string Secret);
public sealed record AgentRequest(int Version, AgentCredential Credential, string Tool, JsonElement Arguments);
public sealed record AgentReply(bool Success, string Code, JsonElement Data)
{
    public static AgentReply Ok(object? data) => new(true, "ok", Wire.Element(data));
    public static AgentReply Fail(string code) => new(false, code, Wire.Element(new { }));
    [JsonIgnore] public int ExitCode => Code switch { "ok" => 0, "invalid_input" => 2, "permission_denied" => 3, "service_unavailable" or "incompatible_service" => 4, "busy" => 5, "timeout" or "canceled" => 7, _ => 6 };
}
public sealed record ToolSpec(string Name, string Scope, string Description, JsonElement Schema, bool ReadOnly = false, bool Destructive = false, bool CliOnly = false);
public static class Catalog
{
    public static readonly string[] Scopes = ["read", "generate", "manage", "export", "configure", "delete"];
    public static readonly Dictionary<string, object> Fields = new()
    {
        ["synthesisMode"] = Choice("preset", "design", "clone"), ["voice"] = Text(40), ["voiceDescription"] = Text(6000),
        ["sourceSampleId"] = Text(32), ["style"] = Text(6000), ["mode"] = Choice("natural", "director", "combined"),
        ["character"] = Text(6000), ["scene"] = Text(6000), ["direction"] = Text(6000), ["tags"] = Text(200),
        ["singing"] = new { type = new[] { "boolean", "null" } }, ["split"] = new { type = new[] { "boolean", "null" } },
        ["segmentCharacters"] = new { type = new[] { "integer", "null" }, minimum = 50, maximum = 3000 }
    };
    public static object Text(int max) => new { type = new[] { "string", "null" }, maxLength = max };
    static object Choice(params string[] values) => new Dictionary<string, object?> { ["type"] = new[] { "string", "null" }, ["enum"] = values.Cast<object?>().Append(null).ToArray() };
    public static JsonElement Object(Dictionary<string, object> fields, params string[] required) => Wire.Element(new { type = "object", properties = fields, additionalProperties = false, required });
    public static readonly JsonElement Parameters = Object(Fields);
    static JsonElement Args(params (string, object)[] fields) => Object(fields.ToDictionary(x => x.Item1, x => x.Item2));
    static readonly JsonElement Tts = Args(("text", Text(60000)), ("parameters", Parameters), ("preset", Text(64)));
    static readonly JsonElement Submit = Args(("text", Text(60000)), ("parameters", Parameters), ("preset", Text(64)), ("requestId", Text(80)));
    static readonly JsonElement Id = Object(new() { ["id"] = Text(32) }, "id");
    static readonly JsonElement Name = Object(new() { ["name"] = Text(64) }, "name");
    static readonly JsonElement Page = Args(("offset",new{type="integer",minimum=0,maximum=1000}));
    static readonly JsonElement Diagnostic = Object(new() { ["service"] = Choice("preset", "design", "clone", "llm"), ["sampleId"] = Text(32) }, "service");
    static ToolSpec S(string name, string scope, string description, JsonElement? schema = null, bool read = false, bool destructive = false, bool cli = false) => new(name, scope, description, schema ?? Args(), read, destructive, cli);
    public static readonly ToolSpec[] Tools =
    [
        S("get_capabilities","read","List authorized operations, protocol and limits.",read:true),
        S("list_voices","read","Preset voice IDs.",read:true), S("list_style_presets","read","Overall style vocabulary; free text is also supported.",read:true),
        S("list_inline_tags","read","Inline audio labels; original text is preserved.",read:true),
        S("get_service_status","read","Public configuration summary; never credentials.",read:true),
        S("get_agent_defaults","read","Read defaults for this grant.",read:true), S("list_presets","read","List named presets for this grant.",read:true),
        S("get_preset","read","Read named preset.",Name,true),
        S("save_preset","configure","Replace named preset; no text, credentials or paths.",Object(new(){["name"]=Text(64),["parameters"]=Parameters},"name","parameters")),
        S("delete_preset","delete","Delete named preset.",Name,destructive:true),
        S("set_agent_defaults","configure","Merge defaults. Null resets a field to backend default; clear=true removes all inherited fields.",Args(("parameters",Parameters),("clear",new{type="boolean"}))),
        S("preview_synthesis","read","Local Composition preview including actual user/assistant segments; no cloud, upload or Job.",Tts,true),
        S("synthesize_audio","generate","Submit TTS Job. Sends text/style and selected clone sample to MiMo; may charge. Disconnect does not cancel. No retry.",Submit),
        S("list_jobs","read","Current user's Jobs; omits creative text.",Page,read:true), S("get_job_status","read","Job state, segment progress, result asset ID and safe error.",Id,true),
        S("cancel_job","generate","Request cancellation of owned Job only; poll until terminal state.",Id),
        S("generate_voice_description","generate","Paid LLM voice description candidate, no TTS or draft changes.",Tts),
        S("generate_style_instruction","generate","Paid LLM style candidate, no TTS or draft changes.",Tts),
        S("generate_director_plan","generate","Paid LLM role/scene/direction candidate, no TTS or draft changes.",Tts),
        S("generate_script","generate","Paid LLM script candidate, no TTS or draft changes.",Tts),
        S("generate_lyrics","generate","Paid LLM lyrics candidate; preset singing mode required.",Tts),
        S("annotate_inline_tags","generate","Paid LLM label insertions; rejects any rewriting of original text.",Tts),
        S("list_voice_samples","read","Owned sample metadata, no audio.",Page,read:true), S("get_voice_sample","read","Owned sample metadata.",Id,true),
        S("create_sample_from_asset","manage","Copy successful VoiceDesign asset into sample library; no cloud.",Object(new(){["id"]=Text(32),["name"]=Text(160)},"id","name")),
        S("rename_voice_sample","manage","Rename owned sample.",Object(new(){["id"]=Text(32),["name"]=Text(160)},"id","name")),
        S("delete_voice_sample","delete","Delete owned private sample; active Jobs keep their snapshot.",Id,destructive:true),
        S("list_audio_assets","read","Owned asset metadata; no text/audio.",Page,read:true), S("get_audio_asset","read","Owned WAV format and metadata.",Id,true),
        S("save_audio_asset","manage","Mark owned private asset as saved; no synthesis.",Id), S("delete_audio_asset","delete","Delete owned private WAV only; exported NAS files remain.",Id,destructive:true),
        S("list_nas_export_targets","export","Live fnOS authorized targets by ID; paths omitted.",read:true),
        S("export_audio_to_nas","export","Copy existing WAV via NasExport, live fnOS ACL, no overwrite. No synthesis.",Object(new(){["id"]=Text(32),["directoryId"]=Text(64),["fileName"]=Text(160)},"id","directoryId","fileName")),
        S("check_service_configuration","read","Offline precheck; clone requires explicitly owned sample.",Diagnostic,true),
        S("probe_service","generate","One paid real cloud probe; clone uploads explicitly selected sample. No saved audio.",Diagnostic),
        S("import_sample","manage","CLI-only explicit local WAV/MP3 transfer.",Object(new(){["name"]=Text(160),["format"]=Choice("wav","mp3")},"name","format"),cli:true),
        S("download_asset","read","CLI-only bounded binary download of owned WAV; no synthesis.",Id,true,cli:true)
    ];
    public static ToolSpec Find(string name) => Tools.SingleOrDefault(t => t.Name == name) ?? throw new ArgumentException();
    // Validate exactly the same schema on both sides. Unknown fields and wrong types are errors.
    public static void Validate(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var types))
        {
            string kind = value.ValueKind switch { JsonValueKind.Object => "object", JsonValueKind.String => "string", JsonValueKind.Null => "null", JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Number when value.TryGetInt32(out _) => "integer", _ => "invalid" };
            if (types.ValueKind == JsonValueKind.Array ? !types.EnumerateArray().Any(t => t.GetString() == kind) : types.GetString() != kind) throw new ArgumentException();
        }
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(c,value))) throw new ArgumentException();
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("maxLength", out var length) && value.GetString()!.Length > length.GetInt32()) throw new ArgumentException();
        if (value.ValueKind == JsonValueKind.Number && (value.GetInt32() < schema.GetProperty("minimum").GetInt32() || value.GetInt32() > schema.GetProperty("maximum").GetInt32())) throw new ArgumentException();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(); var properties = schema.GetProperty("properties");
            foreach (var p in value.EnumerateObject()) { if (!seen.Add(p.Name) || !properties.TryGetProperty(p.Name, out var s)) throw new ArgumentException(); Validate(p.Value, s); }
            foreach (var p in schema.GetProperty("required").EnumerateArray()) if (!seen.Contains(p.GetString()!) || value.GetProperty(p.GetString()!).ValueKind == JsonValueKind.Null) throw new ArgumentException();
        }
    }
}
