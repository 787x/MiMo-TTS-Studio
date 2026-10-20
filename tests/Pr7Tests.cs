using System.Text.Json;
using Studio;
using Studio.Agent;
using MiMoStudio.Agent;
static class Pr7Tests
{
    public static async Task Run(Action<bool,string> check)
    {
        void Reject(Action action,string name){try{action();}catch(Exception e) when(e is InputError or ArgumentException or UnauthorizedAccessException or JsonException){check(true,"PR7 "+name);return;}throw new Exception(name);}
        var path=Path.Combine(Path.GetTempPath(),"mimo-agent-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var storage=new Storage(Path.Combine(path,"etc"),Path.Combine(path,"audio"),Path.Combine(path,"tmp"));storage.SetKey("dummy-agent-key");
            var store=new AgentStore(Path.Combine(path,"agent"));var view=store.Create("1000",new("test",Catalog.Scopes));
            var credential=JsonSerializer.Deserialize<AgentCredential>(File.ReadAllBytes(Path.Combine(path,"agent",view.Id+".credential")),Wire.Json)!;
            check(store.List("1001").Length==0&&store.Verify(credential).View.Owner=="1000","PR7 grant bound to trusted creator");
            Reject(()=>store.Verify(credential with{Secret=new string('x',64)}),"invalid secret denied");
            Reject(()=>store.Create("1000",new("bad",["root"])),"unknown scopes denied");
            var limited=store.Create("1001",new("read",["read"]));
            var other=JsonSerializer.Deserialize<AgentCredential>(File.ReadAllBytes(Path.Combine(path,"agent",limited.Id+".credential")),Wire.Json)!;
            Reject(()=>store.Authorized(other,"generate",g=>true),"scope checked server-side");
            var defaults=Wire.Element(new{parameters=new{style="温柔",tags="清亮",segmentCharacters=50}});store.Configure(credential,"set_agent_defaults",defaults);
            store.Configure(credential,"save_preset",Wire.Element(new{name="narrator",parameters=new{style="严肃",singing=true}}));
            var r=AgentStore.Resolve(store.Verify(credential),Wire.Element(new{text="原文 é👋",preset="narrator",parameters=new{style="",singing=false}}));
            check(r.Style==""&&!r.Singing&&r.Tags=="清亮"&&r.SegmentCharacters==50,"PR7 explicit > preset > defaults > backend");
            var reset=AgentStore.Resolve(store.Verify(credential),Wire.Element(new{text="正文",parameters=new{style=(string?)null,split=(bool?)null,segmentCharacters=(int?)null}}));
            check(reset.Style==""&&reset.Split&&reset.SegmentCharacters==3000,"PR7 null resets backend default while omission inherits");
            check(new AgentStore(Path.Combine(path,"agent")).Verify(credential).Presets.Count==1,"PR7 persistent grants and presets survive reopen");
            foreach(var fields in new[]{"{\"key\":\"bad\"}","{\"baseUrl\":\"https://evil.test\"}","{\"text\":\"secret\"}","{\"sourceSampleId\":\"/etc/passwd\"}","{\"segmentCharacters\":2}","{\"singing\":\"true\"}","{\"style\":\"a\",\"style\":\"b\"}"})
                Reject(()=>AgentStore.ValidateFields(JsonDocument.Parse(fields).RootElement),"invalid/unknown preset fields");
            var wave=Wave.Merge([new(24000,new byte[24000])]);var handler=new AgentHttp(wave);var mimo=new Mimo(new HttpClient(handler));var jobs=new Jobs(storage,mimo,new Lifetime());
            var llm=new Llm(storage,new HttpClient(handler));var execution=new AgentExecution(storage,jobs,llm,new Diagnostics(storage,mimo,llm),new NasExport(storage,FnOsDirectories.Create()),store);
            AgentRequest Req(string name,object? args=null,AgentCredential? c=null)=>new(1,c??credential,name,Wire.Element(args??new{}));
            var preview=await execution.Execute(Req("preview_synthesis",new{text="原文",parameters=new{mode="combined",style="温柔",character="旁白",tags="清亮"}}),null,default);
            check(preview.Success&&preview.Data.GetProperty("prefix").GetString()=="(清亮)"&&handler.Calls==0&&jobs.List("1000").Length==0,"PR7 preview reuses Composition without cloud or Job");
            var submit=Req("synthesize_audio",new{text="Agent 测试",requestId="same-request",parameters=new{tags="温柔 清亮",segmentCharacters=3000}});
            var first=await execution.Execute(submit,null,default);string jobId=first.Data.GetProperty("job").GetString()!;
            var replay=await execution.Execute(submit,null,default);
            check(replay.Data.GetProperty("job").GetString()==jobId&&replay.Data.GetProperty("replayed").GetBoolean(),"PR7 same id and payload reuses Job");
            while(jobs.Get(jobId,"1000")!.State=="running")await Task.Delay(5);
            check(handler.Calls==1&&jobs.Get(jobId,"1000")!.State=="succeeded","PR7 disconnected submit finishes through shared Jobs and Storage");
            try{await execution.Execute(Req("synthesize_audio",new{text="changed",requestId="same-request"}),null,default);throw new Exception();}catch(AgentConflict){check(true,"PR7 conflicting id rejected before cloud");}
            string assetId=jobs.Get(jobId,"1000")!.AssetId!;
            var list=await execution.Execute(Req("list_audio_assets"),null,default);
            check(list.Data.GetProperty("items").GetArrayLength()==1&&!list.Data.GetRawText().Contains("Agent 测试")&&!list.Data.GetRawText().Contains("dummy-agent-key"),"PR7 bounded metadata hides creative text and Key");
            try{await execution.Execute(Req("get_audio_asset",new{id=assetId},other),null,default);throw new Exception();}catch(InputError){check(true,"PR7 cross-owner asset denied");}
            try{await execution.Execute(Req("get_job_status",new{id=jobId},other),null,default);throw new Exception();}catch(InputError){check(true,"PR7 cross-owner Job denied");}
            var unsupported=new[]{new ScriptRequest("test",SynthesisMode:"clone",SourceSampleId:new string('a',32),Singing:true),new ScriptRequest("test",SynthesisMode:"design",VoiceDescription:"warm",Tags:"清亮")};
            foreach(var script in unsupported)Reject(()=>Composition.Plan(script),"mode-specific unsupported fields rejected");
            var names=Catalog.Tools.Select(t=>t.Name).ToArray();check(names.Length==names.Distinct().Count()&&names.Length>=36,"PR7 concrete unique tool schemas");
            foreach(var tool in Catalog.Tools)Reject(()=>Catalog.Validate(Wire.Element(new{unexpected="value"}),tool.Schema),"tool rejects unknown args "+tool.Name);
            using(var overlong=new MemoryStream(new byte[128*1024+1].Select(_=>(byte)'x').ToArray()))
            using(var bounded=new BoundedInput(overlong))
            {try{await bounded.ReadAsync(new byte[128*1024+1]);throw new Exception();}catch(InvalidDataException){check(true,"PR7 MCP oversized input line rejected before SDK parse");}}
            var help=await Cli.Parse(["preview_synthesis","--help","--file","/nonexistent"],default);
            check(help.Help,"PR7 help never opens input files");
            var cli=await Cli.Parse(["synthesize_audio","--text","--help","--style","温柔","--singing","false","--wait"],default);
            check(cli.Arguments.GetProperty("text").GetString()=="--help"&&cli.Wait&&!cli.Help,"PR7 CLI value --help is preserved");
            foreach(var argv in new[]{new[]{"synthesize_audio","--text","a","--text","b"},new[]{"synthesize_audio","--text","a","--stdin"},new[]{"synthesize_audio","--text","a","--parameters","{\"key\":\"bad\"}"},new[]{"list_jobs","--wait"},new[]{"mcp","--text","bad"}})
            {try{await Cli.Parse(argv,default);throw new Exception();}catch(ArgumentException){check(true,"PR7 CLI invalid input rejected");}}
            store.Configure(credential,"set_agent_defaults",Wire.Element(new{clear=true}));check(store.Verify(credential).Defaults.EnumerateObject().Count()==0,"PR7 defaults explicitly clear");
            Parallel.For(0,10,i=>store.Configure(credential,"save_preset",Wire.Element(new{name="p"+i,parameters=new{style="s"+i}})));
            check(store.Verify(credential).Presets.Count==11,"PR7 concurrent preset writes retain every update");
            check(!store.Revoke("1001",view.Id)&&store.Revoke("1000",view.Id),"PR7 revoke owner checked");
            Reject(()=>store.Verify(credential),"reused credential rejected after revoke");
            check(!File.Exists(Path.Combine(path,"agent",view.Id+".credential")),"PR7 revoked credential file removed");
        }
        finally{Directory.Delete(path,true);}
    }
    sealed class AgentHttp(byte[] wav):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{audio=new{data=Convert.ToBase64String(wav)}}}}}))});}
    }
}
