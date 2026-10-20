# Installed Linux lifecycle stress: root orchestrates only, Agent always package identity.
import argparse,json,pathlib,select,subprocess,time,hashlib,uuid
p=argparse.ArgumentParser();p.add_argument("--grant",required=True);p.add_argument("--online",action="store_true");p.add_argument("--baseline");args=p.parse_args()
assert args.online,"Explicit --online required: this test submits billable TTS then cancels/stops it"
r=pathlib.Path('/var/apps/mimotts');grant=args.grant;base=['runuser','-u','mimotts','--',str(r/'target/agent/MiMoStudio.Agent')];common=['--credential-file',str(r/'var/agent'/(grant+'.credential'))]
def cli(tool,*args,exit=0):
 p=subprocess.run(base+[tool]+common+list(args),capture_output=True,timeout=40);assert p.returncode==exit,(tool,p.stdout.decode());return json.loads(p.stdout)['data']
def files():return {p.name for p in (r/'var/audio').iterdir() if p.is_dir() and not p.name.startswith('.')}
before=files();preset=cli('get_preset','--name','PR7 narration')
text='这是用于明确取消和应用停止的临时验收稿件，不会作为成功文件保存。'*8
job=cli('synthesize_audio','--text',text,'--segment-characters','50','--request-id','cancel-'+uuid.uuid4().hex)['job']
cli('cancel_job','--id',job)
for _ in range(100):
 status=cli('get_job_status','--id',job)
 if status['state'] not in ('running','canceling'):break
 time.sleep(.1)
assert status['state']=='canceled' and not status['assetId'] and files()==before
print('PASS real explicit cancellation terminal acknowledgment, zero assets',flush=True)
mcp=subprocess.Popen(base+['mcp']+common,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
def rpc(method,params=None,id=1):
 msg={'jsonrpc':'2.0','method':method}
 if params is not None:msg['params']=params
 if id is not None:msg['id']=id
 mcp.stdin.write(json.dumps(msg).encode()+b'\n');mcp.stdin.flush()
 if id is None:return
 assert select.select([mcp.stdout],[],[],30)[0]
 return json.loads(mcp.stdout.readline())
rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'lifecycle-test','version':'1'}});rpc('notifications/initialized',id=None)
reply=rpc('tools/call',{'name':'synthesize_audio','arguments':{'text':text,'parameters':{'segmentCharacters':50},'requestId':'disconnect-'+uuid.uuid4().hex}})
new=reply['result']['structuredContent']['data']['job'];mcp.stdin.close();mcp.wait(timeout=15)
assert cli('get_job_status','--id',new)['state']=='running'
cli('cancel_job','--id',job);assert cli('get_job_status','--id',new)['state']=='running'
print('PASS real MCP disconnect leaves backend running; old Job cancellation does not affect new Job',flush=True)
subprocess.run(['appcenter-cli','stop','mimotts'],check=True,capture_output=True)
assert not (r/'target/app.sock').exists() and not (r/'target/agent.sock').exists()
state=subprocess.run(['appcenter-cli','status','mimotts'],capture_output=True,text=True).stdout.strip();assert 'stopped' in state
print('PASS application stop during active Job cleans both sockets',flush=True)
try:
 subprocess.run(['appcenter-cli','start','mimotts'],check=True,capture_output=True)
 for _ in range(100):
  if (r/'target/agent.sock').exists() and (r/'target/app.sock').exists():break
  time.sleep(.1)
 assert files()==before and cli('list_jobs')['items']==[]
 assert cli('get_preset','--name','PR7 narration')==preset
 assert cli('get_service_status')['ttsConfigured']
 cli('get_job_status','--id',new,exit=2)
 if args.baseline:
  baseline=json.loads(pathlib.Path(args.baseline).read_text());assert all(hashlib.sha256(pathlib.Path(n).read_bytes()).hexdigest()==h for n,h in baseline.items())
 print('PASS restart preserves Agent presets, Key, assets, samples; no partial successful asset; old Job history unavailable',flush=True)
finally:
 subprocess.run(['appcenter-cli','start','mimotts'],capture_output=True)
