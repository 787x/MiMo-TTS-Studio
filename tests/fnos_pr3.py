"""Read-only post-upgrade checks through the trusted local socket; no cloud calls.
Run as root on fnOS. Optional private baseline file from before upgrade.
"""
import hashlib,http.client,json,pathlib,socket,sys
root=pathlib.Path('/var/apps/mimotts')
def api(path):
 c=http.client.HTTPConnection('nas.test');c.sock=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM);c.sock.connect(str(root/'target/app.sock'))
 c.request('GET','/app/mimotts/api'+path,headers={'X-Trim-Userid':'1000','X-Trim-Username':'w','X-Trim-Isadmin':'true'})
 r=c.getresponse();body=r.read();assert r.status==200,(path,r.status);c.close();return json.loads(body)
version=next(line.split('=',1)[1].strip() for line in (root/'manifest').read_text().splitlines() if line.split('=',1)[0].strip()=='version')
assert tuple(map(int,version.split('.'))) >= (0,3,2)
assert (root/'target/app.sock').stat().st_mode&0o777==0o600
assert (root/'etc/mimo-key').stat().st_mode&0o777==0o600
s=api('/session');assert s['configured'] and not s['development']
llm=api('/llm');assert 'key' not in llm and 'key' not in llm['settings']
api('/assets');api('/samples')
if len(sys.argv)>1:
 baseline=json.loads(pathlib.Path(sys.argv[1]).read_text())
 assert all(pathlib.Path(p).is_file() and hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()==h for p,h in baseline.items())
 print('PASS all',len(baseline),'original configuration/audio/sample files unchanged')
print('PASS',version,'manifest, private socket/Key, Release admin API, LLM status and original asset APIs')
