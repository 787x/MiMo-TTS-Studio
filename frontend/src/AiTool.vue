<script setup lang="ts">
import {computed,onUnmounted,ref,watch} from 'vue'
const props=defineProps<{draft:any;api:(path:string,method?:string,data?:unknown,signal?:AbortSignal)=>Promise<any>;task:string;label:string}>()
const emit=defineEmits<{apply:[patch:Record<string,string> ]}>()
const invalidated=ref(false)
const pending=ref(false),error=ref(''),candidate=ref<any>(null),snapshot=ref(''),before=ref<Record<string,string>|null>(null),applied=ref('')
let revision=0,disposed=false,controller:AbortController|undefined
const fingerprint=()=>JSON.stringify(props.draft)
watch(()=>JSON.stringify([props.draft,props.task]),()=>{revision++;invalidated.value=true;controller?.abort()}, {flush:'sync'})
onUnmounted(()=>{disposed=true;revision++;controller?.abort()})
const current=computed(()=>!invalidated.value&&snapshot.value===fingerprint())
const fields=()=>props.task==='description'?['voiceDescription']:props.task==='style'?['style']:props.task==='director'?['character','scene','direction']:['text']
async function generate(){if(pending.value)return;const token=++revision;pending.value=true;error.value='';candidate.value=null;snapshot.value=fingerprint();invalidated.value=false;controller=new AbortController();try{const value=await props.api('/creative','POST',{task:props.task,draft:JSON.parse(snapshot.value)},controller.signal);if(!disposed&&token===revision&&current.value)candidate.value=value}catch(e){if(!disposed)error.value=controller.signal.aborted?'已请求取消或草稿已改变；原稿未变。服务端释放请求前会拒绝新请求。':String(e)}finally{pending.value=false}}
function apply(){if(!current.value){error.value='草稿已变化，请重新生成。';return}const patch:Record<string,string>={};before.value=Object.fromEntries(fields().map(f=>[f,props.draft[f]]));if(props.task==='director')Object.assign(patch,{character:candidate.value.role,scene:candidate.value.scene,direction:candidate.value.direction});else patch[fields()[0]!]=candidate.value.text;emit('apply',patch);candidate.value=null;applied.value=JSON.stringify({...props.draft,...patch})}
function restore(){if(applied.value!==fingerprint()){error.value='应用后草稿已变化，无法恢复覆盖当前编辑。';return}if(before.value)emit('apply',before.value);before.value=null}
</script>
<template><div class="ai-tool"><button type="button" class="quiet" :disabled="pending" @click="generate">{{pending?'AI 正在创作…':label}}</button><button v-if="pending" type="button" class="quiet" @click="revision++;controller?.abort()">取消 AI</button><button v-if="before" type="button" class="quiet" @click="restore">恢复应用前内容</button><p v-if="error" role="alert">{{error}}</p><details v-if="candidate" open class="preview"><summary>{{label}} · 候选文字（尚未生成语音）</summary><small>原内容</small><pre>{{Object.fromEntries(fields().map(f=>[f,JSON.parse(snapshot)[f]]))}}</pre><small>候选内容</small><pre>{{candidate.text}}</pre><p v-if="!current">草稿已变化，请重新生成。</p><button type="button" :disabled="!current" @click="apply">应用</button><button type="button" class="quiet" @click="candidate=null">放弃</button></details></div></template>

<style scoped>
.ai-tool{margin:10px 0}.ai-tool>button{padding:6px 10px;font-size:12px;margin:0 6px 4px 0}.ai-tool p{font-size:12px;line-height:1.7;overflow-wrap:anywhere}
</style>
