<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, shallowRef } from 'vue'
import { sha256 } from '@noble/hashes/sha2.js'
import { bytesToHex } from '@noble/hashes/utils.js'
import NasExport from './NasExport.vue'
import { startPolling } from './polling'

type Work = { id: string; title: string; state: string; bytes: number; sha256: string | null; total: number; completed: number; chapters: number; volumes: number; error: string | null }
type Page<T> = { items: T[]; nextOffset: number | null }
type Segment = { number: number; offset: number; chapter: number; text: string }
type Preview = Page<Segment> & { user: string; prefix: string; model: string; voice: string | null; sourceSampleName: string | null; segmentCharacters: number }
type Chapter = { chapter: number; first: number; title: string }
type Volume = { number: number; first: number; last: number; chapter: number; bytes: number }

const props = defineProps<{
  api: (path: string, method?: string, data?: unknown, signal?: AbortSignal) => Promise<any>
  base: string
  csrf: string
  configured: boolean
  parameters: { text: string; synthesisMode: string; split: boolean }
}>()
const paths = '/works/'
const chunkBytes = 64 * 1024
const title = ref('')
const pendingKey = 'mimo-work-create'
function readPending() {
  try {
    const id = sessionStorage.getItem(pendingKey)
    return id && /^[a-f0-9]{32}$/.test(id) ? id : undefined
  } catch { return undefined }
}
const pendingId = ref(readPending())
let pendingTitle: string | undefined
function rememberCreation(id?: string) {
  pendingId.value = id
  if (!id) pendingTitle = undefined
  try {
    if (id) sessionStorage.setItem(pendingKey, id)
    else sessionStorage.removeItem(pendingKey)
  } catch { /* Keep the same ID in this page if storage is unavailable. */ }
}
const selected = ref<Work>()
const works = ref<Page<Work>>({ items: [], nextOffset: null })
const workOffset = ref(0)
const preview = ref<Preview>()
const chapters = ref<Page<Chapter>>()
const volumes = ref<Page<Volume>>()
const chapter = ref<number>()
const volumeOffset = ref(0)
const file = shallowRef<File>()
const pastedInput = shallowRef<Blob>()
const upload = ref(0)
const busy = ref(false)
const error = ref('')
const exportPath = ref('')
const exportChapter = ref<{ path: string; chapter: number }>()
const audio = ref<HTMLAudioElement>()
const playing = ref<number>()
const waitingFor = ref<number>()
const audioSource = ref('')
const lifetime = new AbortController()
const stateLabels: Record<string, string> = {
  uploading: '等待上传', planning: '正在分段', invalid: '正文或参数校验失败', ready: '等待生成',
  running: '正在生成', pausing: '正在完成当前段', paused: '已暂停', canceling: '正在取消',
  canceled: '已取消', failed: '生成中止', uncertain: '请求结果待确认', succeeded: '全部生成完成', damaged: '作品文件损坏',
}

const pathFor = (id: string) => paths + id
const volumeUrl = (id: string, index: number) => props.base + pathFor(id) + '/volumes/' + index

async function guarded(action: () => Promise<void>) {
  if (busy.value) return
  error.value = ''
  busy.value = true
  try { await action() }
  catch (e) { if (!lifetime.signal.aborted) error.value = e instanceof Error ? e.message : '操作失败。' }
  finally { busy.value = false }
}

