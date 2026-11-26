<script setup lang="ts">
import {computed,onMounted,onUnmounted,ref} from 'vue'
import {sha256} from '@noble/hashes/sha2.js'
import {bytesToHex} from '@noble/hashes/utils.js'
import {TrimApp} from '@trimjs/web-app'
const props=defineProps<{assetId:string;exportPath?:string;chapter?:{path:string;chapter:number};api:(path:string,method?:string,data?:unknown,signal?:AbortSignal)=>Promise<any>}>()
const emit=defineEmits<{close:[]}>()
type Target={id:string;path:string;writable:boolean;reason:string|null}
const targets=ref<Target[]>([]),directoryId=ref(''),fileName=ref(defaultName()),busy=ref(false),loading=ref(false),error=ref(''),receipt=ref(''),guide=ref(false),authorizing=ref(false)
const chosen=computed(()=>targets.value.find(t=>t.id===directoryId.value))
const finalName=computed(()=>fileName.value.toLowerCase().endsWith('.wav')?fileName.value.slice(0,-4)+'.wav':fileName.value+'.wav')
const lifetime=new AbortController();let disposed=false,revision=0
let batchOffset=0,batchCount=0,batchKey='',operationId=''
function batchOperation(chapter:{path:string;chapter:number},directory:string,stem:string){
 const key='mimo-export-'+bytesToHex(sha256(new TextEncoder().encode(JSON.stringify([chapter.path,chapter.chapter,directory,stem]))))
 if(key!==batchKey){
  batchKey=key;batchOffset=0;batchCount=0
  operationId=bytesToHex(crypto.getRandomValues(new Uint8Array(16)))
  try{
   const previous=sessionStorage.getItem(key)
   if(previous&&/^[a-f0-9]{32}$/.test(previous))operationId=previous
   else sessionStorage.setItem(key,operationId)
  }catch{/* Keep the operation ID in this dialog when storage is unavailable. */}
 }
 return operationId
}
async function exportChapter(input:{directoryId:string;fileName:string}){
 const chapter=props.chapter!
 const stem=input.fileName.replace(/\.wav$/i,'')
 const operationId=batchOperation(chapter,input.directoryId,stem)
 let next:number|null=batchOffset
 while(next!==null){
  const page:{items:{number:number}[];nextOffset:number|null}=await props.api(chapter.path+'/volumes?chapter='+chapter.chapter+'&offset='+next,'GET',undefined,lifetime.signal)
  for(const volume of page.items){
   await props.api(chapter.path+'/volumes/'+volume.number+'/export','POST',
    {...input,fileName:stem+'-'+(volume.number+1)+'.wav',operationId},lifetime.signal)
   batchOffset=volume.number+1
   batchCount++
   receipt.value='已导出 '+batchCount+' 卷。'
  }
  next=page.nextOffset
 }
 receipt.value='本章已导出 '+batchCount+' 卷。'
}
let host:TrimApp|undefined
function defaultName(){
 if(props.chapter)return 'mimo-'+props.chapter.path.split('/').at(-1)+'-chapter-'+(props.chapter.chapter+1)+'.wav'
 return 'mimo-'+new Date().toISOString().replace(/[-:]/g,'').slice(0,15)+'-'+crypto.getRandomValues(new Uint32Array(1))[0]!.toString(16).padStart(8,'0')+'.wav'}
