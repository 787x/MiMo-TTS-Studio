"""Installed fnOS checks; run as root solely to simulate the trusted gateway.
The application still runs as package. Never changes directory authorization/ACL.
No MiMo calls; requires an explicit existing owned asset and authorized target.
Modes: writable exports a uniquely named WAV, denied expects a stale ID to fail.
"""
import argparse,hashlib,http.client,json,pathlib,socket,uuid
p=argparse.ArgumentParser();p.add_argument('mode',choices=['writable','denied']);p.add_argument('--asset',required=True);p.add_argument('--directory',required=True);p.add_argument('--id');p.add_argument('--baseline');args=p.parse_args()
root=pathlib.Path('/var/apps/mimotts')
def api(path,method='GET',data=None):
    c=http.client.HTTPConnection('nas.test');c.sock=socket.socket(socket.AF_UNIX);c.sock.settimeout(130);c.sock.connect(str(root/'target/app.sock'))
    headers={'Host':'nas.test','Origin':'http://nas.test','Referer':'http://nas.test/app/mimotts/','X-Trim-Userid':'1000','X-Trim-Username':'w','X-Trim-Isadmin':'true'}
    if method!='GET':headers['X-Studio-CSRF']=csrf
    if data is not None:headers['Content-Type']='application/json'
    c.request(method,'/app/mimotts/api'+path,None if data is None else json.dumps(data),headers)
    r=c.getresponse();raw=r.read();status=r.status;c.close();return status,json.loads(raw) if raw else None
csrf=api('/session')[1]['csrf']
status,targets=api('/export/targets');assert status==200
target=next((t for t in targets if t['path']==args.directory),None)
directory_id=args.id or (target or {}).get('id');assert directory_id,'provide prior --id for a revoked target'
status,assets=api('/assets');assert status==200
asset=next(a for a in assets if a['id']==args.asset)
source=root/'var/audio'/asset['id']/'audio.wav';before=source.read_bytes();metadata=(source.parent/'asset.json').read_bytes()
jobs=api('/jobs')[1];samples=api('/samples')[1]
name='PR5-controlled-'+uuid.uuid4().hex[:10]+'.wav'
status,result=api('/assets/'+asset['id']+'/export','POST',{'directoryId':directory_id,'fileName':name})
destination=pathlib.Path(args.directory)/name
if args.mode=='writable':
    assert target and target['writable'] and status==200,(status,result)
    assert destination.read_bytes()==before
    assert api('/assets/'+asset['id']+'/export','POST',{'directoryId':directory_id,'fileName':name})[0]==400
    assert destination.read_bytes()==before
    print('PASS installed export hash matches; collision never overwrites; output retained:',str(destination))
else:
    assert status==400 and not destination.exists(),(status,result)
    print('PASS installed read-only/revoked target denies export with no output:',result['error'])
assert source.read_bytes()==before and (source.parent/'asset.json').read_bytes()==metadata
assert api('/jobs')[1]==jobs and api('/samples')[1]==samples
print('PASS source bytes/metadata, Jobs and samples unchanged; no TTS job created')
if args.baseline:
    baseline=json.loads(pathlib.Path(args.baseline).read_text())
    assert all(hashlib.sha256(pathlib.Path(f).read_bytes()).hexdigest()==v for f,v in baseline.items())
    print('PASS original',len(baseline),'private files byte-for-byte unchanged')