async function list(offset = workOffset.value) {
  works.value = await props.api(paths + '?offset=' + offset)
  workOffset.value = offset
}
async function select(id: string) {
  const work: Work = await props.api(pathFor(id))
  selected.value = work
  preview.value = undefined
  chapters.value = undefined
  chapter.value = undefined
  volumes.value = undefined
  volumeOffset.value = 0
  playing.value = undefined
  waitingFor.value = undefined
  audioSource.value = ''
  upload.value = work.bytes
  if (work.total) await loadChapters(0)
  if (work.volumes) await loadVolumes(0)
}
async function loadPreview(offset = 0) {
  if (selected.value) preview.value = await props.api(pathFor(selected.value.id) + '/preview?offset=' + offset)
}
async function loadChapters(offset: number) {
  if (selected.value) chapters.value = await props.api(pathFor(selected.value.id) + '/chapters?offset=' + offset)
}
async function loadVolumes(offset: number, signal?: AbortSignal) {
  const id = selected.value?.id
  if (!id) return
  const filter = chapter.value
  const page = await props.api(pathFor(id) + '/volumes?offset=' + offset + (filter === undefined ? '' : '&chapter=' + filter), 'GET', undefined, signal)
  if (id === selected.value?.id && filter === chapter.value) {
    volumes.value = page
    volumeOffset.value = offset
  }
}
async function pickChapter(item?: Chapter) {
  chapter.value = item?.chapter
  await Promise.all([loadVolumes(0), item ? loadPreview(item.first) : Promise.resolve()])
}
function chooseFile(event: Event) {
  file.value = (event.target as HTMLInputElement).files?.[0]
}
function settings() {
  // Omit text before serialization. TXT imports never read through the editor.
  const { text: _, ...rest } = props.parameters
  if (rest.synthesisMode === 'design' || !rest.split) throw Error('请选择预置音色或 VoiceClone，并开启自动分段。')
  return { ...rest, text: '' }
}

async function putChunk(id: string, offset: number, chunk: ArrayBuffer) {
  const result = await fetch(props.base + pathFor(id) + '/text?offset=' + offset, {
    method: 'PUT', credentials: 'same-origin', signal: lifetime.signal,
    headers: { 'X-Studio-CSRF': props.csrf, 'X-Chunk-SHA256': bytesToHex(sha256(new Uint8Array(chunk))) },
    body: chunk,
  })
  if (!result.ok) {
    const problem = await result.json().catch(() => ({}))
    throw Error(problem.error ?? '分块上传中断，请选择同一份正文继续。')
  }
  return await result.json() as Work
}

async function submit(resume = false, paste = false, pasted?: Blob) {
  const controls = settings()
  const input = pasted ?? (!paste ? file.value ?? pastedInput.value : undefined) ?? new Blob([props.parameters.text])
  if (!input.size) throw Error('请先选择 UTF-8 TXT 或在脚本编辑器粘贴正文。')
  let work: Work
  if (resume) {
    if (!selected.value) throw Error('请选择待上传作品。')
    work = await props.api(pathFor(selected.value.id))
  } else {
    const existing = pendingId.value
    const id = existing ?? bytesToHex(crypto.getRandomValues(new Uint8Array(16)))
    pendingTitle ??= title.value.trim() || (!paste && file.value?.name.slice(0, 160)) || '长篇作品'
    rememberCreation(id)
    // The backend returns the original work when this same ID is replayed.
    // A lookup also recovers the server's title after a page reload.
    const recovered: Work | undefined = existing ? await props.api(pathFor(id)).catch(() => undefined) : undefined
    work = recovered ?? await props.api(paths, 'POST', { id, title: pendingTitle })
    await select(work.id)
    await list(0)
  }
  if (work.bytes > input.size) throw Error('请重新选择原始正文文件。')

  const digest = sha256.create()
  let prefixSha256 = bytesToHex(digest.clone().digest())
  for (let offset = 0; offset < input.size;) {
    lifetime.signal.throwIfAborted()
    const end = Math.min(input.size, offset + chunkBytes, offset < work.bytes ? work.bytes : input.size)
    digest.update(new Uint8Array(await input.slice(offset, end).arrayBuffer()))
    offset = end
    if (offset === work.bytes) prefixSha256 = bytesToHex(digest.clone().digest())
  }
  const fullHash = bytesToHex(digest.digest())
  const id = work.id
  if (work.state !== 'uploading') {
    if (pendingId.value === id && ['planning', 'ready'].includes(work.state) &&
        work.bytes === input.size && work.sha256 === fullHash) {
      rememberCreation()
      return
    }
    throw Error('请选择待上传作品。')
  }
  await props.api(pathFor(id) + '/text/bind', 'POST', {
    bytes: input.size, sha256: fullHash, offset: work.bytes, prefixSha256,
  })
  upload.value = work.bytes
  for (let offset = work.bytes; offset < input.size;) {
    lifetime.signal.throwIfAborted()
    const end = Math.min(input.size, offset + chunkBytes)
    const next = await putChunk(id, offset, await input.slice(offset, end).arrayBuffer())
    selected.value = next
    upload.value = next.bytes
    if (next.bytes !== end) throw Error('上传位置已变化，请重新读取服务端进度。')
    offset = end
  }
  selected.value = await props.api(pathFor(id) + '/prepare', 'POST', {
    parameters: controls, bytes: input.size, sha256: fullHash,
  })
  if (pendingId.value === id) rememberCreation()
}

