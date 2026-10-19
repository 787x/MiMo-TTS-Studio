"""fnOS read-only checks; --online explicitly authorizes four billable fixed probes.
Example (root): python3 fnos_pr4.py --online --sample <explicit-owned-sample-id>
Use --root only for an isolated unpacked Release verification, not installation evidence.
"""
import argparse,hashlib,http.client,json,pathlib,socket,uuid
p=argparse.ArgumentParser();p.add_argument('--root',default='/var/apps/mimotts');p.add_argument('--online',action='store_true');p.add_argument('--sample');p.add_argument('--baseline');args=p.parse_args()
root=pathlib.Path(args.root)
def api(path,method='GET',data=None):
 c=http.client.HTTPConnection('nas.test');c.sock=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM);c.sock.settimeout(110);c.sock.connect(str(root/'target/app.sock'))
 headers={'X-Trim-Userid':'1000','X-Trim-Username':'w','X-Trim-Isadmin':'true','Host':'nas.test','Origin':'http://nas.test','Referer':'http://nas.test/app/mimotts/'}
 if method!='GET':headers['X-Studio-CSRF']=csrf
 if data is not None:headers['Content-Type']='application/json'
 c.request(method,'/app/mimotts/api'+path,None if data is None else json.dumps(data),headers);r=c.getresponse();raw=r.read();status=r.status;c.close();assert status==200,(path,status);return json.loads(raw)
csrf=api('/session')['csrf'];assert not api('/session')['development']
assert (root/'target/app.sock').stat().st_mode&0o777==0o600
initial={path:len(api(path)) for path in ('/jobs','/assets','/samples')}
for service in ('preset','design','clone','llm'):
 check=api('/diagnostics/check','POST',{'service':service})
 assert check['ready']==(service!='clone'),(service,check['category'])
print('PASS Release socket private, saved configurations ready; clone without selection blocked',flush=True)
if args.online:
 assert args.sample,'online clone requires an explicit sample ID'
 assert any(s['id']==args.sample for s in api('/samples')),'sample must belong to current trusted admin'
 for service in ('preset','design','clone','llm'):
  data={'service':service,'sampleId':args.sample if service=='clone' else None}
  check=api('/diagnostics/check','POST',data);assert check['ready']
  result=api('/diagnostics/run','POST',{**data,'revision':check['revision'],'id':uuid.uuid4().hex})
  print('REAL',service,result['state'],result['category'],result['elapsedMs'],'ms',flush=True)
  assert result['state']=='succeeded',(service,result['category'])
 assert initial=={path:len(api(path)) for path in initial}
 print('PASS real probes create zero jobs/audio/sample assets',flush=True)
if args.baseline:
 baseline=json.loads(pathlib.Path(args.baseline).read_text())
 assert all(pathlib.Path(path).is_file() and hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()==h for path,h in baseline.items())
 print('PASS original',len(baseline),'private/config/audio/sample files byte-for-byte unchanged',flush=True)
