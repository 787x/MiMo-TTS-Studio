"""fnOS installed PR7 CLI workflow. Run as root to switch to package identity.
Requires an explicit Web-created grant; real cloud calls only with --online.
"""
import argparse,hashlib,json,pathlib,subprocess,time,uuid,wave
p=argparse.ArgumentParser();p.add_argument('--grant',required=True);p.add_argument('--online',action='store_true');args=p.parse_args()
root=pathlib.Path('/var/apps/mimotts');agent=root/'target/agent/MiMoStudio.Agent'
credential=root/'var/agent'/(args.grant+'.credential')
base=['runuser','-u','mimotts','--',str(agent)]
common=['--credential-file',str(credential)]
def cli(tool,*flags,exit=0):
 result=subprocess.run(base+[tool]+common+list(flags),capture_output=True,timeout=300)
 assert result.returncode==exit,(tool,result.returncode,result.stdout.decode(),result.stderr.decode())
 data=json.loads(result.stdout);assert 'secret' not in data and 'base64' not in result.stdout.decode().lower()
 return data['data']
def wait(job):
 deadline=time.time()+240
 while time.time()<deadline:
  state=cli('get_job_status','--id',job)
  if state['state'] not in ('running','canceling'):
   assert state['state']=='succeeded',state
   return state
  time.sleep(.5)
 raise AssertionError('job timeout')
status=cli('get_service_status');assert status['ttsConfigured'] and status['llmConfigured'];print('PASS installed CLI reuses configured services',flush=True)
for service in ('preset','design','llm'):
 check=cli('check_service_configuration','--service',service);assert check['ready']
assert not cli('check_service_configuration','--service','clone')['ready']
cli('save_preset','--name','PR7 narration','--style','温柔自然','--tags','清亮')
preview=cli('preview_synthesis','--text','你好。[轻笑]原文 é👋。','--preset','PR7 narration','--style','')
assert preview['prefix']=='(清亮)' and preview['user']==''
print('PASS installed preview, presets and offline checks',flush=True)
if args.online:
 request='pr7-'+uuid.uuid4().hex
 text='你好，这是独立工作台的代理接口验收。我们一起把文字变成清晰的声音。[轻笑]每一段内容都保留原来的文字，生成后可以从网页找到同一个音频。'
 flags=['--text',text,'--preset','PR7 narration','--mode','combined','--character','讲述者','--scene','电台','--direction','清晰自然','--segment-characters','50','--request-id',request]
 submitted=cli('synthesize_audio',*flags);job=submitted['job']
 replay=cli('synthesize_audio',*flags);assert replay['job']==job and replay['replayed']
 cli('synthesize_audio','--text','忙碌测试',exit=5)
 done=wait(job);assert done['total']>=2
 preset=done['assetId'];cli('save_audio_asset','--id',preset)
 print('PASS real Preset, tags/director, segmentation, CLI exit continues, replay and global busy',preset,flush=True)
 design=cli('synthesize_audio','--text','你好，我是声音设计样本。欢迎开始今天温暖而清晰的故事。','--synthesis-mode','design','--voice-description','成年女性，温暖清晰的普通话，平静自然，声音柔和。','--mode','director','--character','电台主持','--direction','自然讲述','--wait')['assetId']
 cli('save_audio_asset','--id',design)
 sample=cli('create_sample_from_asset','--id',design,'--name','PR7 Agent VoiceDesign 样本')['id']
 print('PASS real VoiceDesign -> private sample',design,sample,flush=True)
 cloned=cli('synthesize_audio','--text','你好，这是复用新设计样本的代理克隆验收。[轻笑]今天的阳光很温柔。','--synthesis-mode','clone','--source-sample-id',sample,'--tags','温柔 清亮','--style','清晰自然','--wait')['assetId']
 cli('save_audio_asset','--id',cloned)
 targets=cli('list_nas_export_targets');target=next(t for t in targets if t['writable'])
 name='PR7-agent-'+uuid.uuid4().hex[:8]+'.wav'
 receipt=cli('export_audio_to_nas','--id',cloned,'--directory-id',target['id'],'--file-name',name)
 cli('export_audio_to_nas','--id',cloned,'--directory-id',target['id'],'--file-name',name,exit=2)
 print('PASS real VoiceClone -> save -> NAS no-overwrite export',cloned,receipt,flush=True)
 # Explicit local WAV transfer is a separate operation, no re-synthesis.
 download=root/'var'/('pr7-download-'+uuid.uuid4().hex+'.wav')
 cli('download_asset','--id',cloned,'--output',str(download))
 with wave.open(str(download),'rb') as wav:assert wav.getframerate()==24000 and wav.getnframes()>0
 assert hashlib.sha256(download.read_bytes()).digest()==hashlib.sha256((root/'var/audio'/cloned/'audio.wav').read_bytes()).digest()
 cli('download_asset','--id',cloned,'--output',str(download),exit=6)
 imported=cli('import_sample','--input',str(download),'--format','wav','--name','PR7 临时导入')['id']
 cli('rename_voice_sample','--id',imported,'--name','PR7 renamed');cli('delete_voice_sample','--id',imported)
 download.unlink()
 print('PASS private WAV download/import/rename/delete; no extra synthesis',flush=True)
 operations=[('generate_voice_description',['--synthesis-mode','design','--voice-description','温暖清晰的成年男声']),('generate_style_instruction',['--style','温柔清晰的电台讲述']),('generate_director_plan',['--mode','director','--character','夜间电台主持','--scene','安静的夜晚','--direction','温柔讲述']),('generate_script',['--text','写两句关于清晨的中文欢迎词。']),('generate_lyrics',['--text','写四句关于春天的简短中文歌词。','--singing','true']),('annotate_inline_tags',['--text','你好。今天阳光很好，我们一起开始新的故事。'])]
 for tool,flags in operations:
  candidate=cli(tool,*flags);assert candidate['text']
  if tool=='generate_director_plan':assert candidate['role'] and candidate['scene'] and candidate['direction']
  print('PASS real LLM',tool,'validated candidate; no draft or TTS change',flush=True)
 for service in ('preset','design','clone','llm'):
  probe=cli('probe_service','--service',service,*(['--sample-id',sample] if service=='clone' else []))
  assert probe['state']=='succeeded',probe
  print('PASS real service probe',service,probe['category'],flush=True)
 report={'preset':preset,'design':design,'sample':sample,'clone':cloned,'export':name,'target':target['id']}
 (root/'var/pr7-verification.json').write_text(json.dumps(report));(root/'var/pr7-verification.json').chmod(0o600)
 print('PASS full real-cloud Agent workflow',flush=True)
