"""Run as root on an explicitly designated fnOS test device AFTER FPK installation.
Creates only labeled, synthetic fixtures in this app's private storage, then removes them.
Does not call MiMo. Covers actual appcenter lifecycle and Unix-socket HTTP access;
browser login, iframe and gateway spoof tests are separate manual acceptance steps.
"""
import datetime, http.client, io, json, os, pathlib, pwd, socket, struct, subprocess, time, uuid, wave

assert os.getuid()==0
app=pathlib.Path('/var/apps/mimotts')
sock=app/'target/app.sock'
account=pwd.getpwnam('mimotts')
count=0
def check(value,name):
    global count
    assert value,name
    count+=1;print('PASS',name,flush=True)
def request(path='/session',method='GET',data=None,extra=None,identity=True):
    c=http.client.HTTPConnection('nas.test');c.sock=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM);c.sock.settimeout(10);c.sock.connect(str(sock))
    h={'Host':'nas.test','Origin':'http://nas.test:5666','Referer':'http://nas.test:5666/app/mimotts/'}
    if identity:h.update({'X-Trim-Userid':'1000','X-Trim-Username':'verification','X-Trim-Isadmin':'true'})
    if data is not None:h['Content-Type']='application/json'
    h.update(extra or {})
    c.request(method,'/app/mimotts/api'+path,json.dumps(data) if data is not None else None,h)
    r=c.getresponse();status=r.status;body=r.read();c.close();return status,body
def wait_status(wanted):
    for _ in range(150):
        state=subprocess.run(['appcenter-cli','status','mimotts'],capture_output=True,text=True).stdout.strip()
        if state==wanted:return
        time.sleep(.2)
    raise RuntimeError('appcenter state '+state+' wanted '+wanted)
def drop():
    os.setgroups([]);os.setgid(account.pw_gid);os.setuid(account.pw_uid)
wait_status('running')
check(sock.stat().st_mode&0o777==0o600,'actual fnOS socket 0600')
pidfile=app/'var/server.pid'
pid=pidfile.read_text().split()[0]
check(pathlib.Path('/proc/'+pid).stat().st_uid==account.pw_uid,'service runs as package user')
environment=dict(v.split('=',1) for v in pathlib.Path('/proc/'+pid+'/environ').read_text().split('\0') if '=' in v)
main=str(app/'cmd/main')
check(subprocess.run([main,'status'],env=environment,preexec_fn=drop).returncode==0,'main status matches live process')
check(subprocess.run([main,'start'],env=environment,preexec_fn=drop).returncode==0 and pidfile.read_text().split()[0]==pid,'repeated start has same PID')
status,body=request();check(status==200,'actual root gateway peer accepted')
csrf=json.loads(body)['csrf'];headers={'X-Studio-CSRF':csrf}
already_configured=json.loads(body)['configured']
check(request(identity=False)[0]==403,'missing identity rejected')
check(request(extra={'X-Trim-Isadmin':'false'})[0]==403,'non-admin rejected on actual service')
check(request('/preview','POST',{'text':'实机请求预览'},headers)[0]==200,'actual server accepts compatible Origin')
check(request('/preview','POST',{'text':'拒绝来源'},{**headers,'Origin':'http://evil.test'})[0]==403,'wrong Origin rejected')
check(request('/preview','POST',{'text':'无CSRF'})[0]==403,'missing CSRF rejected')
fixture_id=uuid.uuid4().hex;directory=app/'var/audio'/fixture_id
directory.mkdir(mode=0o700);os.chown(directory,account.pw_uid,account.pw_gid)
audio=io.BytesIO()
with wave.open(audio,'wb') as w:
    w.setnchannels(1);w.setsampwidth(2);w.setframerate(24000);w.writeframes(b'\0\0'*2400)
wav=audio.getvalue()
asset={'id':fixture_id,'owner':'1000','title':'Verification fixture — synthetic silence, NOT MiMo','voice':'mimo_default','created':datetime.datetime.now(datetime.timezone.utc).isoformat(),'saved':False,'bytes':len(wav)}
for name,data in [('audio.wav',wav),('asset.json',json.dumps(asset).encode())]:
    path=directory/name;path.write_bytes(data);path.chmod(0o600);os.chown(path,account.pw_uid,account.pw_gid)
key_set=False
try:
    check(request('/assets/'+fixture_id+'/save','POST',extra=headers)[0]==200,'save synthetic fixture')
    check(request('/assets/'+fixture_id+'/audio')[1]==wav,'audio reuses exact fixture bytes')
    check(request('/assets/'+fixture_id+'/audio?download=1')[1]==wav,'download reuses exact fixture bytes')
    check(request('/assets/'+fixture_id+'/audio',extra={'Range':'bytes=0-43'})[0]==206,'HTTP range works')
    check(request('/assets/'+fixture_id+'/audio',extra={'X-Trim-Userid':'1001'})[0]==404,'other admin cannot access owner asset')
    check(request('/assets/../../etc/mimo-key/audio')[0]==404,'path traversal rejected')
    # Never set or clear credentials on a device where the user may configure concurrently.
    check(b'verification-dummy-key' not in request()[1],'Key absent from GET')
    subprocess.run(['appcenter-cli','stop','mimotts'],check=True);wait_status('stopped')
    check(not sock.exists(),'stop removes socket')
    check(subprocess.run([main,'status'],env=environment,preexec_fn=drop).returncode==3,'stopped main status is 3')
    subprocess.run(['appcenter-cli','start','mimotts'],check=True);wait_status('running')
    status,body=request();check(status==200 and (not already_configured or json.loads(body)['configured']),'restart preserves existing Key state')
    csrf=json.loads(body)['csrf'];headers={'X-Studio-CSRF':csrf}
    check(request('/assets/'+fixture_id+'/audio')[1]==wav,'restart preserves saved asset')
    if (app/'etc/mimo-key').exists():check((app/'etc/mimo-key').stat().st_mode&0o777==0o600,'actual Key file 0600')
    check(request('/assets/'+fixture_id,'DELETE',extra=headers)[0]==204,'delete app-owned test asset')
    check(request('/assets/'+fixture_id+'/audio')[0]==404,'deleted asset unavailable')
finally:
    if sock.exists():
        status,body=request();headers={'X-Studio-CSRF':json.loads(body)['csrf']}
        if key_set:request('/key','DELETE',extra=headers)
        request('/assets/'+fixture_id,'DELETE',extra=headers)
print(count,'actual fnOS checks passed',flush=True)
