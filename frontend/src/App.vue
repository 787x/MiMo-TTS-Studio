<script setup lang="ts">
import {computed, nextTick, onMounted, onUnmounted, reactive, ref, watch} from 'vue'
import SampleLibrary from './SampleLibrary.vue'
import {startPolling} from './polling'
import type {VoiceSample} from './samples'
type SynthesisMode='preset'|'design'|'clone'
type Asset={id:string;title:string;voice:string;created:string;saved:boolean;bytes:number;synthesisMode?:SynthesisMode;sampleEligible?:boolean;sourceSampleId?:string;parameters?:{model:string;sourceSampleName:string|null;user:string;prefix:string}}
type Job={id:string;state:string;completed:number;total:number;assetId:string|null;error:string|null;synthesisMode:SynthesisMode}
type Plan={user:string;prefix:string;segments:string[];assistant:string[];model:string;sourceSample:VoiceSample|null}
const base='/app/mimotts/api'
const modeLabels:Record<SynthesisMode,string>={preset:'预置音色',design:'VoiceDesign',clone:'VoiceClone'}
const selectedMode=ref<SynthesisMode>('preset')
const draft=(synthesisMode:SynthesisMode)=>({text:synthesisMode==='design'?'你好，很高兴在这里与你相遇。愿这段声音，陪你开启新的故事。':'欢迎来到 MiMo 语音创作台。\n让文字拥有声音，让每一次表达都被听见。',voice:'mimo_default',style:'',mode:'natural',character:'',scene:'',direction:'',tags:'',singing:false,split:true,segmentCharacters:3000,synthesisMode,voiceDescription:'',sourceSampleId:null as string|null})
const drafts=reactive({preset:draft('preset'),design:draft('design'),clone:draft('clone')})
const form=computed(()=>drafts[selectedMode.value])
const voices=ref<string[]>([]), configured=ref(false), csrf=ref(''), key=ref(''), error=ref(''), notice=ref(''), loading=ref(true)
const assets=ref<Asset[]>([]), samples=ref<VoiceSample[]>([]), job=ref<Job|null>(null), preview=ref<Plan|null>(null), previewSnapshot=ref(''), newSampleName=ref(''), converting=ref(false)
const results=reactive<Record<SynthesisMode,{id:string;snapshot:string}>>({preset:{id:'',snapshot:''},design:{id:'',snapshot:''},clone:{id:'',snapshot:''}})
const resultId=computed({get:()=>results[selectedMode.value].id,set:(id)=>{results[selectedMode.value].id=id}})
const editor=ref<HTMLTextAreaElement>(), showSettings=ref(false), working=ref(false), pending=ref(false)
const fingerprint=()=>JSON.stringify(form.value)
const textBytes=computed(()=>new TextEncoder().encode(form.value.text).length)
const stale=computed(()=>!!resultId.value && results[selectedMode.value].snapshot!==fingerprint())
const busy=computed(()=>pending.value || (!!job.value && ['running','canceling'].includes(job.value.state)))
const result=computed(()=>assets.value.find(a=>a.id===resultId.value))
const selectedSample=computed(()=>samples.value.find(s=>s.id===drafts.clone.sourceSampleId))
const previewCurrent=computed(()=>preview.value && previewSnapshot.value===fingerprint())
const states:Record<string,string>={running:'正在生成',canceling:'正在取消，请等待服务确认',canceled:'已取消',succeeded:'生成完成',failed:'生成失败'}
const labels=['轻笑','大笑','叹气','深呼吸','哽咽','小声','激动','沉默片刻','语速放缓','提高音量']
const size=(n:number)=>(n/1024/1024).toFixed(2)+' MiB'
const assetLabel=(a:Asset)=>a.synthesisMode&&a.synthesisMode!=='preset'?modeLabels[a.synthesisMode]:modeLabels.preset+' · '+a.voice
const lifetime=new AbortController()
async function api(path:string,method='GET',data?:unknown,signal=lifetime.signal){
 const response=await fetch(base+path,{method,signal,credentials:'same-origin',headers:{...(method!=='GET'?{'X-Studio-CSRF':csrf.value}:{}),...(data!==undefined?{'Content-Type':'application/json'}:{})},body:data!==undefined?JSON.stringify(data):undefined})
 if(!response.ok){let message='请求失败（'+response.status+'）';try{message=(await response.json()).error||message}catch{}throw new Error(message)}
 return response.status===204?null:response.json()
}
async function guarded(action:()=>Promise<void>){error.value='';notice.value='';try{await action()}catch(e){error.value=e instanceof Error?e.message:'操作失败'}}
async function refreshAssets(){assets.value=await api('/assets')}
async function refreshSamples(){samples.value=await api('/samples')}
function chooseSample(id:string){drafts.clone.sourceSampleId=id;selectedMode.value='clone';notice.value='已选择本地样本；只有生成时才发送参考音频到 MiMo。'}
async function convertSample(){if(!result.value||converting.value)return;converting.value=true;await guarded(async()=>{const sample:VoiceSample=await api('/assets/'+result.value!.id+'/sample','POST',{name:newSampleName.value});await refreshSamples();chooseSample(sample.id);notice.value='已保存独立克隆样本并选中。请编辑新正文，再点击生成。'});converting.value=false}
async function configure(clear=false){if(working.value)return; working.value=true;await guarded(async()=>{const secret=key.value;key.value='';await api('/key',clear?'DELETE':'PUT',clear?undefined:{key:secret});configured.value=!clear;notice.value=clear?'API Key 已清除。':'API Key 已更新，浏览器不保留该值。';showSettings.value=false});working.value=false}
async function plan(){await guarded(async()=>{const sent=fingerprint(); const value=await api('/preview','POST',JSON.parse(sent));if(sent===fingerprint()){preview.value=value;previewSnapshot.value=sent}})}
async function generate(){
 if(busy.value)return;pending.value=true
 await guarded(async()=>{const sent=fingerprint();const input=JSON.parse(sent);const next:Job=await api('/jobs','POST',input);results[input.synthesisMode as SynthesisMode]={id:'',snapshot:sent};job.value=next;sessionStorage.setItem('mimo-job',next.id)})
 pending.value=false
}
async function cancel(){await guarded(async()=>{if(job.value)job.value=await api('/jobs/'+job.value.id+'/cancel','POST')})}
let disposed=false, stopPolling=()=>{}
async function poll(signal:AbortSignal){
 try{if(job.value&&['running','canceling'].includes(job.value.state)){
  const id=job.value.id;const value:Job=await api('/jobs/'+id,'GET',undefined,signal)
  if(!disposed&&job.value?.id===id){job.value=value;if(value.assetId){results[value.synthesisMode||'preset'].id=value.assetId;await refreshAssets()}if(value.error)error.value=value.error}
 }}catch(e){if(!signal.aborted)error.value=e instanceof Error?e.message:'状态查询失败；未重新发起合成'}
}
async function save(id:string){await guarded(async()=>{await api('/assets/'+id+'/save','POST');await refreshAssets();notice.value='已保存至私有音频库，未再次调用 MiMo。'})}
async function remove(id:string){if(!window.confirm('删除这条音频？此操作无法撤销。'))return;await guarded(async()=>{await api('/assets/'+id,'DELETE');if(resultId.value===id)resultId.value='';await refreshAssets()})}
function select(a:Asset){selectedMode.value=a.synthesisMode||'preset';resultId.value=a.id;results[selectedMode.value].snapshot='';notice.value='已打开历史音频；播放与下载不会发起合成。'}
function audio(id:string){return base+'/assets/'+id+'/audio'}
async function insert(tag:string){
 const el=editor.value;if(!el)return
 const boundaries=[0,...Array.from(new Intl.Segmenter(undefined,{granularity:'grapheme'}).segment(form.value.text),s=>s.index+s.segment.length)]
 const start=Math.max(...boundaries.filter(n=>n<=el.selectionStart)),end=boundaries.find(n=>n>=el.selectionEnd)??form.value.text.length
 const token='['+tag+']'
 // Prefix selected text without replacing it. Keep line endings and full graphemes.
 form.value.text=form.value.text.slice(0,start)+token+form.value.text.slice(start)
 await nextTick();el.focus();el.setSelectionRange(start+token.length,end+token.length)
}
watch(form,()=>{notice.value=''}, {deep:true})
onMounted(async()=>{await guarded(async()=>{
 const session=await api('/session');csrf.value=session.csrf;configured.value=session.configured;voices.value=session.voices
 await Promise.all([refreshAssets(),refreshSamples()]);const jobs:Job[]=await api('/jobs');const remembered=sessionStorage.getItem('mimo-job')
 const active=jobs.find(j=>['running','canceling'].includes(j.state))??jobs.find(j=>j.id===remembered)
 if(active){job.value=active;if(active.assetId)results[active.synthesisMode||'preset'].id=active.assetId}
 });if(disposed)return;loading.value=false;stopPolling=startPolling(poll)})
