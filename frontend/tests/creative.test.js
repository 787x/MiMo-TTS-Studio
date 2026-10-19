import {mount,flushPromises} from '@vue/test-utils'
import {reactive,nextTick} from 'vue'
import {describe,it,expect,vi} from 'vitest'
import AiTool from '../src/AiTool.vue'
const draft=()=>reactive({text:'原稿',synthesisMode:'preset',style:'',character:'旧角色',scene:'旧场景',direction:'旧指导',sourceSampleId:null})
function setup(task='script',api=vi.fn().mockResolvedValue({text:'候选',role:'新角色',scene:'新场景',direction:'新指导'})){
 const value=draft();const wrapper=mount(AiTool,{props:{draft:value,task,label:'AI 创作',api,onApply:patch=>Object.assign(value,patch)}});return {wrapper,value,api}
}
const click=async(w,label)=>{const button=w.findAll('button').find(b=>b.text()===label);expect(button).toBeTruthy();await button.trigger('click');await flushPromises()}
describe('AI candidate lifecycle',()=>{
 it('requires explicit apply and restores the previous text',async()=>{const {wrapper:w,value,api}=setup();await click(w,'AI 创作');expect(value.text).toBe('原稿');expect(w.text()).toContain('候选');await click(w,'应用');expect(value.text).toBe('候选');await click(w,'恢复应用前内容');expect(value.text).toBe('原稿');expect(api.mock.calls[0][0]).toBe('/creative')})
 it('applies and restores all director fields atomically',async()=>{const {wrapper:w,value}=setup('director');await click(w,'AI 创作');await click(w,'应用');expect([value.character,value.scene,value.direction]).toEqual(['新角色','新场景','新指导']);await click(w,'恢复应用前内容');expect([value.character,value.scene,value.direction]).toEqual(['旧角色','旧场景','旧指导'])})
 it('refuses applying after target changes',async()=>{const {wrapper:w,value}=setup();await click(w,'AI 创作');value.text='新稿';await nextTick();expect(w.findAll('button').find(b=>b.text()==='应用').attributes('disabled')).toBeDefined();expect(value.text).toBe('新稿')})
 for(const change of ['edit','sample','cancel','unmount'])it('drops late response after '+change,async()=>{let resolve;const api=vi.fn(()=>new Promise(r=>resolve=r));const {wrapper:w,value}=setup('script',api);await w.find('button').trigger('click');if(change==='edit')value.text='改稿';if(change==='sample')value.sourceSampleId='different';if(change==='cancel')await click(w,'取消 AI');if(change==='unmount')w.unmount();await nextTick();resolve({text:'迟到结果'});await flushPromises();expect(value.text).not.toBe('迟到结果');if(change!=='unmount')expect(w.text()).not.toContain('候选文字')})
 it('does not overwrite edits on restore',async()=>{const {wrapper:w,value}=setup();await click(w,'AI 创作');await click(w,'应用');value.text='保留编辑';await nextTick();await click(w,'恢复应用前内容');expect(value.text).toBe('保留编辑')})
})
