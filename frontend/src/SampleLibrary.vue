<script setup lang="ts">
import {onUnmounted,reactive,ref,watch} from 'vue'
import type {VoiceSample} from './samples'
import {useSampleRecording} from './sampleRecording'
const props=defineProps<{samples:VoiceSample[];csrf:string;api:(path:string,method?:string,data?:unknown)=>Promise<any>}>()
const emit=defineEmits<{refresh:[];select:[id:string];error:[message:string];notice:[message:string]}>()
const name=ref(''),file=ref<File>(),working=ref(false),names=reactive<Record<string,string>>({})
const recordingName=ref('')
const {unavailable,state,seconds,error:recordingError,draft,start,stop,discard,clearDraft}=useSampleRecording()
const sources={upload:'本地上传',design:'VoiceDesign 结果的独立副本',recording:'浏览器录音'}
const lifetime=new AbortController()
onUnmounted(()=>lifetime.abort())
watch(()=>props.samples,samples=>{for(const sample of samples)if(names[sample.id]===undefined)names[sample.id]=sample.name},{immediate:true})
function choose(event:Event){file.value=(event.target as HTMLInputElement).files?.[0];if(file.value&&!name.value)name.value=file.value.name.replace(/\.[^.]+$/,'')}
async function action(run:()=>Promise<void>){if(working.value)return;working.value=true;try{emit('error','');await run()}catch(e){if(!lifetime.signal.aborted)emit('error',e instanceof Error?e.message:'样本操作失败')}finally{working.value=false}}
async function upload(){await action(async()=>{
 if(!file.value)return
 const format=file.value.name.split('.').pop()?.toLowerCase()
 if(!['wav','mp3'].includes(format||''))throw new Error('请选择 WAV 或 MP3 文件。')
 if(file.value.size>7_400_000)throw new Error('参考音频最多 7.4 MB，含前缀的编码大小需小于 10 MB。')
 await saveSample(file.value,name.value,format!,'upload')
})}
async function saveRecording(){
 if(!draft.value||!recordingName.value.trim())return
 await action(async()=>{
  if(!draft.value)return
  await saveSample(draft.value.wave,recordingName.value,'wav','recording')
  clearDraft()
  recordingName.value=''
 })
}
async function saveSample(data:Blob,sampleName:string,format:string,source:'upload'|'recording'){
 const response=await fetch('/app/mimotts/api/samples/upload',{method:'POST',signal:lifetime.signal,credentials:'same-origin',headers:{'Content-Type':'application/octet-stream','X-Studio-CSRF':props.csrf,'X-Sample-Name':encodeURIComponent(sampleName),'X-Sample-Format':format,'X-Sample-Source':source},body:data})
 if(!response.ok){let message='上传失败（'+response.status+'）';try{message=(await response.json()).error||message}catch{}throw new Error(message)}
 const sample:VoiceSample=await response.json();emit('refresh');emit('notice','样本已保存在 NAS 私有目录，未发送给 MiMo。');emit('select',sample.id)
}
async function rename(s:VoiceSample){await action(async()=>{await props.api('/samples/'+s.id,'PUT',{name:names[s.id]});emit('refresh');emit('notice','样本名称已更新。')})}
async function remove(s:VoiceSample){if(!window.confirm('永久删除样本「'+s.name+'」？已生成音频不受影响，正在进行的克隆仍使用启动时的独立快照。'))return;await action(async()=>{await props.api('/samples/'+s.id,'DELETE');emit('refresh');emit('notice','样本已删除，已有生成音频不受影响。')})}
</script>
<template>
 <section class="card sample-library"><div class="section-title"><h2><span>04</span> 持久声音样本库</h2><span class="live">仅本地保存</span></div>
  <p class="hint">样本仅保存到 NAS，选用它启动克隆时才会发送给 MiMo。请使用你有权使用的清晰单人语音。样本支持 0.2–120 秒、最多 7.4 MB。</p>
  <form @submit.prevent="upload"><label>样本名称<input v-model="name" maxlength="160" placeholder="例如：温柔旁白试音"></label><label>选择 WAV / MP3<input type="file" accept=".wav,.mp3,audio/wav,audio/mpeg" @change="choose"></label><p v-if="file" class="hint">{{file.name}} · {{(file.size/1000000).toFixed(2)}} MB</p><button :disabled="working||!file||!name.trim()">{{working?'处理中…':'上传到私有样本库'}}</button></form>
  <section class="sample-recorder">
   <h3>浏览器录音</h3>
   <p v-if="unavailable" class="hint">{{unavailable}}</p>
   <template v-else>
    <p class="hint">最长 120 秒。先在浏览器试听，确认后再保存到 NAS。</p>
    <p v-if="state==='requesting'" role="status" class="hint">等待麦克风授权…</p>
    <p v-if="state==='recording'" class="hint">录音中 · {{seconds}} / 120 秒</p>
    <p v-if="state==='processing'" role="status" class="hint">正在准备录音试听…</p>
    <p v-if="recordingError" role="alert" class="hint">{{recordingError}}</p>
    <p v-if="recordingError&&!unavailable" class="hint"><a href="/app/mimotts/" target="_blank" rel="noopener">在新标签页打开工作台</a></p>
    <form v-if="draft" @submit.prevent="saveRecording">
     <audio :src="draft.url" controls aria-label="试听待保存录音"></audio>
     <label>录音样本名称<input v-model="recordingName" maxlength="160" :disabled="working" placeholder="例如：旁白试音"></label>
     <button :disabled="working||!recordingName.trim()">{{working?'处理中…':'保存录音到样本库'}}</button>
    </form>
    <div class="sample-actions">
     <button v-if="state==='idle'" type="button" :disabled="working" @click="start">{{draft?'重新录音':'开始录音'}}</button>
     <button v-if="state==='recording'" type="button" @click="stop">停止录音</button>
     <button v-if="state!=='idle'||draft" type="button" class="quiet" :disabled="working&&!!draft" @click="discard">{{draft?'丢弃录音':'取消录音'}}</button>
    </div>
   </template>
  </section>
  <p v-if="!samples.length" class="empty-small">还没有声音样本。</p>
  <details v-for="s in samples" :key="s.id" class="sample-entry"><summary>{{s.name}} <small>{{s.format.toUpperCase()}} · {{s.durationSeconds.toFixed(2)}} 秒 · {{(s.bytes/1000000).toFixed(2)}} MB</small></summary><p class="hint">来源：{{sources[s.source]}} · {{new Date(s.created).toLocaleString()}}</p><audio :src="'/app/mimotts/api/samples/'+s.id+'/audio'" controls preload="none" :aria-label="'试听样本 '+s.name"></audio><label>样本名<input v-model="names[s.id]" :aria-label="'重命名 '+s.name" maxlength="160"></label><div class="sample-actions"><button class="quiet" :disabled="working" @click="rename(s)">重命名</button><button :disabled="working" @click="emit('select',s.id)">用于克隆</button><button class="quiet danger" :disabled="working" @click="remove(s)">删除样本</button></div></details>
 </section>
</template>
<style scoped>
.sample-recorder { margin-top: 18px; padding-top: 14px; border-top: 1px solid var(--sample-border); }
.sample-recorder h3 { margin: 0; font-size: 14px; }
.sample-recorder audio { margin-bottom: 8px; }
</style>
