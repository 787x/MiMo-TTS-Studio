"""Collect redistribution licenses from the exact restored runtime and UI packages."""
import json, os, pathlib, shutil, sys
repo=pathlib.Path(__file__).resolve().parent.parent
output=pathlib.Path(sys.argv[1])/'app/server/licenses'
output.mkdir(parents=True,exist_ok=True)
assets=json.loads((repo/'backend/obj/project.assets.json').read_text())
cache=pathlib.Path(next(iter(assets['packageFolders'])))
runtime_count=0
for framework in assets['project']['frameworks'].values():
    for entry in framework.get('downloadDependencies',[]):
        name=entry['name'].lower()
        if name not in ('microsoft.netcore.app.runtime.linux-x64','microsoft.aspnetcore.app.runtime.linux-x64'):continue
        version=entry['version'].strip('[]').split(',')[0].strip()
        source=cache/name/version
        target=output/(name+'-'+version);target.mkdir(exist_ok=True)
        for file in source.iterdir():
            if file.name.lower().startswith(('license','third-party-notice')):shutil.copy2(file,target/file.name)
        runtime_count+=1
assert runtime_count==2, 'Missing .NET redistribution notices'
lock=json.loads((repo/'frontend/package-lock.json').read_text())
for name,entry in lock['packages'].items():
    if not name or entry.get('dev'):continue
    source=repo/'frontend'/name
    for file in source.iterdir():
        if file.is_file() and file.name.lower().startswith(('license','notice')):
            target=output/name.replace('/','-');target.mkdir(exist_ok=True)
            shutil.copy2(file,target/file.name)
