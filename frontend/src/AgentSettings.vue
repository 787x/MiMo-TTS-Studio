<script setup lang="ts">
import {onMounted,ref} from 'vue'
const props=defineProps<{api:(path:string,method?:string,data?:unknown)=>Promise<any>}>()
type Grant={id:string;name:string;owner:string;scopes:string[];created:string}
const grants=ref<Grant[]>([]),name=ref(''),scopes=ref(['read','generate','manage']),busy=ref(false),message=ref('')
const labels:Record<string,string>={read:'读取与预览',generate:'云端生成 / 取消（可能计费）',manage:'保存 / 导入 / 重命名',export:'NAS 授权目录导出',configure:'修改 Agent 默认值 / 预设',delete:'删除私有数据 / 预设'}
async function refresh(){grants.value=await props.api('/agents')}
async function create(){if(busy.value)return;busy.value=true;message.value='';try{const grant=await props.api('/agents','POST',{name:name.value,scopes:scopes.value});name.value='';await refresh();message.value=`授权已创建：${grant.id}。连接凭据仅保存在 NAS 应用私有 agent 目录，按 CLI/MCP 文档由受控包身份使用。`}catch{message.value='创建失败，请检查名称、权限或服务状态。'}finally{busy.value=false}}
async function revoke(id:string){if(busy.value)return;busy.value=true;try{await props.api('/agents/'+id,'DELETE');await refresh();message.value='授权已撤销；之后的新操作将被拒绝。已提交的后台任务仍可在 Web 管理。'}catch{message.value='撤销失败，请刷新后重试。'}finally{busy.value=false}}
onMounted(()=>refresh().catch(()=>message.value='无法读取 Agent 授权。'))
</script>
<template><section class="card agent-settings"><h2>CLI / MCP · Agent 授权</h2><p>授权绑定当前 fnOS 管理员，只允许通过受控身份切换运行的应用包用户连接。系统 UID 权限与下列业务权限共同生效；同 UID 恶意进程不在隔离保证内。云端生成会发送正文、指导及明确选定的克隆样本，可能计费。</p><form @submit.prevent="create"><label>授权名称<input v-model="name" maxlength="80" required></label><div class="scopes"><label v-for="(label,scope) in labels" :key="scope" class="check"><input v-model="scopes" type="checkbox" :value="scope" :disabled="scope==='read'">{{label}}</label></div><button :disabled="busy||!name.trim()">创建授权</button></form><ul><li v-for="grant in grants" :key="grant.id"><strong>{{grant.name}}</strong> · {{grant.scopes.join(', ')}}<br><code>{{grant.id}}</code> · fnOS 用户 {{grant.owner}} <button type="button" class="quiet danger" :disabled="busy" @click="revoke(grant.id)">撤销授权</button></li></ul><p role="status">{{message}}</p></section></template>
<style scoped>.agent-settings{margin-bottom:24px}.scopes{display:flex;flex-wrap:wrap;gap:12px;margin:12px 0}.agent-settings li{margin:12px 0;overflow-wrap:anywhere}.agent-settings p{line-height:1.7}</style>