onUnmounted(()=>{disposed=true;lifetime.abort();stopPolling()})
</script>

<template>
 <header><div class="brand"><span class="mark">m<span>∿</span></span><div><strong>MiMo TTS Studio</strong><small>语音创作工作台</small></div></div><button class="quiet" @click="showSettings=!showSettings">● <span :class="configured?'ready':'muted'">{{configured?'Key 已配置':'配置 API Key'}}</span> ⚙</button></header>
 <main>
  <section class="intro"><div><span class="eyebrow">VOICE WORKSPACE / 02</span><h1>让文字，拥有表达。</h1><p>从一句问候到一段故事，编排属于你的声音。</p></div><span class="badge">{{modeLabels[selectedMode]}} · MiMo V2.5</span></section>
  <div v-if="error" class="alert" role="alert">{{error}}</div><div v-if="notice" class="notice" role="status">{{notice}}</div>
  <section v-if="showSettings" class="settings card"><div><h2>服务端 API Key</h2><p>Key 保存在 NAS 应用私有目录，仅显示配置状态。文件权限保护，不是加密。</p></div><form @submit.prevent="configure()"><input type="password" v-model="key" autocomplete="new-password" placeholder="输入新的 MiMo API Key" maxlength="512" aria-label="MiMo API Key"><button :disabled="!key||working">更新 Key</button><button type="button" class="quiet danger" :disabled="!configured||working" @click="configure(true)">清除</button></form></section>
  <div v-if="loading" class="card">正在连接创作工作台…</div>
  <div v-else class="workspace">
   <section class="card compose">
    <div class="synthesis-tabs" role="group" aria-label="合成模式"><button v-for="m in (['preset','design','clone'] as const)" :key="m" :class="{active:selectedMode===m}" @click="selectedMode=m">{{modeLabels[m]}}</button></div>
    <label v-if="selectedMode==='design'" class="voice-description">音色描述<textarea v-model="form.voiceDescription" rows="3" placeholder="例如：成年女性，声音温暖醇厚，带一点沙哑，像深夜电台的主持人。"></textarea><small>描述进入 user 指令；下方正文进入 assistant。保持原文，不启用服务端优化。每次独立设计具有随机性，不保证相同描述得到同一声音。</small></label>
    <div v-if="selectedMode==='clone'" class="clone-source"><label>参考声音样本<select v-model="form.sourceSampleId"><option :value="null" disabled>从持久样本库选择；可在右侧上传</option><option v-for="s in samples" :key="s.id" :value="s.id">{{s.name}} · {{s.format.toUpperCase()}} · {{s.durationSeconds.toFixed(2)}} 秒</option></select></label><template v-if="selectedSample"><p class="hint">{{selectedSample.format.toUpperCase()}} · {{selectedSample.durationSeconds.toFixed(2)}} 秒 · {{size(selectedSample.bytes)}} · 本地音频样本，不是云端 Voice ID</p><audio :key="selectedSample.id" :src="base+'/samples/'+selectedSample.id+'/audio'" controls preload="metadata" aria-label="当前克隆参考样本"></audio></template><p v-else class="hint">先上传 WAV / MP3 或从 VoiceDesign 结果保存样本。已删除的样本不能用于新任务。</p></div>
    <div class="section-title"><h2><span>01</span> 脚本与演绎</h2><small>{{textBytes.toLocaleString()}} / 60,000 bytes</small></div>
    <div class="editor"><div v-if="selectedMode!=='design'" class="toolbar"><span>插入标签</span><button v-for="tag in labels" :key="tag" class="tag" @mousedown.prevent @click="insert(tag)">{{tag}}</button></div><textarea ref="editor" v-model="form.text" class="script" aria-label="多行语音脚本" spellcheck="false" placeholder="写下你想听见的文字，或在光标处插入句内标签…"></textarea><div class="editor-foot">{{selectedMode==='design'?'试音正文 · 原文提交':'TEXT → VOICE'}} <span>支持中文 / English</span></div></div>
    <div v-if="selectedMode!=='design'" class="controls"><label v-if="selectedMode==='preset'">预置音色<select v-model="form.voice"><option v-for="v in voices" :key="v" :value="v">{{v==='mimo_default'?'MiMo 默认（随集群变化）':v}}</option></select></label><label>整体风格标签<input v-model="form.tags" placeholder="例如：温柔 清亮" maxlength="200"></label></div>
    <div class="tabs" role="group" aria-label="风格模式"><button v-for="m in [{id:'natural',name:'自然语言'},{id:'director',name:'导演模式'},{id:'combined',name:'组合指导'}]" :key="m.id" :class="{active:form.mode===m.id}" @click="form.mode=m.id">{{m.name}}</button></div>
    <label v-if="form.mode!=='director'">自然语言风格说明<textarea v-model="form.style" rows="2" placeholder="用轻快明亮的语气朗读，语速舒缓，带着笑意。"></textarea></label>
    <div v-if="form.mode!=='natural'" class="director"><label>角色<textarea v-model="form.character" rows="2" placeholder="人物身份、性格与说话习惯"></textarea></label><label>场景<textarea v-model="form.scene" rows="2" placeholder="此刻发生了什么，在对谁说话"></textarea></label><label>指导<textarea v-model="form.direction" rows="2" placeholder="语速、气息、停顿、重音和情绪"></textarea></label></div>
    <div v-if="selectedMode!=='design'" class="options"><label v-if="selectedMode==='preset'" class="check"><input type="checkbox" v-model="form.singing">唱歌模式 <small>每段添加 (唱歌)</small></label><label class="check"><input type="checkbox" v-model="form.split">自动分段</label><label class="target">目标文本元素数 <input type="number" v-model.number="form.segmentCharacters" min="50" max="3000" :disabled="!form.split"></label></div>
    <p class="hint">{{selectedMode==='design'?'VoiceDesign 只生成一段试音，最多 3000 UTF-8 字节。长文本请先保存声音样本，再用 VoiceClone；不会拆成多个随机设计。':'每段含前缀最多 3000 UTF-8 字节；按自然句边界切分。唱歌仅适用于预置模式，歌词建议保持完整。'}}</p>
    <div class="actions"><button class="quiet" @click="plan">预览最终请求</button><button class="primary" :disabled="busy||!configured||!form.text.trim()||(selectedMode==='design'&&!form.voiceDescription.trim())||(selectedMode==='clone'&&!selectedSample)" @click="generate">{{busy?'生成进行中…':'✦ 生成语音'}}</button></div>
    <p class="privacy">在线服务，可能产生费用。仅点击「生成语音」发送本次正文与风格{{selectedMode==='clone'?'及选中的完整参考音频到小米云端':''}}；分段逐次请求，不自动重试。取消后已发出的请求仍可能计费。仅保存、上传或试听样本不发送给 MiMo。</p>
    <details v-if="preview" open class="preview"><summary>实际提交预览 · {{preview.segments.length}} 段 <em v-if="!previewCurrent">（输入已更改，请重新预览）</em></summary><p class="hint">{{preview.model}} · 非流式 WAV<span v-if="preview.sourceSample"> · 参考样本：{{preview.sourceSample.name}}（{{preview.sourceSample.format}}，{{preview.sourceSample.durationSeconds.toFixed(2)}} 秒）</span></p><h3>user · 风格指令 / 音色描述</h3><pre>{{preview.user||'（省略 user 消息）'}}</pre><div v-for="(text,i) in preview.assistant" :key="i"><h3>assistant · 第 {{i+1}} 段</h3><pre>{{text}}</pre></div></details>
   </section>
   <aside>
    <section class="card output"><div class="section-title"><h2><span>02</span> 声音预览</h2><span class="live">WAV</span></div>
     <div class="wave-art" aria-hidden="true"><i v-for="n in 41" :key="n" :style="{height:(12+Math.abs(Math.sin(n*1.7))*46)+'px'}"></i></div>
     <div v-if="job&&(['running','canceling'].includes(job.state)||(job.synthesisMode||'preset')===selectedMode)" class="status" role="status"><strong>{{modeLabels[job.synthesisMode||'preset']}} · {{states[job.state]}}</strong><span>{{job.completed}} / {{job.total}} 段</span><progress :value="job.completed" :max="job.total"></progress><button v-if="busy" class="quiet" :disabled="job.state==='canceling'" @click="cancel">取消任务</button></div>
     <template v-if="result"><div class="result-meta"><strong>{{assetLabel(result)}}</strong><span>{{size(result.bytes)}} · PCM WAV</span></div><p v-if="result.parameters?.sourceSampleName" class="hint">生成时的参考样本：{{result.parameters.sourceSampleName}}</p><p v-if="stale" class="old">这是旧参数或历史音频的结果。当前输入尚未生成。</p><audio :key="result.id" :src="audio(result.id)" controls preload="metadata" aria-label="浏览器音频试听"></audio><div class="result-actions"><a class="button quiet" :href="audio(result.id)+'?download=1'" download>↓ 下载 WAV</a><button :disabled="result.saved" @click="save(result.id)">{{result.saved?'✓ 已保存':'保存到音频库'}}</button></div><form v-if="result.synthesisMode==='design'" class="convert-sample" @submit.prevent="convertSample"><label>克隆样本名称<input v-model="newSampleName" maxlength="160" placeholder="为这个声音命名"></label><button :disabled="!result.sampleEligible||!newSampleName.trim()||converting">保存为克隆样本并切换</button><p class="hint">{{result.sampleEligible?'复制本次成功音频；不再次调用 MiMo，删除原结果不影响样本。':'该结果不满足本应用的 0.2–120 秒 / 7.4 MB 参考样本范围，无法转换；仍可试听或下载。'}}</p></form></template>
     <div v-else class="empty"><strong>你的下一段声音，从这里开始</strong><p>生成完成后在浏览器试听与下载。<br>播放、下载和保存都复用同一结果。</p></div>
     <details v-if="result?.parameters" class="preview"><summary>生成时的参数快照</summary><p class="hint">{{result.parameters.model}} · {{result.parameters.sourceSampleName||result.voice}}</p><h3>user</h3><pre>{{result.parameters.user||'（未指定风格）'}}</pre><h3>整体前缀</h3><pre>{{result.parameters.prefix||'（无）'}}</pre></details>
    </section>
    <section class="card library"><div class="section-title"><h2><span>03</span> 私有音频库</h2><button class="text-button" @click="guarded(refreshAssets)">刷新</button></div><p class="hint">保存在 NAS · {{assets.length}} 条音频 / {{samples.length}} 条样本 · 共用 1 GiB / 1000 条配额<br>未保存结果在启动时清理超过 24 小时的条目。</p><p v-if="!assets.length" class="empty-small">还没有音频。完成一次创作后，在这里继续收听。</p><article v-for="a in assets" :key="a.id" class="asset"><button class="asset-title" @click="select(a)"><span class="asset-icon">▷</span><span><strong>{{a.title}}</strong><small>{{assetLabel(a)}} · {{new Date(a.created).toLocaleString()}} · {{a.saved?'已保存':'临时结果'}}</small></span></button><div class="asset-actions"><a :href="audio(a.id)+'?download=1'" download>下载</a><button v-if="!a.saved" class="text-button" @click="save(a.id)">保存</button><button class="text-button danger" @click="remove(a.id)">删除</button></div></article></section>
    <SampleLibrary :samples="samples" :csrf="csrf" :api="api" @refresh="guarded(refreshSamples)" @select="chooseSample" @error="error=$event" @notice="notice=$event"/>
    <p class="footnote">独立的 fnOS 语音创作工具<br>音频由 MiMo 云端生成 · 播放发生在你的浏览器</p>
   </aside>
  </div>
 </main>
</template>
