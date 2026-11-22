import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import SampleLibrary from '../src/SampleLibrary.vue'

let library, fetchSample, getUserMedia, releaseMic, decodeAudioData

function button(label) {
  return library.findAll('button').find(control => control.text() === label)
}

beforeEach(() => {
  releaseMic = vi.fn()
  getUserMedia = vi.fn().mockResolvedValue({ getTracks: () => [{ stop: releaseMic }] })
  decodeAudioData = vi.fn().mockResolvedValue({
    length: 6_000, numberOfChannels: 1, getChannelData: () => new Float32Array(6_000).fill(0.5),
  })
  fetchSample = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ id: 'recorded' }) })
  vi.stubGlobal('isSecureContext', true)
  vi.stubGlobal('navigator', { mediaDevices: { getUserMedia } })
  vi.stubGlobal('OfflineAudioContext', class { decodeAudioData = decodeAudioData })
  vi.stubGlobal('MediaRecorder', class {
    state = 'inactive'
    mimeType = 'audio/webm'
    start() { this.state = 'recording' }
    stop() {
      this.state = 'inactive'
      queueMicrotask(() => {
        this.ondataavailable({ data: new Blob(['synthetic recording']) })
        this.onstop()
      })
    }
  })
  vi.stubGlobal('fetch', fetchSample)
})

afterEach(() => {
  library?.unmount()
  library = undefined
  vi.unstubAllGlobals()
})

function openLibrary() {
  library = mount(SampleLibrary, { props: { samples: [], csrf: 'test-csrf', api: vi.fn() } })
}

it('previews a WAV locally, then explicitly saves it and selects it for cloning', async () => {
  openLibrary()
  await button('开始录音').trigger('click')
  await flushPromises()
  await button('停止录音').trigger('click')
  await flushPromises()
  expect(releaseMic).toHaveBeenCalledOnce()
  expect(library.get('audio').attributes('src')).toMatch(/^blob:/)
  expect(fetchSample).not.toHaveBeenCalled()
  await library.get('.sample-recorder input').setValue('录音验收')
  await library.get('.sample-recorder form').trigger('submit')
  await flushPromises()
  expect(fetchSample).toHaveBeenCalledOnce()
  const [path, request] = fetchSample.mock.calls[0]
  expect(path).toBe('/app/mimotts/api/samples/upload')
  expect(request.headers).toMatchObject({
    'X-Studio-CSRF': 'test-csrf',
    'X-Sample-Format': 'wav',
    'X-Sample-Source': 'recording',
  })
  const wave = new DataView(await request.body.arrayBuffer())
  expect(wave.getUint32(0)).toBe(0x52494646)
  expect(wave.getUint16(22, true)).toBe(1)
  expect(wave.getUint32(24, true)).toBe(24_000)
  expect(wave.getUint32(40, true)).toBe(12_000)
  expect(wave.getInt16(44, true)).toBe(16_384)
  expect(library.emitted('select')).toEqual([['recorded']])
  expect(library.find('audio').exists()).toBe(false)
})

it('releases a microphone granted after cancelling permission and never saves it', async () => {
  let grant
  getUserMedia.mockReturnValue(new Promise(resolve => { grant = resolve }))
  openLibrary()
  await button('开始录音').trigger('click')
  await button('取消录音').trigger('click')
  grant({ getTracks: () => [{ stop: releaseMic }] })
  await flushPromises()
  expect(releaseMic).toHaveBeenCalledOnce()
  expect(button('开始录音')).toBeDefined()
  expect(decodeAudioData).not.toHaveBeenCalled()
  expect(fetchSample).not.toHaveBeenCalled()
})

it('keeps WAV/MP3 upload available over HTTP without asking for a microphone', () => {
  vi.stubGlobal('isSecureContext', false)
  openLibrary()
  expect(library.text()).toContain('需要受信任的 HTTPS')
  expect(library.find('input[type=file]').exists()).toBe(true)
  expect(button('开始录音')).toBeUndefined()
  expect(getUserMedia).not.toHaveBeenCalled()
})
