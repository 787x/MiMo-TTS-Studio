import {mount,flushPromises} from '@vue/test-utils'
import {describe,it,expect,vi,afterEach} from 'vitest'
import NasExport from '../src/NasExport.vue'
vi.mock('@trimjs/web-app',()=>({TrimApp:class {isStandaloneWeb=true;ready(){return Promise.resolve()}}}))
const wrappers=[]
afterEach(()=>{wrappers.forEach(w=>w.unmount());wrappers.length=0})
async function setup(list=[{id:'opaque',path:'/vol1/1000/声音',writable:true}]){
 const api=vi.fn(async(path)=>path==='/export/targets'?list:{directory:list[0].path,fileName:'confirmed.wav',bytes:48044})
 const w=mount(NasExport,{props:{assetId:'owned',api}});wrappers.push(w);await flushPromises();return {w,api,list}
}
describe('NAS export',()=>{
 it('shows native authorization guidance and no path textbox when empty',async()=>{const {w,api}=await setup([]);expect(w.text()).toContain('fnOS 应用设置');expect(w.findAll('input')).toHaveLength(1);expect(w.find('form button').attributes('disabled')).toBeDefined();expect(api).toHaveBeenCalledTimes(1)})
 it('confirms exact directory and filename, sends only ID, never creates a TTS job',async()=>{const {w,api}=await setup();await w.find('input').setValue('成品');expect(w.text()).toContain('/vol1/1000/声音 / 成品.wav');await w.find('form').trigger('submit');await flushPromises();expect(api.mock.calls[1].slice(0,3)).toEqual(['/assets/owned/export','POST',{directoryId:'opaque',fileName:'成品'}]);expect(w.text()).toContain('已导出：');expect(w.text()).toContain('源音频保留');expect(w.find('input').element.value).toMatch(/^mimo-.*\.wav$/)})
 it('disables read-only choices and clears revoked cached IDs on refresh',async()=>{const {w,list}=await setup();list[0].writable=false;list[0].reason='没有写权限';await w.findAll('button').find(b=>b.text()==='返回后刷新目录').trigger('click');await flushPromises();expect(w.find('select').element.value).toBe('');expect(w.find('form button').attributes('disabled')).toBeDefined();expect(w.text()).toContain('没有写权限')})
 it('reports collision and preserves filename for correction',async()=>{const {w,api}=await setup();api.mockRejectedValueOnce(Error('目标已有同名文件'));await w.find('input').setValue('same.wav');await w.find('form').trigger('submit');await flushPromises();expect(w.find('[role=alert]').text()).toContain('同名');expect(w.find('input').element.value).toBe('same.wav')})
 it('prevents double submission and closure until copy finishes',async()=>{const {w,api}=await setup();let resolve;api.mockImplementationOnce(()=>new Promise(r=>resolve=r));await w.find('form').trigger('submit');await w.find('form').trigger('submit');expect(api).toHaveBeenCalledTimes(2);expect(w.findAll('button')[0].attributes('disabled')).toBeDefined();resolve({directory:'/target',fileName:'done.wav',bytes:44});await flushPromises();expect(w.text()).toContain('done.wav')})
 it('falls back clearly when no host bridge exists',async()=>{const {w}=await setup();await w.findAll('button').find(b=>b.text()==='前往 fnOS 应用设置授权目录').trigger('click');await flushPromises();expect(w.text()).toContain('当前页面没有 fnOS 宿主桥接');expect(w.text()).toContain('应用中心 → 已安装')})
 it('late response after unmount is discarded and copy request is aborted',async()=>{const {w,api}=await setup();let resolve;api.mockImplementationOnce(()=>new Promise(r=>resolve=r));await w.find('form').trigger('submit');const signal=api.mock.calls[1][3];w.unmount();expect(signal.aborted).toBe(true);resolve({directory:'/target',fileName:'late.wav',bytes:44});await flushPromises()})
})
