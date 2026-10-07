"""Compact audit-only delivery register; no gameplay edits or source mutation."""
import json,pathlib
p=pathlib.Path(__file__).resolve().parent
d=json.loads((p/'dependency-ledger.json').read_text())
absent=set(d['summary']['recipe_ingredients_outside_upper_bound_closure'])
out=['recipe_id|source_status|missing_inputs|station|fabrication_min|tier|min_input_quantities|output']
for r in d['recipes']:
    missing=sorted(set(r['ingredients'])&absent)
    out.append('|'.join([r['recipe_id'],'BLOCKED' if missing else 'UPPER_BOUND_ONLY',','.join(missing) or '-',r.get('station_kind',''),str(r.get('required_skill_level',0)),str(r.get('station_tier_min',0)),','.join(f'{k}={v}' for k,v in r['ingredients'].items()),f"{r['produces']['item_id']}={r['produces']['quantity']}"]))
(p/'recipe-status-delivery.txt').write_text('\n'.join(out),encoding='utf-8')
mapping={item:[r['recipe_id'] for r in d['recipes'] if item in r['ingredients']] for item in sorted(absent)}
(p/'missing-input-delivery.json').write_text(json.dumps(mapping,indent=2),encoding='utf-8')
print('\n'.join(out))
print('MISSING_INPUTS='+json.dumps(mapping,separators=(',',':')))
print('SOURCELESS='+','.join(d['summary']['registered_items_no_enumerated_source']))
print('COMPONENT_FORMS='+','.join(d['summary']['undefined_referenced_items']))
print('BOOKS='+json.dumps(d['books'],separators=(',',':')))
print('PREREQUISITES='+json.dumps(d['prerequisites'],separators=(',',':')))