async function prepare() {
  if (!selected.value) return
  const { id, bytes, sha256 } = selected.value
  preview.value = undefined
  chapters.value = undefined
  selected.value = await props.api(pathFor(id) + '/prepare', 'POST', { parameters: settings(), bytes, sha256 })
}

async function control(action: string) {
  const work = selected.value
  if (!work) return
  const confirm = action === 'resume' && work.state === 'uncertain'
  if (confirm && !window.confirm('上一段可能已经计费。仍要重新发送当前段吗？')) return
  selected.value = await props.api(pathFor(work.id) + '/control', 'POST', { action, confirm })
  if (selected.value!.volumes !== work.volumes) await loadVolumes(volumeOffset.value)
}

async function remove() {
  if (!selected.value || !window.confirm('删除此作品、任务记录、原文和私有音频？已经导出的 NAS 文件将保留。')) return
  await props.api(pathFor(selected.value.id), 'DELETE')
  if (pendingId.value === selected.value.id) rememberCreation()
  selected.value = undefined
  audioSource.value = ''
  preview.value = undefined
  chapters.value = undefined
  volumes.value = undefined
  await list(0)
}

async function play(index: number) {
  if (!selected.value) return
  waitingFor.value = undefined
  playing.value = index
  audioSource.value = volumeUrl(selected.value.id, index)
  await playSource()
}
async function playSource() {
  await nextTick()
  try { await audio.value?.play() } catch { /* Native play control remains available. */ }
}
async function latest() {
  if (!selected.value?.completed) return
  waitingFor.value = undefined
  playing.value = undefined
  audioSource.value = props.base + pathFor(selected.value.id) + '/segments/' + (selected.value.completed - 1)
  await playSource()
}
async function ended() {
  const work = selected.value
  if (!work || playing.value === undefined) return
  const next = playing.value + 1
  if (next < work.volumes) await play(next)
  else if (['running', 'pausing'].includes(work.state)) waitingFor.value = next
}
let stopPolling = () => {}
onMounted(() => {
  void guarded(() => list(0))
  stopPolling = startPolling(async signal => {
    const previous = selected.value
    if (busy.value || !previous || !['running', 'planning', 'pausing', 'canceling'].includes(previous.state)) return
    try {
      const work: Work = await props.api(pathFor(previous.id), 'GET', undefined, signal)
      if (selected.value !== previous || busy.value || signal.aborted) return
      selected.value = work
      if (work.total && !previous.total) await loadChapters(0)
      if (work.volumes !== previous.volumes) await loadVolumes(volumeOffset.value, signal)
      if (waitingFor.value !== undefined && waitingFor.value < work.volumes) await play(waitingFor.value)
    } catch (e) {
      if (!signal.aborted) error.value = e instanceof Error ? e.message : '无法刷新作品进度。'
    }
  })
})
onUnmounted(() => { lifetime.abort(); stopPolling() })
function stage(input: Blob) {
  pastedInput.value = input
  error.value = ''
}
defineExpose({ paste: () => guarded(() => submit(false, true)), stage })
</script>

