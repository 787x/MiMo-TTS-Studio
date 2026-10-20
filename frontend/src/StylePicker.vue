<script setup lang="ts">
import {computed,onUnmounted,ref,watch} from 'vue'
import {isSinging,nameError,searchStyles,styleText,stylePrefix,tagsError,type StyleState} from './styles'
const props=defineProps<{tags:string;singing:boolean;mode:string;state:StyleState;validate:(tags:string,singing:boolean)=>Promise<void>}>()
const emit=defineEmits<{change:[tags:string,singing:boolean]}>()
const query=ref(''),custom=ref(''),error=ref(''),notice=ref(''),pending=ref(false)
const groups=computed(()=>searchStyles(query.value))
const prefix=computed(()=>stylePrefix(props.tags,props.singing))
let disposed=false
onUnmounted(()=>{disposed=true})
// External/manual changes are opaque strings, never tokenized into selected names.
watch(()=>props.tags,value=>{if(value!==styleText(props.state)){
 props.state.manual=value;props.state.selected=[];error.value=''
 notice.value='当前完整值已保留为手动文本；未按空格拆分。可继续追加选择。'
}}, {immediate:true})
async function add(name:string){
 if(pending.value)return
 error.value='';notice.value=''
 const invalid=nameError(name);if(invalid){error.value=invalid;return}
 const singing=isSinging(name)
 if(singing&&props.mode!=='preset'){error.value='唱歌仅适用于预置音色。';return}
 if(!singing&&props.state.selected.includes(name))return
 if(!singing&&props.state.selected.length>=16){error.value='最多选择 16 项，请先移除一项。';return}
 // An opaque manual value cannot safely be split (a name may contain spaces).
 if(!singing&&(' '+props.state.manual+' ').includes(' '+name+' ')){
  error.value='手动文本已包含这个名称，请在上方完整输入框中编辑，避免重复添加。';return
 }
 const selected=singing?[...props.state.selected]:[...props.state.selected,name]
 const tags=styleText({manual:props.state.manual,selected}),nextSinging=singing?true:props.singing
 const invalidTags=tagsError(tags);if(invalidTags){error.value=invalidTags;return}
 const before=JSON.stringify([props.tags,props.singing])
 pending.value=true
 try{
  await props.validate(tags,nextSinging)
  if(disposed)return
  if(before!==JSON.stringify([props.tags,props.singing])){error.value='输入已更改，请重新添加。';return}
  props.state.selected=selected;emit('change',tags,nextSinging);custom.value=''
 }catch(e){error.value=e instanceof Error?e.message:'无法添加，请检查请求预算。'}finally{pending.value=false}
}
function remove(name:string){
 error.value='';notice.value=''
 if(isSinging(name)){emit('change',props.tags,false);return}
 props.state.selected=props.state.selected.filter(t=>t!==name);emit('change',styleText(props.state),props.singing)
}
function toggle(name:string){if(isSinging(name)?props.singing:props.state.selected.includes(name))remove(name);else void add(name)}
function clear(){props.state.manual='';props.state.selected=[];error.value='';notice.value='';emit('change','',false)}
</script>

<template>
 <div class="style-picker">
  <p class="style-prefix">实际整体前缀：<code>{{prefix||'（空）'}}</code></p>
  <div class="style-selected" role="group" aria-label="已选整体风格">
   <span v-if="state.manual" class="style-manual">手动文本：{{state.manual}}（整体保留）</span>
   <button v-for="tag in state.selected" :key="tag" type="button" class="style-chip" :aria-label="'移除风格 '+tag" @click="remove(tag)">{{tag}} ×</button>
   <button v-if="singing" type="button" class="style-chip" aria-label="关闭唱歌模式" @click="remove('唱歌')">唱歌 ×</button>
   <button type="button" class="quiet" :disabled="!tags&&!singing" @click="clear">清空整体风格</button>
  </div>
  <details class="style-presets">
   <summary>浏览预置 / 添加自定义风格</summary>
   <label>搜索名称或类别<input v-model="query" type="search" placeholder="例如：情绪、温柔、方言"></label>
   <div class="style-categories">
    <div v-for="group in groups" :key="group.name" class="style-category"><h3>{{group.name}}</h3><div class="style-buttons">
     <button v-for="tag in group.tags" :key="tag" type="button" :aria-pressed="isSinging(tag)?singing:state.selected.includes(tag)" :disabled="pending||(isSinging(tag)&&mode!=='preset')" @click="toggle(tag)">{{tag}}</button>
    </div></div>
    <p v-if="!groups.length" class="hint">没有匹配的预置，仍可添加自定义风格。</p>
   </div>
   <form class="style-custom" @submit.prevent="add(custom)"><label>自定义风格名称<textarea v-model="custom" rows="1" placeholder="上海话、新闻播报、说书腔…" @keydown.enter.prevent="!$event.isComposing && add(custom)"></textarea></label><button type="submit" :disabled="pending">{{pending?'校验中…':'添加风格'}}</button></form>
   <p class="hint">每项 1–64 个 UTF-16 字符，最多选择 16 项；按添加顺序组合，精确区分大小写和 Unicode，不自动规范化。手动文本不拆分；编辑上方输入框会将完整值转为手动文本。添加前仅在本机校验请求，不调用 MiMo。唱歌使用独立开关。</p>
  </details>
  <p v-if="error||tagsError(tags)" class="style-error" role="alert">{{error||tagsError(tags)}}</p>
  <p v-if="notice" class="hint" role="status">{{notice}}</p>
 </div>
</template>
