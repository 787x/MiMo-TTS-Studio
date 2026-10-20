"""Linux real non-root IPC/CLI/MCP regression; root simulates fnOS gateway only. No cloud calls.
Run: sudo python3 tests/agent_ipc.py [publish-root]
"""
import http.client,json,os,pathlib,pwd,select,signal,socket,struct,subprocess,sys,tempfile,time,wave,io
assert sys.platform=='linux' and os.getuid()==0
repo=pathlib.Path(__file__).resolve().parent.parent
publish=pathlib.Path(sys.argv[1]).resolve() if len(sys.argv)>1 else repo/'artifacts/fpk/app'
account=pwd.getpwnam('nobody');count=0

def check(ok,name):
 global count
 assert ok,name
 count+=1;print('PASS',name,flush=True)
with tempfile.TemporaryDirectory(prefix='mimo-agent-ipc-') as tmp:
 root=pathlib.Path(tmp);root.chmod(0o755)
 for name in ('etc','var','tmp','target','files'):
  d=root/name;d.mkdir();os.chown(d,account.pw_uid,account.pw_gid)
 env={**os.environ,**{'TRIM_'+k:str(root/v) for k,v in [('APPDEST','target'),('PKGETC','etc'),('PKGVAR','var'),('PKGTMP','tmp')]}}
 def drop():os.setgroups([]);os.setgid(account.pw_gid);os.setuid(account.pw_uid)
 backend=subprocess.Popen([str(publish/'server/MiMoStudio')],env=env,preexec_fn=drop,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
 csrf=''
 def web(path,method='GET',body=None,owner='1000',extra=None):
  c=http.client.HTTPConnection('nas.test');c.sock=socket.socket(socket.AF_UNIX);c.sock.settimeout(8);c.sock.connect(str(root/'target/app.sock'))
  h={'Host':'nas.test','Origin':'http://nas.test','X-Trim-Userid':owner,'X-Trim-Username':'admin','X-Trim-Isadmin':'true','X-Studio-CSRF':csrf,'Content-Type':'application/json'};h.update(extra or {})
  c.request(method,'/app/mimotts/api'+path,None if body is None else json.dumps(body),h);r=c.getresponse();b=r.read();s=r.status;c.close();return s,json.loads(b) if b else None
 try:
  for _ in range(100):
   if (root/'target/app.sock').exists() and (root/'target/agent.sock').exists():break
   if backend.poll()!=None:raise RuntimeError(backend.stdout.read().decode())
   time.sleep(.1)
  check((root/'target/agent.sock').stat().st_mode&0o777==0o600,'private Agent socket 0600')
  csrf=web('/session')[1]['csrf']
  check(web('/agents','POST',{'name':'bad','scopes':['read']},extra={'Origin':'http://evil.test'})[0]==403,'grant creation Origin protected')
  status,g=web('/agents','POST',{'name':'PR7 regression','scopes':['read','generate','manage','export','configure','delete']})
  check(status==200 and 'secret' not in json.dumps(g),'grant Web response hides secret')
  credential=root/'var/agent'/(g['id']+'.credential');secret=json.loads(credential.read_text())
  check(credential.stat().st_mode&0o777==0o600,'credential file private')
  cli=[str(publish/'agent/MiMoStudio.Agent')]
  common=['--socket',str(root/'target/agent.sock'),'--credential-file',str(credential)]
  def run(tool,*args,input=None,exit=0):
   p=subprocess.run(cli+[tool]+common+list(args),input=input,capture_output=True,preexec_fn=drop,timeout=20)
   check(p.returncode==exit,'CLI '+tool+' exit '+str(exit))
   r=json.loads(p.stdout);check(b'dummy-private-key' not in p.stdout and secret['secret'].encode() not in p.stdout,'CLI bounded safe JSON '+tool);return r
  run('get_capabilities');run('list_voices');run('get_service_status')
  r=run('preview_synthesis','--stdin','--tags','温柔 清亮',input='原文 é👋\r\n'.encode())
  check(r['data']['assistant']==['(温柔 清亮)原文 é👋\r\n'],'CLI stdin preserves UTF8 graphemes and CRLF')
  run('preview_synthesis','--stdin',input=b'\xff',exit=2)
  run('preview_synthesis','--stdin',input=b'x'*60001,exit=2)
  run('list_jobs','--unexpected','bad',exit=2)
  run('set_agent_defaults','--style','自然');run('save_preset','--name','test','--tags','清亮')
  run('get_preset','--name','test');run('get_agent_defaults')
  r=run('preview_synthesis','--text','正文','--preset','test','--style','')
  check(r['data']['user']=='' and r['data']['prefix']=='(清亮)','real IPC preset precedence')
  audio=io.BytesIO()
  with wave.open(audio,'wb') as f:f.setnchannels(1);f.setsampwidth(2);f.setframerate(24000);f.writeframes(b'\0'*24000)
  file=root/'files/sample.wav';file.write_bytes(audio.getvalue());os.chown(file,account.pw_uid,account.pw_gid);file.chmod(0o600)
  imported=run('import_sample','--input',str(file),'--name','fixture','--format','wav')['data']['id']
  run('preview_synthesis','--text','克隆','--synthesis-mode','clone','--source-sample-id',imported)
  run('rename_voice_sample','--id',imported,'--name','renamed');run('get_voice_sample','--id',imported)
  asset_id='b'*32;asset_dir=root/'var/audio'/asset_id;asset_dir.mkdir(mode=0o700);os.chown(asset_dir,account.pw_uid,account.pw_gid)
  metadata={'id':asset_id,'owner':'1000','title':'private text sentinel','voice':'test','created':'2026-10-20T00:00:00Z','saved':True,'bytes':len(audio.getvalue())}
  for name,data in [('asset.json',json.dumps(metadata).encode()),('audio.wav',audio.getvalue())]:
   destination=asset_dir/name;destination.write_bytes(data);os.chown(destination,account.pw_uid,account.pw_gid);destination.chmod(0o600)
  run('get_audio_asset','--id',asset_id)
  output=root/'files/result.wav';run('download_asset','--id',asset_id,'--output',str(output));check(output.read_bytes()==audio.getvalue(),'bounded download reuses exact committed bytes')
  run('download_asset','--id',asset_id,'--output',str(output),exit=6)
  alias=root/'files/credential-link';alias.symlink_to(credential)
  bad=subprocess.run(cli+['list_jobs','--socket',str(root/'target/agent.sock'),'--credential-file',str(alias)],capture_output=True,preexec_fn=drop)
  check(bad.returncode==3,'credential symlink rejected')
  other=web('/agents','POST',{'name':'other','scopes':['read']},owner='1001')[1]
  common_other=cli+['get_voice_sample','--id',imported,'--socket',str(root/'target/agent.sock'),'--credential-file',str(root/'var/agent'/(other['id']+'.credential'))]
  p=subprocess.run(common_other,preexec_fn=drop,capture_output=True);check(p.returncode==2,'cross-owner sample denied over real IPC')
  # Root does not pass the real-peer check, even with a valid stolen credential.
  s=socket.socket(socket.AF_UNIX);s.connect(str(root/'target/agent.sock'));payload=json.dumps({'version':1,'credential':secret,'tool':'list_jobs','arguments':{}}).encode()
  try:s.sendall(struct.pack('>i',len(payload))+payload);rejected=s.recv(4)==b''
  except ConnectionResetError:rejected=True
  s.close();check(rejected,'wrong Linux peer UID rejected before credential')
  smoke=repo/'mcp-tests/bin/Release/net10.0/McpSmoke.dll'
  official=subprocess.run(['dotnet',str(smoke),'runuser','-u','nobody','--']+cli+['mcp']+common,capture_output=True,timeout=90)
  check(official.returncode==0,'official MCP SDK client initialize/discovery/structured call/error: '+official.stderr.decode()[:150]);print(official.stdout.decode().strip())
  mcp=subprocess.Popen(cli+['mcp']+common,preexec_fn=drop,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
  def rpc(method,params=None,id=1):
   message={'jsonrpc':'2.0','method':method}
   if params is not None:message['params']=params
   if id is not None:message['id']=id
   mcp.stdin.write(json.dumps(message).encode()+b'\n');mcp.stdin.flush()
   if id is None:return
   assert select.select([mcp.stdout],[],[],15)[0],method+' timed out'
   return json.loads(mcp.stdout.readline())
  check('result' in rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'raw-test','version':'1'}}),'raw MCP newline-delimited initialize')
  rpc('notifications/initialized',id=None)
  check('result' in rpc('tools/list'),'raw MCP tools/list stdout protocol only')
  same_text='逐字对比 é👋'
  expected=run('preview_synthesis','--text',same_text,'--tags','温柔 清亮','--style','自然','--mode','combined','--character','旁白')['data']
  actual=rpc('tools/call',{'name':'preview_synthesis','arguments':{'text':same_text,'parameters':{'tags':'温柔 清亮','style':'自然','mode':'combined','character':'旁白'}}})['result']['structuredContent']['data']
  check(expected==actual,'CLI and MCP identical effective Composition for identical parameters')
  rpc('notifications/cancelled',{'requestId':9999,'reason':'test'},id=None)
  check('result' in rpc('tools/list'),'MCP cancellation notification remains standard protocol')
  web('/agents/'+g['id'],'DELETE')
  reply=rpc('tools/call',{'name':'list_jobs','arguments':{}})
  check(reply['result']['isError'] and reply['result']['structuredContent']['code']=='permission_denied','revoked grant denied on existing MCP connection')
  mcp.stdin.close();mcp.wait(timeout=15);check(not mcp.stderr.read(),'MCP logs absent from stdout and stderr')
  run('get_capabilities',exit=3)
 finally:
  backend.terminate();backend.wait(timeout=15)
  check(not (root/'target/agent.sock').exists(),'Agent socket removed on shutdown')
print(count,'Agent IPC/CLI/MCP checks passed')
