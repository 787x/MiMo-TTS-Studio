import {mount,flushPromises} from '@vue/test-utils'
import {describe,it,expect,vi} from 'vitest'
import AgentSettings from '../src/AgentSettings.vue'
describe('Agent authorization settings',()=>{
 it('creates explicit owner-bound grant scopes without showing credentials',async()=>{
  const grants=[];const api=vi.fn(async(path,method,data)=>{if(method==='POST'){grants.push({id:'a'.repeat(32),owner:'1000',name:data.name,scopes:data.scopes});return grants[0]}return grants})
  const w=mount(AgentSettings,{props:{api}});await flushPromises()
  expect(api).toHaveBeenCalledWith('/agents');expect(w.find('input[type=checkbox]').attributes('disabled')).toBeDefined()
  await w.find('input[maxlength]').setValue('local Agent');await w.find('form').trigger('submit');await flushPromises()
  expect(api).toHaveBeenCalledWith('/agents','POST',{name:'local Agent',scopes:['read','generate','manage']})
  expect(w.text()).toContain('fnOS 用户 1000');expect(w.text()).toContain('撤销授权');expect(w.find('input[type=password]').exists()).toBe(false)
  await w.find('.danger').trigger('click');await flushPromises();expect(api).toHaveBeenCalledWith('/agents/'+'a'.repeat(32),'DELETE')
 })
 it('does not create authorizations on open or silently add dangerous scopes',async()=>{
  const api=vi.fn(async()=>[]);const w=mount(AgentSettings,{props:{api}});await flushPromises()
  expect(api.mock.calls).toEqual([['/agents']]);expect(w.find('button').attributes('disabled')).toBeDefined()
  const checked=w.findAll('input[type=checkbox]').filter(x=>x.element.checked).map(x=>x.element.value)
  expect(checked).toEqual(['read','generate','manage'])
 })
})
