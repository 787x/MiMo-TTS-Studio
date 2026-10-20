"""Controlled IPC replies from actual .NET Diagnostics tests -> real CLI and official MCP client.
No cloud. Run as non-root Linux, with the fixtures emitted by Studio.Tests.
"""
import json,os,pathlib,socket,struct,subprocess,sys,tempfile,threading
assert sys.platform=='linux' and os.getuid()!=0
binary=str(pathlib.Path(sys.argv[1]).resolve());fixtures=pathlib.Path(sys.argv[2]).resolve();smoke=pathlib.Path(sys.argv[3]).resolve()
cases=json.loads(fixtures.read_text());states={'preset':'succeeded','design':'failed','clone':'expired','llm':'canceled'}
with tempfile.TemporaryDirectory(prefix='mimo-feedback-') as directory:
 root=pathlib.Path(directory);credential=root/'credential';secret={'id':'a'*32,'secret':'b'*64};credential.write_text(json.dumps(secret));credential.chmod(0o600)
 server=socket.socket(socket.AF_UNIX);path=root/'agent.sock';server.bind(str(path));path.chmod(0o600);server.listen(4);server.settimeout(.2)
 stop=threading.Event();calls=[];errors=[]
 def exact(c,n):
  b=b''
  while len(b)<n:
   chunk=c.recv(n-len(b))
   if not chunk:raise EOFError()
   b+=chunk
  return b
 def worker():
  try:
   while not stop.is_set():
    try:c,_=server.accept()
    except socket.timeout:continue
    with c:
     length=struct.unpack('>i',exact(c,4))[0];assert 0<length<=128*1024
     request=json.loads(exact(c,length));assert request['credential']==secret and request['version']==1
     tool=request['tool'];calls.append(tool)
     if tool=='get_capabilities':reply={'success':True,'code':'ok','data':{'tools':['probe_service','check_service_configuration']}}
     elif tool=='probe_service':reply=cases[states[request['arguments']['service']]]
     elif tool=='check_service_configuration':reply=cases['check']
     else:raise AssertionError('unexpected tool '+tool)
     payload=json.dumps(reply).encode();c.sendall(struct.pack('>i',len(payload))+payload)
  except Exception as e:errors.append(e)
 t=threading.Thread(target=worker);t.start()
 common=['--socket',str(path),'--credential-file',str(credential)]
 try:
  for service,state in states.items():
   p=subprocess.run([binary,'probe_service',*common,'--service',service],capture_output=True,timeout=10)
   expected_exit={'succeeded':0,'failed':6,'expired':6,'canceled':7}[state]
   assert p.returncode==expected_exit and json.loads(p.stdout)==cases[state],(state,p.returncode,p.stdout)
   assert not p.stderr
   print('PASS CLI probe',state,'exit',expected_exit,'and unchanged safe diagnostic details')
  p=subprocess.run([binary,'check_service_configuration',*common,'--service','preset'],capture_output=True,timeout=10)
  assert p.returncode==0 and json.loads(p.stdout)==cases['check'];print('PASS CLI offline check remains successful when not ready')
  p=subprocess.run(['dotnet',str(smoke),'--probe-feedback',str(fixtures),binary,'mcp',*common],capture_output=True,timeout=40)
  assert p.returncode==0,(p.stdout,p.stderr);print(p.stdout.decode().strip())
  assert calls.count('probe_service')==8 and calls.count('check_service_configuration')==2 and calls.count('get_capabilities')==1
  assert not errors
  print('PASS controlled IPC: one request per CLI/MCP probe; no retries, no cloud')
 finally:stop.set();t.join(timeout=2);server.close()
