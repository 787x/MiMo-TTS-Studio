import { mount, flushPromises } from '@vue/test-utils'
import { afterEach, expect, it, vi } from 'vitest'
import LongWorks from '../src/LongWorks.vue'

afterEach(() => {
  vi.unstubAllGlobals()
  sessionStorage.clear()
})

it('recovers a lost creation response and resumes at the acknowledged cursor', async () => {
  const text = 'a'.repeat(140001)
  let work
  const api = vi.fn(async (path, method, body) => {
    if (path.startsWith('/works/?')) return { items: work ? [work] : [], nextOffset: null }
    if (path === '/works/' && method === 'POST') {
      work = { id: body.id, title: body.title, state: 'uploading', bytes: 0, chapters: 0, completed: 0, total: 0, volumes: 0 }
      throw Error('creation response lost')
    }
    if (path.endsWith('/text/bind')) {
      expect(body.offset).toBe(work.bytes)
      expect(body.sha256).toMatch(/^[a-f0-9]{64}$/)
      expect(body.prefixSha256).toMatch(/^[a-f0-9]{64}$/)
      return { ...work }
    }
    if (path.endsWith('/prepare')) {
      expect(body.bytes).toBe(text.length)
      expect(work.bytes).toBe(text.length)
      return { ...work, state: 'planning' }
    }
    return { ...work }
  })
  const put = vi.fn(async (path, request) => {
    const offset = Number(path.split('offset=')[1])
    expect(offset).toBe(work.bytes)
    const updated = { ...work, bytes: work.bytes + request.body.byteLength }
    work = updated
    if (offset === 65536) throw Error('second chunk response lost')
    return { ok: true, json: async () => updated }
  })
  vi.stubGlobal('fetch', put)
  const wrapper = mount(LongWorks, { props: { api, base: '/api', csrf: 'test', configured: false,
    parameters: { text, synthesisMode: 'preset', split: true } } })
  const submit = async () => {
    await wrapper.findAll('button').find(button => button.text() === '用编辑器正文建立作品').trigger('click')
    await flushPromises()
  }
  try {
    await flushPromises()
    await submit()
    expect(wrapper.get('[role=alert]').text()).toBe('creation response lost')
    expect(sessionStorage.getItem('mimo-work-create')).toBe(work.id)
    await submit()
    expect(wrapper.get('[role=alert]').text()).toBe('second chunk response lost')
    await submit()
    expect(api.mock.calls.filter(([path, method]) => path === '/works/' && method === 'POST')).toHaveLength(1)
    expect(put.mock.calls.map(([path]) => Number(path.split('offset=')[1]))).toEqual([0, 65536, 131072])
    expect(sessionStorage.getItem('mimo-work-create')).toBeNull()
  } finally { wrapper.unmount() }
})

it('uses the same creation ID after a request fails before reaching the server', async () => {
  const createIds = []
  const api = vi.fn(async (path, method, body) => {
    if (path.startsWith('/works/?')) return { items: [], nextOffset: null }
    if (path === '/works/' && method === 'POST') createIds.push(body.id)
    throw Error('offline')
  })
  const wrapper = mount(LongWorks, { props: { api, base: '/api', csrf: 'test', configured: false,
    parameters: { text: '原稿', synthesisMode: 'preset', split: true } } })
  try {
    await flushPromises()
    const submit = wrapper.findAll('button').find(button => button.text() === '用编辑器正文建立作品')
    await submit.trigger('click'); await flushPromises()
    await submit.trigger('click'); await flushPromises()
    expect(createIds).toHaveLength(2)
    expect(createIds[0]).toBe(createIds[1])
  } finally { wrapper.unmount() }
})
