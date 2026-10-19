"""Exercise a Release build through a real private Unix socket; requires Linux root.
Usage: sudo python3 tests/production_gateway.py artifacts/fpk/app/server/MiMoStudio
No MiMo calls are made. Identity headers are injected only by the root test client.
"""
import http.client, http.server, threading, io, json, os, pathlib, pwd, shutil, socket, subprocess, sys, tempfile, time, wave

assert sys.platform == 'linux' and os.getuid() == 0, 'run as Linux root for gateway peer simulation'
exe=pathlib.Path(sys.argv[1]).resolve()
account=pwd.getpwnam('nobody')
root=pathlib.Path(tempfile.mkdtemp(prefix='mimotts-gateway-'))
root.chmod(0o755)
for name in ('etc','var','tmp','target'):
    path=root/name; path.mkdir();os.chown(path,account.pw_uid,account.pw_gid)
# The runtime remains in its published directory; target is only the socket directory.
env={**os.environ,'TRIM_APPDEST':str(root/'target'),'TRIM_PKGETC':str(root/'etc'),'TRIM_PKGVAR':str(root/'var'),'TRIM_PKGTMP':str(root/'tmp'),'MIMO_DEV_LOCAL':'1','ASPNETCORE_ENVIRONMENT':'Development'}
def drop():
    os.setgroups([]);os.setgid(account.pw_gid);os.setuid(account.pw_uid)
process=subprocess.Popen([str(exe)],cwd=exe.parent,env=env,preexec_fn=drop,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
sock=root/'target/app.sock'
def request(path='/api/session',method='GET',data=None,headers=None,identity=True,raw=None,chunked=False):
    connection=http.client.HTTPConnection('nas.test')
    connection.sock=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM);connection.sock.settimeout(5);connection.sock.connect(str(sock))
    h={'Host':'nas.test'}
    if identity:h.update({'X-Trim-Userid':'1000','X-Trim-Username':'admin','X-Trim-Isadmin':'true'})
    if data is not None:h['Content-Type']='application/json'
    h.update(headers or {})
    body=raw if raw is not None else json.dumps(data) if data is not None else None
    if chunked:body=iter([body[i:i+16000] for i in range(0,len(body),16000)])
    connection.request(method,'/app/mimotts'+path,body,h,encode_chunked=chunked)
    response=connection.getresponse();content=response.read();status=response.status;connection.close();return status,content
count=0
def check(value,name):
    global count
    assert value,name
    count+=1;print('PASS',name)
