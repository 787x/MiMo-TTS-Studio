<script setup lang="ts">
import ProbeButton from './ProbeButton.vue'
import {ref,reactive,watch,onMounted,onUnmounted} from 'vue'
import type {VoiceSample} from './samples'
const props=defineProps<{api:(path:string,method?:string,data?:unknown,signal?:AbortSignal)=>Promise<any>;samples:VoiceSample[];ttsDirty:boolean;llmDirty:boolean}>()
const emit=defineEmits<{change:[value:{states:Record<string,{state:string;message:string}>;busy:boolean}]}>()
const services=[{id:'preset',name:'MiMo 预置音色'},{id:'design',name:'MiMo VoiceDesign'},{id:'clone',name:'MiMo VoiceClone'},{id:'llm',name:'LLM 创作服务'}]
const rows=reactive<Record<string,any>>(Object.fromEntries(services.map(s=>[s.id,{state:'untested',check:null,result:null,error:''}])))
const labels:Record<string,string>={untested:'未检测',ready:'配置就绪',running:'检测中',canceling:'正在取消',succeeded:'成功',failed:'失败',canceled:'取消',expired:'过期'}
const sampleId=ref(''),open=ref(false),active=ref('')
let runId='',disposed=false,epoch=0,controller:AbortController|undefined,timer:ReturnType<typeof setInterval>|undefined,checking=false
const dirty=(id:string)=>id==='llm'?props.llmDirty:props.ttsDirty
const input=(id:string)=>({service:id,sampleId:id==='clone'?sampleId.value||null:null})
async function precheck(){
 if(checking||(!open.value&&!active.value&&!Object.values(rows).some(row=>row.result)))return;checking=true
 const version=epoch
 try{for(const s of services){const value=await props.api('/diagnostics/check','POST',input(s.id));if(disposed||version!==epoch)return;const row=rows[s.id];if(row.check&&row.check.revision!==value.revision){if(!['untested','ready'].includes(row.state))row.state='expired';row.result=null;if(active.value===s.id)stop(true)}row.check=value;if(row.state==='untested'&&value.ready)row.state='ready'}}catch{if(!disposed&&version===epoch)for(const row of Object.values(rows))row.error='预检查失败，请重新打开服务检测。'}finally{checking=false}
}
async function stop(expired=false){
 const version=++epoch,id=active.value,abort=controller
 if(!id)return
 rows[id].state=expired?'expired':'canceling'
 let confirmed=true
 try{if(runId){const result=await props.api('/diagnostics/'+runId+'/cancel','POST',undefined,new AbortController().signal);if(!result.canceled&&!expired)rows[id].error='检测已结束或取消未被接收；已丢弃旧结果。'}}
 catch{confirmed=false;if(!disposed&&!expired)rows[id].error='取消确认失败；请求连接将关闭，请稍后重新预检查。'}
 finally{abort?.abort();if(version===epoch){active.value='';runId='';if(!expired)rows[id].state=confirmed?'canceled':'failed'}}
}
function invalidate(id:string){const row=rows[id];row.state=['untested','ready'].includes(row.state)?'untested':'expired';row.result=null;if(active.value===id)stop(true)}
watch(()=>props.ttsDirty,()=>{for(const id of ['preset','design','clone'])invalidate(id)})
watch(()=>props.llmDirty,()=>invalidate('llm'))
watch(sampleId,()=>{invalidate('clone');void precheck()})
watch(()=>props.samples,()=>{invalidate('clone');void precheck()},{deep:true})
watch(open,value=>{if(value)void precheck();else stop(true)})
async function run(id:string){
 if(active.value||dirty(id))return
 const row=rows[id],version=++epoch
 row.error='';row.result=null;row.state='running';active.value=id
 controller=new AbortController();const signal=controller.signal
 try{
  const check=await props.api('/diagnostics/check','POST',input(id),signal)
  if(disposed||version!==epoch)return
  row.check=check
  if(!check.ready){row.state='failed';row.error=check.reason;return}
  const request={...input(id),revision:check.revision,id:Array.from(crypto.getRandomValues(new Uint8Array(16)),n=>n.toString(16).padStart(2,'0')).join('')}
  runId=request.id
  const result=await props.api('/diagnostics/run','POST',request,signal)
  if(!disposed&&version===epoch){row.result=result;row.state=result.state}

 }catch(e){if(!disposed&&version===epoch){row.state=signal.aborted?'canceled':'failed';row.error=signal.aborted?'已请求取消，等待服务确认。':String(e)}}
 finally{if(version===epoch){active.value='';runId=''}}
}
function cancel(){void stop()}
defineExpose({run,cancel})
watch([rows,active],()=>emit('change',{busy:!!active.value,states:Object.fromEntries(Object.entries(rows).map(([id,row])=>[id,{state:row.state,message:row.error||row.result?.reason||row.check?.reason||''}]))}),{deep:true,immediate:true})
onMounted(()=>{timer=setInterval(()=>void precheck(),2000)})
onUnmounted(()=>{disposed=true;stop(true);clearInterval(timer)})
</script>
<template><section class="settings card diagnostics"><details :open="open" @toggle="open=($event.target as HTMLDetailsElement).open"><summary>服务检测 · MiMo / LLM</summary><p>配置预检查仅在本机进行。点击「服务检测」发送固定短文本，不自动重试；不保存音频、不改变工作台。</p><div v-for="service in services" :key="service.id" class="sample-entry"><strong>{{service.name}}</strong><label v-if="service.id==='clone'">本次检测参考样本（选择并点击检测即授权上传）<select v-model="sampleId"><option value="">不选择，不上传</option><option v-for="s in samples" :key="s.id" :value="s.id">{{s.name}} · {{s.format}} · {{s.durationSeconds.toFixed(2)}} 秒</option></select></label><p>{{rows[service.id].check?.target||'尚未预检查'}}</p><p>{{rows[service.id].check?.reason}}</p><p v-if="dirty(service.id)" class="old">相关配置尚未保存，请先保存再检测；旧结果已过期。</p><p role="status">{{labels[rows[service.id].state]}}<template v-if="rows[service.id].result"> · {{rows[service.id].result.elapsedMs}} ms · {{new Date(rows[service.id].result.testedAt).toLocaleString()}} · {{rows[service.id].result.category}}<br>{{rows[service.id].result.reason}}</template></p><p v-if="rows[service.id].error" role="alert">{{rows[service.id].error}}</p><ProbeButton label="服务检测" :service="service.name" :state="rows[service.id].state" :message="rows[service.id].error||rows[service.id].result?.reason||rows[service.id].check?.reason" :busy="!!active" :disabled="dirty(service.id)||!rows[service.id].check?.ready" @probe="run(service.id)"/><button v-if="active===service.id" class="quiet" @click="cancel">取消检测</button></div></details></section></template>
<style scoped>.diagnostics summary{cursor:pointer}.diagnostics p{overflow-wrap:anywhere}.diagnostics button{margin-right:8px}</style>
