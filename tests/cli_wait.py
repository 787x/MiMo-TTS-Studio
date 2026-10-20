"""Controlled IPC response test of CLI waiting; no Studio or cloud. Run as non-root Linux."""
import json,os,pathlib,socket,struct,subprocess,sys,tempfile,threading,time
assert sys.platform=='linux' and os.getuid()!=0
binary=str(pathlib.Path(sys.argv[1]).resolve())
with tempfile.TemporaryDirectory(prefix='mimo-cli-wait-') as d:
 root=pathlib.Path(d);credential=root/'credential';credential.write_text(json.dumps({'id':'a'*32,'secret':'b'*64}));credential.chmod(0o600)
 path=root/'agent.sock';server=socket.socket(socket.AF_UNIX);server.bind(str(path));path.chmod(0o600);server.listen(4);server.settimeout(.2)
 calls=[];stop=threading.Event()
 def exact(c,n):
  b=b''
  while len(b)<n:
   more=c.recv(n-len(b))
   if not more:raise EOFError()
   b+=more
  return b
 def worker():
  while not stop.is_set():
   try:c,_=server.accept()
   except socket.timeout:continue
   with c:
    request=json.loads(exact(c,struct.unpack('>i',exact(c,4))[0]));calls.append(request['tool'])
    data=json.dumps({'success':True,'code':'ok','data':{'id':'c'*32,'state':'running','completed':0,'total':2,'assetId':None,'error':None}}).encode()
    c.sendall(struct.pack('>i',len(data))+data)
 t=threading.Thread(target=worker);t.start()
 try:
  p=subprocess.run([binary,'get_job_status','--socket',str(path),'--credential-file',str(credential),'--id','c'*32,'--wait','--timeout','1'],capture_output=True,timeout=5)
  assert p.returncode==7 and json.loads(p.stdout)['code']=='timeout'
  assert len(calls)>=1 and set(calls)=={'get_job_status'}
  print('PASS controlled IPC: CLI wait timeout exits 7 and never sends cancellation or resubmission')
 finally:stop.set();t.join(timeout=2);server.close()
