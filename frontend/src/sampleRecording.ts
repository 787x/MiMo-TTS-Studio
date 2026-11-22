import { onMounted, onUnmounted, ref, shallowRef } from 'vue'

const sampleRate = 24_000
const maxSeconds = 120
const maxEncodedBytes = 8_000_000

export function recordingWave(audio: AudioBuffer): Blob {
  const frames = Math.min(audio.length, maxSeconds * sampleRate)
  if (frames < sampleRate / 5) throw new Error('录音至少需要 0.2 秒，请重新录制。')
  const bytes = new DataView(new ArrayBuffer(44 + frames * 2))
  for (const [offset, tag] of [[0, 'RIFF'], [8, 'WAVEfmt '], [36, 'data']] as const) {
    for (let i = 0; i < tag.length; i++) bytes.setUint8(offset + i, tag.charCodeAt(i))
  }
  bytes.setUint32(4, bytes.byteLength - 8, true)
  bytes.setUint32(16, 16, true)
  bytes.setUint16(20, 1, true)
  bytes.setUint16(22, 1, true)
  bytes.setUint32(24, sampleRate, true)
  bytes.setUint32(28, sampleRate * 2, true)
  bytes.setUint16(32, 2, true)
  bytes.setUint16(34, 16, true)
  bytes.setUint32(40, frames * 2, true)

  const channels = Array.from({ length: audio.numberOfChannels }, (_, i) => audio.getChannelData(i))
  for (let i = 0; i < frames; i++) {
    let sum = 0
    for (const channel of channels) sum += channel[i]
    const sample = Math.max(-1, Math.min(1, sum / channels.length))
    bytes.setInt16(44 + i * 2, Math.round(sample * (sample < 0 ? 32768 : 32767)), true)
  }
  return new Blob([bytes.buffer], { type: 'audio/wav' })
}

export function useSampleRecording() {
  const unavailable = !window.isSecureContext
    ? '浏览器录音需要受信任的 HTTPS。请用 HTTPS 登录 fnOS，或上传 WAV / MP3。'
    : !navigator.mediaDevices?.getUserMedia || !window.MediaRecorder || !window.OfflineAudioContext
      ? '当前浏览器不支持录音，请使用 WAV / MP3 上传。'
      : ''
  const state = ref<'idle' | 'requesting' | 'recording' | 'processing'>('idle')
  const seconds = ref(0)
  const error = ref('')
  const draft = shallowRef<{ wave: Blob; url: string }>()
  let capture: AbortController | undefined
  let stream: MediaStream | undefined
  let recorder: MediaRecorder | undefined
  let timer: number | undefined

  function release() {
    window.clearInterval(timer)
    stream?.getTracks().forEach(track => track.stop())
    stream = undefined
  }

  function clearDraft() {
    if (draft.value) URL.revokeObjectURL(draft.value.url)
    draft.value = undefined
  }

  function discard() {
    capture?.abort()
    capture = undefined
    if (recorder && recorder.state !== 'inactive') recorder.stop()
    recorder = undefined
    release()
    clearDraft()
    state.value = 'idle'
    seconds.value = 0
    error.value = ''
  }

  function fail(message: string) {
    discard()
    error.value = message
  }

  function stop() {
    if (state.value !== 'recording' || !recorder) return
    state.value = 'processing'
    if (recorder.state !== 'inactive') recorder.stop()
    release()
  }

  async function start() {
    if (unavailable || state.value !== 'idle') return
    discard()
    const operation = new AbortController()
    capture = operation
    state.value = 'requesting'
    try {
      const input = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1 } })
      if (operation.signal.aborted) {
        input.getTracks().forEach(track => track.stop())
        return
      }
      stream = input
      const active = new MediaRecorder(input, { audioBitsPerSecond: 96_000 })
      recorder = active
      const chunks: Blob[] = []
      let bytes = 0
      active.ondataavailable = ({ data }) => {
        if (operation.signal.aborted || !data.size) return
        bytes += data.size
        if (bytes > maxEncodedBytes) {
          fail('录音数据超限，请缩短录音后重试。')
          return
        }
        chunks.push(data)
      }
      active.onerror = () => {
        if (!operation.signal.aborted) fail('录音中断，请检查麦克风后重试。')
      }
      active.onstop = async () => {
        if (operation.signal.aborted) return
        release()
        recorder = undefined
        state.value = 'processing'
        try {
          const encoded = await new Blob(chunks, { type: active.mimeType }).arrayBuffer()
          if (operation.signal.aborted) return
          const context = new OfflineAudioContext(1, 1, sampleRate)
          const audio = await context.decodeAudioData(encoded)
          if (operation.signal.aborted) return
          const wave = recordingWave(audio)
          draft.value = { wave, url: URL.createObjectURL(wave) }
          capture = undefined
          state.value = 'idle'
        } catch (e) {
          if (!operation.signal.aborted) {
            fail(e instanceof Error && e.message.startsWith('录音至少') ? e.message : '无法读取录音，请重新录制或上传音频文件。')
          }
        }
      }
      active.start(250)
      state.value = 'recording'
      const started = performance.now()
      timer = window.setInterval(() => {
        const elapsed = (performance.now() - started) / 1000
        seconds.value = Math.min(maxSeconds, Math.floor(elapsed))
        if (elapsed >= maxSeconds) stop()
      }, 100)
    } catch (e) {
      if (operation.signal.aborted) return
      const reason = e instanceof DOMException ? e.name : ''
      fail(reason === 'NotAllowedError'
        ? '麦克风未获授权。请检查站点权限，或在新标签页打开工作台后重试。'
        : '无法开启麦克风，请检查设备连接和系统权限。')
    }
  }

  onMounted(() => window.addEventListener('pagehide', discard))
  onUnmounted(() => {
    window.removeEventListener('pagehide', discard)
    discard()
  })
  return { unavailable, state, seconds, error, draft, start, stop, discard, clearDraft }
}
