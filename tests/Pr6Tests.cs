using System.Text;
using System.Text.Json;
using Studio;

static class Pr6Tests
{
 public static void Run(Action<bool,string> check, Action<Action,string> reject)
 {
  const string tags="温柔 清亮 上海话";
  foreach(var mode in new[]{"preset","clone"})
  {
   var r=new ScriptRequest("你好。[轻笑]原文 e\u0301👋\r\n"+new string('中',2000), Tags:tags,
    SynthesisMode:mode, SourceSampleId:mode=="clone"?new string('a',32):null);
   var p=Composition.Plan(r);
   check(p.Prefix=="("+tags+")"&&p.User==""&&string.Concat(p.Segments)==r.Text,"PR6 "+mode+" prefix and original body");
   check(p.Assistant.All(s=>s.StartsWith(p.Prefix)&&Encoding.UTF8.GetByteCount(s)<=3000),"PR6 "+mode+" every segment includes budgeted prefix");
   foreach(var segment in p.Segments)
   {
    var payload=JsonSerializer.SerializeToElement(Mimo.Payload(r,p,segment,mode=="clone"?"data:audio/wav;base64,"+Convert.ToBase64String(Wave.Merge([new(24000,[0,0])])):null));
    var messages=payload.GetProperty("messages");
    check(messages.GetArrayLength()==1&&messages[0].GetProperty("role").GetString()=="assistant"&&messages[0].GetProperty("content").GetString()==p.Prefix+segment,"PR6 "+mode+" payload matches preview without user duplication");
   }
  }
  foreach(var tagsValue in new[]{"  soft voice  e\u0301 上海话 ","é e\u0301 News news","😀",new string('x',200)})
   check(Composition.Plan(new("原文",Tags:tagsValue)).Prefix=="("+tagsValue+")","PR6 free Tags preserved exactly");
  foreach(var unsafeTags in new[]{"x\t","x\0","x\n","x\u200b","x\u202e","x\U000e0001","x\ud800","x\udfff","【坏】","x\u00a0y",new string('x',201)})
   reject(()=>Composition.Plan(new("原文",Tags:unsafeTags)),"PR6 unsafe Tags rejected "+JsonSerializer.Serialize(unsafeTags));
  foreach(var alias in new[]{"唱歌","sing","SING","Singing","温柔 singing"})
   foreach(var mode in new[]{"preset","clone"})
    reject(()=>Composition.Plan(new("原文",Tags:alias,SynthesisMode:mode,SourceSampleId:mode=="clone"?new string('a',32):null)),"PR6 alias cannot bypass singing "+mode+" "+alias);
  check(Composition.Plan(new("歌词",Tags:"清亮",Singing:true)).Prefix=="(唱歌)(清亮)","PR6 singing and style combination unchanged");
  check(Composition.Plan(new("原文")).Prefix=="","PR6 empty prefix");
  var exact=new ScriptRequest(new string('x',3000-Encoding.UTF8.GetByteCount("("+tags+")")),Tags:tags,Split:false);
  check(Encoding.UTF8.GetByteCount(Composition.Plan(exact).Assistant[0])==3000,"PR6 exact UTF8 budget accepted");
  reject(()=>Composition.Plan(exact with {Text=exact.Text+"x"}),"PR6 one byte beyond budget rejected");
  reject(()=>Composition.Plan(new("["+new string('x',2980)+"]",Tags:tags)),"PR6 atomic annotation plus new prefix exceeds budget");
 }
}
