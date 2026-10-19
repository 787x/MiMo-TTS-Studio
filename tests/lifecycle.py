"""Exercise lifecycle hooks in disposable owned directories; no device required."""
import os,pathlib,subprocess,tempfile

repo=pathlib.Path(__file__).resolve().parent.parent
with tempfile.TemporaryDirectory(prefix='mimotts-hooks-') as temporary:
    root=pathlib.Path(temporary)
    env={**os.environ,'TRIM_PKGETC':str(root/'etc'),'TRIM_PKGVAR':str(root/'var')}
    def hook(name,**overrides):
        return subprocess.run(['bash',str(repo/'packaging/cmd'/name)],env={**env,**overrides},capture_output=True)
    assert hook('install_callback').returncode==0
    key=root/'etc/mimo-key';key.write_text('test-only-key');key.chmod(0o600)
    audio=root/'var/audio';audio.mkdir();(audio/'test.wav').write_bytes(b'fixture')
    assert hook('upgrade_callback').returncode==0 and key.read_text()=='test-only-key'
    print('PASS upgrade retains configuration and assets')
    assert hook('uninstall_init').returncode==0
    assert hook('uninstall_callback').returncode==0 and audio.exists()
    key.unlink() # Simulate fnOS removing etc on uninstall.
    assert hook('install_callback').returncode==0 and key.read_text()=='test-only-key'
    assert key.stat().st_mode & 0o777==0o600
    print('PASS keep uninstall/reinstall retains Key and audio with private permissions')
    outside=root/'unrelated';outside.mkdir();(outside/'keep.txt').write_text('user-data')
    assert hook('uninstall_init').returncode==0
    assert hook('uninstall_callback',wizard_data_action='delete').returncode==0
    assert not audio.exists() and not (root/'var/.retained-config').exists() and (outside/'keep.txt').exists()
    print('PASS confirmed deletion only removes owned children')
    audio.symlink_to(outside,target_is_directory=True)
    assert hook('uninstall_callback',wizard_data_action='delete').returncode!=0 and (outside/'keep.txt').exists()
    print('PASS symlink escape refused')
    assert hook('uninstall_callback',wizard_data_action='invalid').returncode!=0
    print('PASS invalid uninstall choice refused')
print('5 lifecycle checks passed')
