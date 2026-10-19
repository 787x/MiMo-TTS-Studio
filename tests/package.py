"""Inspect the fnpack archive without extracting it. No third-party dependencies."""
import io,json,pathlib,sys,tarfile
path=pathlib.Path(sys.argv[1] if len(sys.argv)>1 else 'artifacts/mimotts.fpk')
with tarfile.open(path,'r:gz') as outer:
    manifest=outer.extractfile('manifest').read().decode()
    fields=dict(line.split('=',1) for line in manifest.splitlines() if '=' in line)
    fields={k.strip():v.strip() for k,v in fields.items()}
    assert fields['platform']=='x86' and fields['appname']=='mimotts'
    assert fields['ctl_stop']=='true' and 'service_port' not in fields
    for name in ['main','install_init','install_callback','upgrade_init','upgrade_callback','uninstall_init','uninstall_callback']:
        member=outer.getmember('cmd/'+name);assert member.mode&0o111
        script=outer.extractfile(member).read();assert b'\r' not in script and b'/vol1' not in script
    assert json.load(outer.extractfile('config/privilege'))['defaults']['run-as']=='package'
    json.load(outer.extractfile('config/resource'));json.load(outer.extractfile('wizard/uninstall'))
    with tarfile.open(fileobj=io.BytesIO(outer.extractfile('app.tgz').read()),mode='r:gz') as app:
        members={m.name.removeprefix('./'):m for m in app.getmembers()}
        def read(name):return app.extractfile(members[name]).read()
        config=json.loads(read('ui/config'))['.url']['mimotts.main']
        assert config['gatewayPrefix']=='/app/mimotts' and config['gatewaySocket']=='app.sock' and not config['allUsers']
        assert read('server/MiMoStudio')[:4]==b'\x7fELF'
        assert members['server/MiMoStudio'].mode&0o111
        assert 'server/libcoreclr.so' in members and 'server/wwwroot/index.html' in members
        assert any('microsoft.netcore.app.runtime.linux-x64' in n and 'LICENSE' in n for n in members)
        assert any('microsoft.aspnetcore.app.runtime.linux-x64' in n and 'LICENSE' in n for n in members)
        assert all('node_modules/' not in n and 'SeleVox' not in n and not n.endswith(('mimo-key','.partial','.pdb')) for n in members)
        deps=read('server/MiMoStudio.deps.json')
        assert b'linux-x64' in deps and b'WindowsDesktop' not in deps
print('PASS FPK metadata, scripts, gateway, non-root privilege, ELF/runtime, UI, notices, independence and clean payload')
