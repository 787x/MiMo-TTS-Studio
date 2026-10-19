"""PR3 palette substitution is lossless: all existing declarations/layout stay exact."""
import pathlib,re,subprocess
root=pathlib.Path(__file__).resolve().parent.parent
original=subprocess.check_output(['git','show','c959e84:frontend/src/style.css'],cwd=root).decode()
current=(root/'frontend/src/style.css').read_text(encoding='utf-8')
light,current=current.split('}\n',1)
_,current=current.split('}\n',1)
values=dict(re.findall(r'(--[\w-]+):([^;]+);',light))
current=current.split('\ninput::placeholder',1)[0]
current=re.sub(r'var\((--[\w-]+)\)',lambda m:values[m[1]],current)
# #fff and white are equivalent named colors; original light declarations otherwise exact.
normalize=lambda s:re.sub(r'\s+','',s.replace('white','#fff'))
assert normalize(current)==normalize(original),'An original declaration changed beyond color substitution'
print('PASS all PR3 CSS declarations retained exactly after resolving light color variables')