<template>
  <NasExport v-if="exportPath || exportChapter" :asset-id="exportPath" :export-path="exportPath" :chapter="exportChapter" :api="api" @close="exportPath = ''; exportChapter = undefined" />
  <section id="long-works" class="card long-works">
    <div class="section-title"><h2>长篇作品</h2><button class="text-button" :disabled="busy" @click="guarded(() => list(0))">刷新作品</button></div>
    <p class="hint">导入 UTF-8 TXT 或使用编辑器正文。分段采用工作台当前音色、风格和分段设置。启动生成后可关闭页面。</p>
    <div class="controls">
      <label>作品名称<input v-model="title" maxlength="160" placeholder="可使用 TXT 文件名"></label>
      <label>UTF-8 TXT 文件<input type="file" accept=".txt,text/plain" @change="chooseFile"></label>
    </div>
    <div class="actions">
      <button :disabled="busy || !file" @click="guarded(() => submit())">导入并分段</button>
      <button v-if="pastedInput" :disabled="busy" @click="guarded(() => submit(false, true, pastedInput))">用剪贴板正文分段</button>
      <button class="quiet" :disabled="busy || !parameters.text.length" @click="guarded(() => submit(false, true))">用编辑器正文建立作品</button>
      <button v-if="selected?.state === 'uploading'" class="quiet" :disabled="busy" @click="guarded(() => submit(true))">继续上传原文</button>
    </div>
    <p v-if="pendingId" class="hint">还有一次创建或上传待确认。再次提交同一正文会继续这次操作。
      <button class="text-button" :disabled="busy" @click="rememberCreation()">改为建立另一部作品</button>
    </p>
    <p v-if="pastedInput" class="hint">剪贴板正文 {{ pastedInput.size.toLocaleString() }} 字节</p>
    <p v-if="busy && upload" class="hint">已上传 {{ upload.toLocaleString() }} 字节</p>
    <p v-if="error" class="alert" role="alert">{{ error }}</p>
    <div v-if="works.items.length" class="controls">
      <label>打开作品<select :value="selected?.id ?? ''" :disabled="busy" @change="guarded(() => select(($event.target as HTMLSelectElement).value))">
        <option value="" disabled>选择作品</option>
        <option v-if="selected && !works.items.some(item => item.id === selected?.id)" :value="selected.id">{{ selected.title }}</option>
        <option v-for="item in works.items" :key="item.id" :value="item.id">{{ item.title }}</option>
      </select></label>
      <button v-if="workOffset" class="quiet" :disabled="busy" @click="guarded(() => list(Math.max(0, workOffset - 24)))">上一页作品</button>
      <button v-if="works.nextOffset !== null" class="quiet" :disabled="busy" @click="guarded(() => list(works.nextOffset!))">下一页作品</button>
    </div>
    <template v-if="selected">
      <div class="status" role="status">
        <strong>{{ selected.title }} · {{ stateLabels[selected.state] }}</strong>
        <span>{{ selected.bytes.toLocaleString() }} 字节 · {{ selected.chapters.toLocaleString() }} 章 · {{ selected.completed.toLocaleString() }} / {{ selected.total.toLocaleString() }} 段</span>
        <span v-if="selected.completed < selected.total && ['running', 'pausing', 'uncertain', 'failed', 'paused'].includes(selected.state)">当前第 {{ (selected.completed + 1).toLocaleString() }} 段</span>
        <progress v-if="selected.total" :value="selected.completed" :max="selected.total"></progress>
      </div>
      <p v-if="selected.error" class="alert" role="alert">{{ selected.error }}</p>
      <div class="actions">
        <button v-if="selected.state === 'ready'" :disabled="busy || !configured" @click="guarded(() => control('start'))">启动生成</button>
        <button v-if="selected.state === 'running'" :disabled="busy" @click="guarded(() => control('pause'))">暂停</button>
        <button v-if="['paused', 'failed', 'uncertain'].includes(selected.state)" :disabled="busy || !configured" @click="guarded(() => control('resume'))">{{ selected.state === 'uncertain' ? '确认重试本段' : '恢复生成' }}</button>
        <button v-if="['ready', 'running', 'pausing', 'paused', 'failed', 'uncertain'].includes(selected.state)" class="quiet" :disabled="busy" @click="guarded(() => control('cancel'))">取消任务</button>
        <button v-if="['ready', 'invalid'].includes(selected.state)" class="quiet" :disabled="busy" @click="guarded(prepare)">使用当前设置重新分段</button>
        <button v-if="!['planning', 'running', 'pausing', 'canceling'].includes(selected.state)" class="text-button danger" :disabled="busy" @click="guarded(remove)">删除作品</button>
      </div>
      <details v-if="selected.sha256" class="preview"><summary>原文 SHA-256</summary><pre>{{ selected.sha256 }}</pre></details>
      <details v-if="selected.total" class="preview">
        <summary>章节和分段预览</summary>
        <div v-if="chapters" class="long-work-pages">
          <button class="quiet" :disabled="busy" @click="guarded(() => pickChapter())">全部章节</button>
          <button v-for="item in chapters.items" :key="item.chapter" class="quiet" :disabled="busy" @click="guarded(() => pickChapter(item))">{{ item.title }}</button>
          <button v-if="chapters.nextOffset !== null" class="quiet" :disabled="busy" @click="guarded(() => loadChapters(chapters!.nextOffset!))">更多章节</button>
        </div>
        <div class="actions">
          <button class="quiet" :disabled="busy" @click="guarded(() => loadPreview(0))">查看首页</button>
          <button v-if="preview?.nextOffset != null" class="quiet" :disabled="busy" @click="guarded(() => loadPreview(preview!.nextOffset!))">下一页分段</button>
        </div>
        <template v-if="preview">
          <p class="hint">{{ preview.model }} · {{ preview.voice || preview.sourceSampleName }} · 每段目标 {{ preview.segmentCharacters }} 个文本元素</p><p v-if="preview.user" class="hint">{{ preview.user }}</p>
          <div v-for="item in preview.items" :key="item.number"><strong>第 {{ item.number + 1 }} 段 · UTF-8 偏移 {{ item.offset }}</strong><pre>{{ preview.prefix + item.text }}</pre></div>
        </template>
      </details>
      <template v-if="selected.completed && selected.state !== 'damaged'">
        <div class="actions">
          <button v-if="selected.volumes" :disabled="busy" @click="play(0)">从头连续播放</button>
          <button class="quiet" :disabled="busy" @click="latest">试听最新完成段</button>
        </div>
        <audio v-show="audioSource" ref="audio" :src="audioSource || undefined" controls preload="metadata" aria-label="长篇作品试听" @ended="ended"></audio>
        <p v-if="playing !== undefined" class="hint">正在播放第 {{ playing + 1 }} 卷<span v-if="waitingFor !== undefined"> · 等待下一卷</span></p>
        <template v-if="volumes">
          <div v-if="chapter !== undefined" class="actions">
            <span class="hint">第 {{ chapter + 1 }} 章的分卷</span>
            <a v-if="volumes.items.length" class="button quiet" :href="base + pathFor(selected.id) + '/chapters/' + chapter + '/download'" download>下载本章已完成 WAV（TAR）</a>
            <button v-if="volumes.items.length" class="quiet" @click="exportChapter = { path: pathFor(selected!.id), chapter: chapter! }">导出本章已完成分卷</button>
          </div>
          <div v-for="volume in volumes.items" :key="volume.number" class="asset">
            <button class="asset-title" @click="play(volume.number)">第 {{ volume.number + 1 }} 卷 · 第 {{ volume.chapter + 1 }} 章 · 第 {{ volume.first + 1 }}–{{ volume.last + 1 }} 段</button>
            <div class="asset-actions">
              <a :href="volumeUrl(selected.id, volume.number) + '?download=1'" download>下载 WAV</a>
              <button class="text-button" @click="exportPath = pathFor(selected!.id) + '/volumes/' + volume.number + '/export'">导出到 NAS</button>
            </div>
          </div>
          <div class="actions">
            <button v-if="volumeOffset" class="quiet" :disabled="busy" @click="guarded(() => loadVolumes(0))">分卷首页</button>
            <button v-if="volumes.nextOffset !== null" class="quiet" :disabled="busy" @click="guarded(() => loadVolumes(volumes!.nextOffset!))">下一页分卷</button>
          </div>
        </template>
      </template>
    </template>
  </section>
</template>

<style scoped>
.long-works { margin-top: 24px; }
.long-works audio { width: 100%; }
.long-work-pages { display: flex; flex-wrap: wrap; gap: 6px; }
.long-work-pages button { overflow-wrap: anywhere; }
</style>
