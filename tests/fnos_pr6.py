"""Installed PR6 Composition checks, no cloud calls. Root simulates the trusted gateway.
Optional --baseline points to a root-only pre-upgrade {absolute_path: sha256} file.
Never prints credentials/digests or alters existing assets, settings or samples.
"""
import argparse, hashlib, http.client, json, pathlib, socket
p=argparse.ArgumentParser();p.add_argument('--baseline');args=p.parse_args()
root=pathlib.Path('/var/apps/mimotts');csrf=''
def api(path, method='GET', data=None):
    c=http.client.HTTPConnection('nas.test');c.sock=socket.socket(socket.AF_UNIX);c.sock.settimeout(15);c.sock.connect(str(root/'target/app.sock'))
    headers={'Host':'nas.test','Origin':'http://nas.test','Referer':'http://nas.test/app/mimotts/','X-Trim-Userid':'1000','X-Trim-Username':'w','X-Trim-Isadmin':'true','X-Studio-CSRF':csrf}
    if data is not None:headers['Content-Type']='application/json'
    c.request(method,'/app/mimotts/api'+path,None if data is None else json.dumps(data),headers)
    r=c.getresponse();body=r.read();status=r.status;c.close();return status,json.loads(body) if body else None
status,session=api('/session');assert status==200 and not session['development'];csrf=session['csrf']
assert '0.6.2' in (root/'manifest').read_text()
assert (root/'target/app.sock').stat().st_mode & 0o777 == 0o600
before={path:api(path)[1] for path in ('/jobs','/assets','/samples')}
text='[轻笑]你好 e\u0301👋。'+('这是一段原文。'*120)
for mode in ('preset','clone'):
    if mode=='clone' and not before['/samples']:
        print('SKIP clone preview: no existing sample');continue
    data={'text':text,'tags':'温柔 清亮 上海话','synthesisMode':mode}
    if mode=='clone':data['sourceSampleId']=before['/samples'][0]['id']
    status,plan=api('/preview','POST',data);assert status==200
    assert plan['prefix']=='(温柔 清亮 上海话)' and plan['user']==''
    assert ''.join(plan['segments'])==text
    assert plan['assistant']==[plan['prefix']+s for s in plan['segments']]
    assert all(len(s.encode())<=3000 for s in plan['assistant'])
    print('PASS installed',mode,'preview: original body, every prefix and UTF8 budget')
for tags in ('温柔\n清亮','x\u202e','x\u200b','x\ud800','(温柔)','【清亮】','唱歌','温柔 SINGING','x'*201):
    assert api('/preview','POST',{'text':'原文','tags':tags})[0]==400
assert api('/preview','POST',{'text':'x'*2990,'tags':'温柔 清亮 上海话','split':False})[0]==400
status,plan=api('/preview','POST',{'text':'歌词','tags':'清亮','singing':True});assert status==200 and plan['prefix']=='(唱歌)(清亮)'
assert api('/preview','POST',{'text':'试音','tags':'温柔','synthesisMode':'design','voiceDescription':'清亮女声'})[0]==400
raw='  soft voice  e\u0301 上海话 '
assert api('/preview','POST',{'text':'原文','tags':raw})[1]['prefix']=='('+raw+')'
assert before=={path:api(path)[1] for path in before}
print('PASS installed invalid/alias/budget rejection; free Tags exact; zero jobs/assets/sample changes')
if args.baseline:
    baseline=json.loads(pathlib.Path(args.baseline).read_text())
    assert all(pathlib.Path(path).is_file() and hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==value for path,value in baseline.items())
    print('PASS original',len(baseline),'private configuration/WAV/sample files unchanged')
