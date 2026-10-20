import argparse,json,os,pathlib,pwd,select,subprocess,time
p=argparse.ArgumentParser();p.add_argument('--grant',required=True);args=p.parse_args()
r=pathlib.Path('/var/apps/mimotts');original=r/'var/agent'/(args.grant+'.credential');copy=r/'var/pr7-revocation.credential'
account=pwd.getpwnam('mimotts');fd=os.open(copy,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
with os.fdopen(fd,'wb') as f:f.write(original.read_bytes())
os.chown(copy,account.pw_uid,account.pw_gid)
mcp=None
try:
 mcp=subprocess.Popen(['runuser','-u','mimotts','--',str(r/'target/agent/MiMoStudio.Agent'),'mcp','--credential-file',str(copy)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 def rpc(method,params=None,id=1):
  msg={'jsonrpc':'2.0','method':method}
  if params is not None:msg['params']=params
  if id is not None:msg['id']=id
  mcp.stdin.write(json.dumps(msg).encode()+b'\n');mcp.stdin.flush()
  if id is None:return
  assert select.select([mcp.stdout],[],[],20)[0]
  return json.loads(mcp.stdout.readline())
 rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'revocation-test','version':'1'}});rpc('notifications/initialized',id=None)
 first=rpc('tools/call',{'name':'get_capabilities','arguments':{}});assert first['result']['structuredContent']['success']
 print('READY: revoke the grant through the trusted fnOS Web settings',flush=True)
 deadline=time.time()+180
 while original.exists() and time.time()<deadline:time.sleep(.2)
 assert not original.exists(),'Web grant not revoked before deadline'
 reply=rpc('tools/call',{'name':'synthesize_audio','arguments':{'text':'此操作必须被拒绝，不能发送到云端。'}})
 assert reply['result']['isError'] and reply['result']['structuredContent']['code']=='permission_denied'
 assert copy.exists()
 print('PASS real fnOS Web revocation rejects new synthesis on existing MCP connection using an old credential copy; no cloud',flush=True)
finally:
 if mcp:
  mcp.stdin.close();mcp.wait(timeout=15)
 copy.unlink(missing_ok=True)