async function refresh(){
 const request=++revision;loading.value=true;error.value=''
 try{const list:Target[]=await props.api('/export/targets','GET',undefined,lifetime.signal);if(disposed||request!==revision)return;targets.value=list;if(!list.some(t=>t.id===directoryId.value&&t.writable))directoryId.value=list.find(t=>t.writable)?.id||''}
 catch(e){if(!disposed&&request===revision){targets.value=[];directoryId.value='';error.value=e instanceof Error?e.message:'目录刷新失败'}}
 finally{if(!disposed&&request===revision)loading.value=false}
}
async function platform(action:'pick'|'settings'){
 if(authorizing.value)return;authorizing.value=true;guide.value=true;error.value=''
 let timer:ReturnType<typeof setTimeout>|undefined
 try{
  const sdk=host??(host=new TrimApp())
  await Promise.race([sdk.ready(),new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('当前宿主未响应，请按下面步骤在 fnOS 应用设置授权。')),8000)})])
  clearTimeout(timer)
  if(sdk.isStandaloneWeb)throw Error('当前页面没有 fnOS 宿主桥接，请按下面步骤前往应用设置授权。')
  const response=action==='pick'?await sdk.pickSharedFile({title:'选择 MiMo 导出目录',okText:'确认授权',creatable:false,sidebarGroup:['myFiles','otherShare']}):await sdk.openAppSetting()
  if(action==='pick'&&response&&typeof response==='object'&&'code' in response&&response.code!==0)throw Error('fnOS 未完成目录授权，请到应用设置检查权限后刷新。')
  if(!disposed)await refresh()
 }catch(e){if(!disposed)error.value=e instanceof Error?e.message:'无法打开授权页面，请使用 fnOS 应用设置。'}
 finally{clearTimeout(timer);if(!disposed)authorizing.value=false}
}
async function save(){
 if(busy.value||loading.value||!chosen.value?.writable)return
 busy.value=true;error.value='';receipt.value=''
 try{
  const input={directoryId:directoryId.value,fileName:fileName.value}
  if(props.chapter)await exportChapter(input)
  else {
   const result=await props.api(props.exportPath??('/assets/'+props.assetId+'/export'),'POST',input,lifetime.signal)
   if(!disposed){receipt.value='已导出：'+result.directory+'/'+result.fileName+'（'+result.bytes+' 字节）。私有源音频保留。';fileName.value=defaultName()}
  }
 }
 catch(e){if(!disposed)error.value=(e instanceof Error?e.message:'导出失败，请刷新目录后重试')+(props.chapter&&batchCount?' 已导出 '+batchCount+' 卷，继续操作将从尚未确认的分卷开始。':'')}
 finally{if(!disposed)busy.value=false}
}
onMounted(refresh)
onUnmounted(()=>{disposed=true;revision++;lifetime.abort()})
</script>

<template>
 <div class="nas-overlay" @keydown.esc="!busy&&emit('close')">
  <section class="card nas-panel" role="dialog" aria-modal="true" aria-labelledby="nas-title" tabindex="-1">
   <div class="section-title"><h2 id="nas-title">导出到 NAS</h2><button type="button" class="quiet" :disabled="busy" @click="emit('close')">关闭</button></div>
   <p class="hint">复制已生成的 WAV，不调用 MiMo。源音频、保存状态和克隆样本保持不变。</p>
   <div class="nas-tools"><button type="button" class="quiet" :disabled="busy||authorizing" @click="platform('pick')">选择并授权文件夹</button><button type="button" class="quiet" :disabled="busy||authorizing" @click="platform('settings')">前往 fnOS 应用设置授权目录</button><button type="button" class="quiet" :disabled="busy||loading" @click="refresh">{{loading?'正在刷新…':'返回后刷新目录'}}</button></div>
   <p v-if="guide||!targets.some(t=>t.writable)" class="hint">在 fnOS 桌面打开「应用中心 → 已安装 → MiMo TTS Studio → 设置 → 授权目录」，添加已有文件夹并授予读写权限，然后返回刷新。若系统提示重启，请先停止并启动本应用。NAS 导出需要 fnOS 1.2.0401 或以上的开放 API。</p>
   <p v-if="error" class="alert" role="alert">{{error}}</p><p v-if="receipt" class="notice" role="status">{{receipt}}</p>
   <form @submit.prevent="save">
    <label>已授权目标文件夹<select v-model="directoryId" :disabled="busy||loading"><option value="" disabled>请选择可写文件夹</option><option v-for="t in targets" :key="t.id" :value="t.id" :disabled="!t.writable">{{t.path}}{{t.reason?' · '+t.reason:''}}</option></select></label>
    <p v-if="!loading&&!targets.length" class="hint">尚无可用授权目录。请先通过 fnOS 授权，不能手动输入宿主机路径。</p>
    <label>{{chapter?'WAV 文件名前缀':'WAV 文件名'}}<input v-model="fileName" :disabled="busy" maxlength="180" required autocomplete="off"></label>
    <p class="nas-destination">目标：{{chosen?.path||'（尚未选择）'}} / {{chapter ? fileName.replace(/\.wav$/i,'')+'-卷号.wav' : finalName}}</p>
    <p class="hint">{{chapter?'网络中断后，使用原目录和文件名前缀继续。只有属于同一次导出的已校验文件才会被跳过。':'同名文件不会覆盖；再次导出会使用新的默认名称。'}}请勿在文件名中填写敏感正文。</p>
    <button :disabled="busy||loading||!chosen?.writable||!fileName.trim()">{{busy?'正在复制，请等待…':chapter?'导出本章分卷':'确认导出 WAV 到 NAS'}}</button>
   </form>
  </section>
 </div>
</template>
