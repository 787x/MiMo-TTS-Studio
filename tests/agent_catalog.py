"""Keep the Agent catalog identical to PR6 UI vocabulary; Composition remains authoritative."""
import json,pathlib,re
root=pathlib.Path(__file__).resolve().parent.parent
ui=(root/'frontend/src/styles.ts').read_text(encoding='utf-8').split('export const searchStyles')[0]
server=(root/'backend/AgentExecution.cs').read_text(encoding='utf-8').split('public static class StyleCatalog')[1]
ui_groups=[(name,re.findall("'([^']*)'",tags)) for name,tags in re.findall(r"name:'([^']+)',tags:\[([^\]]+)\]",ui)]
server_groups=[(name,re.findall('"([^"]*)"',tags)) for name,tags in re.findall(r'name="([^"]+)",tags=new\[\]\{([^}]+)\}',server)]
assert ui_groups==server_groups and len(ui_groups)==8 and sum(len(tags) for _,tags in ui_groups)==48
print('PASS Agent style catalog matches all 8 categories / 48 PR6 UI entries exactly')
