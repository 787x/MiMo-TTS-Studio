<script setup lang="ts">
withDefaults(defineProps<{state?:string;message?:string;busy?:boolean;disabled?:boolean;service:string;label?:string}>(),{state:'untested',message:'',busy:false,disabled:false,label:'检测服务'})
defineEmits<{probe:[]}>()
const labels:Record<string,string>={succeeded:'成功',failed:'失败',running:'检测中',canceling:'取消中',expired:'已过期',canceled:'已取消'}
</script>
<template><button type="button" class="quiet probe-button" :class="{'probe-success':state==='succeeded','probe-failed':state==='failed'}" :disabled="disabled||busy" :aria-label="service+' · '+label+' · '+(labels[state]||'未检测')" :title="disabled?'相关设置尚未保存，请先保存再检测。':(message||'使用已保存配置执行服务检测。')" @click="$emit('probe')">{{state==='running'?'检测中…':state==='canceling'?'取消中…':state==='succeeded'?'检测成功':state==='failed'?'检测失败':label}}</button></template>
<style scoped>
.probe-button{white-space:nowrap}.probe-success{background:var(--notice-bg);color:var(--success);border-color:var(--success)}.probe-success:hover{background:var(--notice-bg)}.probe-failed{background:var(--error-bg);color:var(--error-text);border-color:var(--error-text)}.probe-failed:hover{background:var(--error-bg)}
</style>
