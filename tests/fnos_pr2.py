"""Run as root on a designated fnOS device after upgrading; no cloud synthesis.
Pass the synthetic tone.mp3 path and, optionally, a private pre-upgrade digest file.
Only creates/removes its own labeled sample fixtures; never changes the API Key.
"""
import hashlib,http.client,io,json,os,pathlib,pwd,socket,subprocess,sys,time,wave

assert os.getuid()==0
root=pathlib.Path('/var/apps/mimotts');sock=root/'target/app.sock';count=0
def check(condition,name):
    global count
    assert condition,name
    count+=1;print('PASS',name,flush=True)
csrf=''
def request(path='/session',method='GET',data=None,headers=None,raw=None):
    c=http.client.HTTPConnection('nas.test');c.sock=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM);c.sock.settimeout(10);c.sock.connect(str(sock))
    h={'Host':'nas.test','Origin':'http://nas.test:5666','Referer':'http://nas.test:5666/app/mimotts/','X-Trim-Userid':'1000','X-Trim-Username':'verification','X-Trim-Isadmin':'true','X-Studio-CSRF':csrf}
    if data is not None:h['Content-Type']='application/json'
    h.update(headers or {})
    c.request(method,'/app/mimotts/api'+path,raw if raw is not None else json.dumps(data) if data is not None else None,h)
    r=c.getresponse();status=r.status;body=r.read();c.close();return status,body
def session():
    global csrf
    status,body=request();check(status==200,'admin private-socket session');csrf=json.loads(body)['csrf']
def wait_status(wanted):
    for _ in range(150):
        r=subprocess.run(['appcenter-cli','status','mimotts'],capture_output=True,text=True)
        if r.stdout.strip()==wanted:return
        time.sleep(.2)
    raise RuntimeError('device lifecycle timeout')
session()
if len(sys.argv)>2:
    before=json.loads(pathlib.Path(sys.argv[2]).read_text())
    check('0.1.3' in before['version'] and 'version' in (root/'manifest').read_text() and '0.2.' in (root/'manifest').read_text(),'actual PR1-to-PR2 version transition')
    check(hashlib.sha256((root/'etc/mimo-key').read_bytes()).hexdigest()==before['key'],'upgrade preserves exact private Key')
    check(all(hashlib.sha256((root/'var/audio'/identifier/'audio.wav').read_bytes()).hexdigest()==digest for identifier,digest in before['audio'].items()),'upgrade preserves every pre-existing WAV hash')
    listed=json.loads(request('/assets')[1]);check(all(any(a['id']==identifier for a in listed) for identifier in before['audio']),'PR1 assets listed in new API')
    check(all(request('/assets/'+identifier+'/audio')[0]==200 for identifier in before['audio']),'PR1 assets remain accessible')
jobs_before=json.loads(request('/jobs')[1]);check(not any(j['state'] in ('running','canceling') for j in jobs_before),'no active user task interrupted by restart test')
check((root/'etc/mimo-key').stat().st_mode&0o777==0o600,'Key mode still 0600')
check(sock.stat().st_mode&0o777==0o600,'socket mode still 0600')
pid=(root/'var/server.pid').read_text().split()[0]
check(pathlib.Path('/proc/'+pid).stat().st_uid==pwd.getpwnam('mimotts').pw_uid,'package user still runs service')
content=io.BytesIO()
with wave.open(content,'wb') as w:
    w.setnchannels(1);w.setsampwidth(2);w.setframerate(24000);w.writeframes(b'\0\0'*24000*4)
wav=content.getvalue();mp3=pathlib.Path(sys.argv[1]).read_bytes()
created=[]
try:
    upload={'Content-Type':'application/octet-stream','X-Sample-Name':'PR2-local-verification','X-Sample-Format':'wav'}
    status,body=request('/samples/upload','POST',headers=upload,raw=wav);check(status==201,'actual fnOS accepts >128 KiB WAV upload');a=json.loads(body);created.append(a['id'])
    check(a['durationSeconds']==4 and a['bytes']==len(wav) and a['format']=='wav','actual WAV metadata')
    status,body=request('/samples/upload','POST',headers={**upload,'X-Sample-Format':'mp3'},raw=mp3);check(status==201,'managed MP3 decoder runs on actual fnOS');b=json.loads(body);created.append(b['id'])
    check(b['format']=='mp3' and .95<b['durationSeconds']<1.1,'actual MP3 format and frame duration')
    check(request('/samples/upload','POST',headers=upload,raw=mp3)[0]==400,'actual disguised audio rejected')
    check(request('/samples/upload','POST',headers={**upload,'X-Sample-Format':'mp3'},raw=mp3[:-1])[0]==400,'actual truncated MP3 rejected')
    check(request('/preview','POST',{'text':'x'*140000})[0]==413,'ordinary JSON still capped at 128 KiB')
    check(request('/samples/upload','POST',headers={**upload,'Content-Length':'7400001'},raw=b'')[0]==413,'actual upload cap enforced before file read')
    check(request('/samples/upload','POST',headers={**upload,'Origin':'http://evil.test'},raw=wav)[0]==403,'actual upload Origin protection')
    check(request('/samples/'+a['id']+'/audio',headers={'X-Trim-Isadmin':'false'})[0]==403,'sample requires admin')
    check(request('/samples/'+a['id']+'/audio',headers={'X-Trim-Userid':'1001'})[0]==404,'sample ownership enforced')
    check(request('/samples/../../etc/mimo-key/audio')[0]==404,'sample traversal rejected')
    check(request('/samples/'+a['id'],'PUT',{'name':'PR2-renamed-verification'})[0]==200,'sample rename works')
    check(request('/samples/'+a['id']+'/audio?download=1')[1]==wav,'sample download reuses exact bytes')
    check(request('/preview','POST',{'text':'new text','synthesisMode':'clone','sourceSampleId':a['id']})[0]==200,'clone preview reads trusted local sample metadata')
    check(len(json.loads(request('/jobs')[1]))==len(jobs_before),'all sample operations created no synthesis jobs')
    subprocess.run(['appcenter-cli','stop','mimotts'],check=True,stdout=subprocess.PIPE);wait_status('stopped')
    subprocess.run(['appcenter-cli','start','mimotts'],check=True,stdout=subprocess.PIPE);wait_status('running');session()
    check(request('/samples/'+a['id']+'/audio')[1]==wav and request('/samples/'+b['id']+'/audio')[1]==mp3,'WAV and MP3 samples survive actual restart byte-for-byte')
    check(any(s['id']==a['id'] and s['name']=='PR2-renamed-verification' for s in json.loads(request('/samples')[1])),'sample metadata and rename survive restart')
    check(request('/samples/'+a['id'],'DELETE')[0]==204,'owned sample deletion works');created.remove(a['id'])
    check(request('/samples/'+a['id']+'/audio')[0]==404,'deleted sample no longer accessible')
    check(request('/samples/'+b['id']+'/audio')[1]==mp3,'deleting one sample preserves the other')
finally:
    if sock.exists():
        status,body=request();csrf=json.loads(body)['csrf']
        for identifier in created:request('/samples/'+identifier,'DELETE')
print(count,'PR2 actual fnOS checks passed',flush=True)