try:
    for _ in range(100):
        if sock.exists() and sock.stat().st_mode & 0o777 == 0o600:break
        if process.poll() is not None:raise RuntimeError(process.stdout.read().decode())
        time.sleep(.1)
    check(sock.stat().st_mode & 0o777 == 0o600,'socket mode 0600')
    status,content=request();check(status==200,'trusted root gateway admin')
    session=json.loads(content);check(session['development'] is False,'Release cannot enable development bypass')
    check(request(identity=False)[0]==403,'missing identity denied')
    check(request(headers={'X-Trim-Isadmin':'false'})[0]==403,'non-admin denied')
    check(request('/assets/anything',identity=False)[0]==403,'asset routes protected')
    check(request('/')[0]==200,'UI under gateway prefix')
    check(request('/',identity=False)[0]==403,'UI requires admin')
    check(request('/api/preview','POST',{'text':'你好'})[0]==403,'write without CSRF denied')
    writes={'Origin':'http://nas.test:5666','Referer':'http://nas.test:5666/app/mimotts/','X-Studio-CSRF':session['csrf']}
    check(request('/api/preview','POST',{'text':'你好'},writes)[0]==200,'gateway Origin port compatibility')
    for path,data in [('/api/diagnostics/check',{'service':'preset'}),('/api/diagnostics/run',{'service':'preset','id':'0'*32}),('/api/diagnostics/'+'0'*32+'/cancel',None)]:
        check(request(path,'POST',data,headers=writes,identity=False)[0]==403,'diagnostic endpoint admin protection '+path)
        check(request(path,'POST',data,headers={**writes,'Origin':'http://evil.test'})[0]==403,'diagnostic endpoint Origin protection '+path)
        check(request(path,'POST',data)[0]==403,'diagnostic endpoint CSRF protection '+path)
    status,body=request('/api/diagnostics/check','POST',{'service':'preset'},writes)
    precheck=json.loads(body);check(status==200 and not precheck['ready'] and precheck['category']=='missing_key','diagnostic precheck missing credentials is offline')

    check(request('/api/preview','POST',{'text':'你好'},{**writes,'Origin':'http://evil.test'})[0]==403,'cross-origin write denied')
    check(request('/api/key','PUT',{'key':'verification-dummy-key'},writes)[0]==200,'Key update')
    status,body=request();check(b'verification-dummy-key' not in body and json.loads(body)['configured'],'Key never returned')
    check((root/'etc/mimo-key').stat().st_mode & 0o777==0o600,'Key file private')
    check(request('/api/assets/../../etc/mimo-key/audio')[0]==404,'traversal denied')
    check(request('/api/llm',identity=False)[0]==403,'LLM settings require admin')
    check(request('/api/creative','POST',{'task':'script','draft':{'text':'request'}},headers=writes,identity=False)[0]==403,'AI requests require admin')
    settings={'baseUrl':'https://api.xiaomimimo.com/v1','model':'mimo-v2.6-flash','thinking':'mimo-disabled'}
    check(request('/api/llm','PUT',{'settings':settings,'key':'dummy-llm-private'},writes)[0]==200,'LLM configuration update')
    check(b'dummy-llm-private' not in request('/api/llm')[1],'LLM status hides credential')
    check((root/'etc/llm-private').stat().st_mode & 0o777==0o600,'LLM private file mode')
    check(request('/api/llm','PUT',{'settings':settings,'clearKey':True},writes)[0]==200,'LLM independent Key clear')
    check(request('/api/creative','POST',{'task':'script','draft':{'text':'request'}},writes)[0]==400,'no LLM Key prevents upstream request')
    check(request('/api/llm','PUT',{'settings':settings},headers={**writes,'Origin':'http://evil.test'})[0]==403,'LLM configuration Origin protected')
    upstream_calls=[]
    class CreativeUpstream(http.server.BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def do_POST(self):
            if self.headers.get('Content-Length'):
                raw=self.rfile.read(int(self.headers['Content-Length']))
            else:
                raw=b''
                while True:
                    length=int(self.rfile.readline().strip().split(b';')[0],16)
                    if not length:self.rfile.readline();break
                    raw+=self.rfile.read(length);assert self.rfile.read(2)==b'\r\n'
            data=json.loads(raw)
            upstream_calls.append((self.path,self.headers.get('Authorization'),data))
            content=json.dumps({'choices':[{'finish_reason':'stop','message':{'content':'Local compatible welcome.'}}]}).encode()
            self.send_response(200);self.send_header('Content-Length',str(len(content)));self.end_headers();self.wfile.write(content)
    local=http.server.HTTPServer(('127.0.0.1',0),CreativeUpstream)
    threading.Thread(target=local.serve_forever,daemon=True).start()
    try:
        custom={**settings,'baseUrl':'http://127.0.0.1:'+str(local.server_port)+'/v1','allowLocal':True}
        check(request('/api/llm','PUT',{'settings':{**custom,'allowLocal':False},'key':'dummy-local-key'},writes)[0]==400,'HTTP requires explicit administrator opt-in')
        check(request('/api/llm','PUT',{'settings':custom,'key':'dummy-local-key'},writes)[0]==200,'explicit local compatible service accepted')
        status,result=request('/api/creative','POST',{'task':'script','draft':{'text':'Write a greeting.'}},writes)
        check(status==200 and json.loads(result)['text']=='Local compatible welcome.','actual loopback HTTP through LLM client produces validated candidate')
        check(len(upstream_calls)==1 and upstream_calls[0][0]=='/v1/chat/completions' and upstream_calls[0][1]=='Bearer dummy-local-key' and 'audio' not in upstream_calls[0][2],'custom service uses independent credential with exactly one text request')
        status,body=request('/api/diagnostics/check','POST',{'service':'llm'},writes)
        precheck=json.loads(body);check(status==200 and precheck['ready'] and len(upstream_calls)==1,'saved LLM probe precheck sends no HTTP')
        status,body=request('/api/diagnostics/run','POST',{'service':'llm','revision':precheck['revision'],'id':'1'*32},writes)
        result=json.loads(body);check(status==200 and result['state']=='succeeded' and len(upstream_calls)==2,'Release authenticated probe sends exactly one real loopback request')
        check(b'dummy-local-key' not in body and not list((root/'var/audio').glob('*/asset.json')),'probe output hides Key and creates zero assets')

    finally:local.shutdown();local.server_close()
    check(request('/api/key','DELETE',headers=writes)[0]==204,'Key clear')
    check(request('/api/jobs','POST',{'text':'不会发送'},writes)[0]==400,'no Key prevents synthesis')
    check(request('/api/preview','POST',{'text':'x'*140000},writes)[0] in (400,413),'request body bound')
    content=io.BytesIO()
    with wave.open(content,'wb') as w:
        w.setnchannels(1);w.setsampwidth(2);w.setframerate(24000);w.writeframes(b'\0\0'*24000*4)
    reference=content.getvalue()
    upload={**writes,'X-Sample-Name':'integration-sample','X-Sample-Format':'wav','Content-Type':'application/octet-stream'}
    check(len(reference)>128*1024,'test fixture exceeds ordinary JSON cap')
    status,body=request('/api/samples/upload','POST',headers=upload,raw=reference)
    check(status==201,'valid large WAV upload accepted only on upload route')
    sample=json.loads(body)
    check(sample['durationSeconds']==4 and sample['bytes']==len(reference) and sample['format']=='wav','sample metadata from actual WAV')
    check(request('/api/samples/'+sample['id']+'/audio')[1]==reference,'private sample playback reuses upload bytes')
    check(request('/api/samples/'+sample['id']+'/audio',headers={'X-Trim-Userid':'1001'})[0]==404,'cross-user sample audio denied')
    check(request('/api/samples/'+sample['id'],'PUT',{'name':'renamed'},writes)[0]==200,'sample rename through CSRF-protected JSON API')
    check(request('/api/samples/'+sample['id'],'DELETE',headers={**writes,'X-Trim-Userid':'1001'})[0]==404,'cross-user sample deletion denied')
    check(request('/api/samples/upload','POST',headers={**upload,'Origin':'http://evil.test'},raw=reference)[0]==403,'upload Origin checked before accepting file')
    check(request('/api/samples/upload','POST',headers=upload,raw=b'RIFF fake')[0]==400,'invalid uploaded audio rejected')
    mp3=(pathlib.Path(__file__).parent/'fixtures/tone.mp3').read_bytes()
    check(request('/api/samples/upload','POST',headers=upload,raw=mp3)[0]==400,'file extension/declared format cannot disguise MP3')
    status,body=request('/api/samples/upload','POST',headers={**upload,'X-Sample-Format':'mp3'},raw=mp3,chunked=True)
    check(status==201,'chunked MP3 upload fully decoded without Content-Length trust')
    mp3sample=json.loads(body)
    status,preview=request('/api/preview','POST',{'text':'new text','synthesisMode':'clone','sourceSampleId':sample['id']},writes)
    check(status==200 and b'base64' not in preview and b'audio/wav' in preview,'clone preview validates sample with no reference bytes')
    check(request('/api/preview','POST',{'text':'new text','synthesisMode':'clone','sourceSampleId':sample['id']},{**writes,'X-Trim-Userid':'1001'})[0]==400,'clone preview cannot cross sample ownership')
    check(request('/api/samples/../../etc/mimo-key/audio')[0]==404,'sample traversal denied')
    check(request('/api/samples/upload','POST',headers=upload,raw=b'')[0]==400,'empty sample rejected')
    check(request('/api/samples/upload','POST',headers={**upload,'Content-Length':'7400001'},raw=b'')[0]==413,'sample upload body rejected before receiving oversized file')
    check(request('/api/samples/'+sample['id'],'DELETE',headers=writes)[0]==204 and request('/api/samples/'+mp3sample['id'],'DELETE',headers=writes)[0]==204,'owned sample deletion succeeds')
    check(json.loads(request('/api/samples')[1])==[],'upload failures never leave valid samples')
    # Even the socket owner cannot impersonate the gateway by forging headers.
    child=os.fork()
    if child==0:
        drop()
        try:os._exit(0 if request()[0]==403 else 1)
        except Exception:os._exit(2)
    _,exit_status=os.waitpid(child,0)
    check(os.waitstatus_to_exitcode(exit_status)==0,'non-root peer with forged admin headers denied')
finally:
    process.terminate()
    try:process.wait(timeout=15)
    except subprocess.TimeoutExpired:process.kill();process.wait()
    shutil.rmtree(root)
print(count,'production integration checks passed')
