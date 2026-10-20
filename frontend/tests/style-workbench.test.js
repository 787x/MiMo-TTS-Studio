import {mount,flushPromises} from '@vue/test-utils'
import {describe,it,expect,vi,afterEach} from 'vitest'
import App from '../src/App.vue'
const wrappers=[]
afterEach(()=>{wrappers.splice(0).forEach(w=>w.unmount());vi.unstubAllGlobals();vi.useRealTimers()})
const click=async(w,name)=>{await w.findAll('button').find(b=>b.text()===name).trigger('click');await flushPromises()}
async function setup(){
 const calls=[];let generated=false
 vi.stubGlobal('fetch',vi.fn(async(url,options)=>{
  const path=url.replace('/app/mimotts/api',''),body=options.body?JSON.parse(options.body):null;calls.push({path,method:options.method,body})
  if(path==='/jobs'&&body)generated=true
  const result=path==='/session'?{csrf:'test',configured:true,voices:['mimo_default']}:path==='/preview'?{prefix:(body.tags?'('+body.tags+')':''),user:'',segments:[body.text],assistant:[(body.tags?'('+body.tags+')':'')+body.text]}:path==='/jobs'&&body?{id:'job',state:'running',assetId:null,synthesisMode:body.synthesisMode}:path==='/jobs/job'?{id:'job',state:'succeeded',assetId:'result',synthesisMode:'preset'}:path==='/assets'&&generated?[{id:'result',title:'测试结果',voice:'mimo_default',bytes:100,synthesisMode:'preset',parameters:{prefix:'(温柔 清亮 上海话)'}}]:[]
  return {ok:true,status:200,json:async()=>result}
 }))
 const w=mount(App);wrappers.push(w);await flushPromises();return {w,calls}
}
describe('style workbench integration',()=>{
 it('keeps structured selections in independent mode drafts and hides design controls',async()=>{
  const {w,calls}=await setup();await click(w,'温柔');await click(w,'清亮');expect(w.find('input[placeholder="例如：温柔 清亮"]').element.value).toBe('温柔 清亮')
  await click(w,'VoiceClone');await click(w,'平静');expect(w.find('.style-prefix').text()).toContain('(平静)')
  await click(w,'VoiceDesign');expect(w.find('.style-picker').exists()).toBe(false)
  await click(w,'预置音色');expect(w.findAll('.style-chip').map(c=>c.text())).toEqual(['温柔 ×','清亮 ×'])
  await click(w,'VoiceClone');expect(w.findAll('.style-chip').map(c=>c.text())).toEqual(['平静 ×'])
  expect(calls.filter(c=>c.method!=='GET').every(c=>c.path==='/preview')).toBe(true)
 })
 it('generation receives the exact composed Tags and edits stale the existing snapshot',async()=>{
  vi.useFakeTimers();const {w,calls}=await setup();await click(w,'温柔');await click(w,'清亮');await w.find('.style-custom textarea').setValue('上海话');await w.find('.style-custom textarea').trigger('keydown',{key:'Enter'});await flushPromises();
  await click(w,'预览最终请求');expect(w.find('.preview').text()).toContain('(温柔 清亮 上海话)')
  await click(w,'✦ 生成语音');const sent=calls.find(c=>c.path==='/jobs'&&c.method==='POST');expect(sent.body.tags).toBe('温柔 清亮 上海话');expect(sent.body.singing).toBe(false)
  await vi.advanceTimersByTimeAsync(1200);await flushPromises();expect(w.find('.output audio').exists()).toBe(true);expect(w.find('.old').exists()).toBe(false)
  const count=calls.filter(c=>c.path==='/jobs'&&c.method==='POST').length
  await click(w,'温柔');expect(w.find('.old').exists()).toBe(true);expect(calls.filter(c=>c.path==='/jobs'&&c.method==='POST')).toHaveLength(count);expect(w.find('.preview').text()).toContain('输入已更改')
 })
})
