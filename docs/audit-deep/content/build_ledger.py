import json, pathlib, re, hashlib, collections, subprocess
ROOT=pathlib.Path(r'F:\tmp\synaptic-sea-playable'); OUT=ROOT/'docs/audit-deep/content'; DATA=ROOT/'SynapticSea/Assets/StreamingAssets/data'
REV='840f14fb4450dcfba2a0bbce7cb8527e7dfa8e48'
def read(p): return json.loads((DATA/p).read_text(encoding='utf-8-sig'))
def loc(p, token):
 s=(ROOT/p).read_text(encoding='utf-8-sig'); m=re.search(re.escape('"'+token+'"'),s); return {'file':str(p).replace('\\','/'),'line':s[:m.start()].count('\n')+1 if m else None,'symbol':token,'revision':REV}
def dl(p,t):return loc(pathlib.Path('SynapticSea/Assets/StreamingAssets/data')/p,t)
merged={}; origins=collections.defaultdict(list); rawcounts={}
paths=['tools/tool_definitions.json','items/item_definitions.json','items/medicine_definitions.json','items/stimulant_definitions.json','combat/ammo_definitions.json','items/utility_item_definitions.json','items/trade_item_definitions.json','materials/material_definitions.json','items/equipment_definitions.json','items/junk_items.json','items/unique_items.json']
for p in paths:
 raw=read(p); raw=raw.get('materials',raw) if p.startswith('materials/') else raw.get('items',raw) if p in ['items/junk_items.json','items/unique_items.json'] else raw
 raw={k:v for k,v in raw.items() if isinstance(v,dict)}; rawcounts[p]=len(raw)
 for k,v in raw.items():
  k=v.get('item_id',k) if p=='items/unique_items.json' else k
  origins[k].append(dl(p,k))
  if p.startswith('materials/') and k in merged:continue
  if p=='items/unique_items.json' and k not in merged:continue
  if p=='tools/tool_definitions.json':v={'category':'tool','weight':2.0,**v}
  if p in ['items/medicine_definitions.json','items/stimulant_definitions.json','combat/ammo_definitions.json','items/utility_item_definitions.json','items/trade_item_definitions.json','items/junk_items.json','items/unique_items.json']:merged[k]={**merged.get(k,{}),**v}
  else:merged[k]=v
recipes=read('recipes/recipe_definitions.json')['recipes']; loot={k:v for k,v in read('items/loot_tables.json').items() if isinstance(v,dict) and 'entries' in v}; comps=read('components/component_catalog.json'); systems=read('ship_systems/systems.json')['systems']; classes=read('player/classes.json')['classes']; skills=read('player/skills.json')['skills']; books=read('player/skill_books.json')['books']; training=read('player/training_actions.json'); prereqs=read('player/skill_tree.json')['skill_prerequisites']; crops=read('crops/hydroponics_crops.json')['crops']
source=collections.defaultdict(list); edges=[]
def edge(a,b,t,ev,**kw):edges.append({'from':a,'to':b,'kind':t,'evidence':ev,**kw})
def grant(item,kind,id,ev,**kw):
 source[item].append({'kind':kind,'source_id':id,'evidence':ev,**kw});edge(kind+':'+id,'item:'+item,'acquires',ev,**kw)
for k,v in loot.items():
 for e in v['entries']:grant(e['item_id'],'loot_table_potential',k,dl('items/loot_tables.json',k),weight=e.get('weight',1),not_guaranteed=True)
home=read('procgen/golden/coherent_ship_001/gameplay_slice.json')
for c in home['loot_containers']:
 for e in c.get('contents',[]):grant(e['item_id'],'finite_home_authored',c['id'],dl('procgen/golden/coherent_ship_001/gameplay_slice.json',c['id']),quantity=e.get('qty',e.get('quantity')),room=c['room_id'])
# Current generator finite contents; parsed directly from its current source, not fixtures.
gpath='SynapticSea/Assets/_Project/Core/Procgen/GameplaySliceBuilder.cs'; gs=(ROOT/gpath).read_text();
for i,line in enumerate(gs.splitlines(),1):
 m=re.search(r'\{ "item_id", "([^"]+)" \}, \{ "qty", (\d+)L \}',line)
 if m:grant(m[1],'finite_expedition_authored','reclamation_expedition_v4',{'file':gpath,'line':i,'symbol':'Build','revision':REV},quantity=int(m[2]),conditional='medical / first crew store / first maintenance store; see enclosing source condition')
for c,v in comps['components'].items():
 form=v.get('item_form',c); role_refs=[r for r,sets in comps['role_sets'].items() if c in json.dumps(sets)]
 grant(form,'component_dismount_potential',c,dl('components/component_catalog.json',c),placement_roles=role_refs,normal_placement_candidate=bool(role_refs))
 for req in v.get('required_tools',[]):edge('item:'+req,'component:'+c,'tool_gate',dl('components/component_catalog.json',c))
for r in recipes:
 rid=r['recipe_id']; ev=dl('recipes/recipe_definitions.json',rid); out=r['produces']['item_id']; grant(out,'recipe',rid,ev,quantity=r['produces'].get('quantity',1))
 for k,q in r['ingredients'].items():edge('item:'+k,'recipe:'+rid,'ingredient',ev,quantity=q)
 edge('station:'+r['station_kind'],'recipe:'+rid,'station_gate',ev,tier=r.get('station_tier_min',0));edge('skill:fabrication','recipe:'+rid,'live_skill_gate' if r['station_kind']!='field_crafting' and r.get('category')!='deconstruction' else 'quality_only_or_salvage_ungated',ev,level=r.get('required_skill_level',0))
for k,v in read('items/junk_items.json')['items'].items():
 for y in v.get('yields',[]):grant(y['material_id'],'junk_yield',k,dl('items/junk_items.json',k),quantity=y['quantity']);edge('item:'+k,'junk_yield:'+k,'consumed',dl('items/junk_items.json',k))
for c in crops:grant(c['produce_item_id'],'production',c['crop_id'],dl('crops/hydroponics_crops.json',c['crop_id']),quantity=c['produce_quantity'])
grant('purified_water','production','water_recycler',loc('SynapticSea/Assets/_Project/Core/Session/Interactables/ProductionStation.cs','purified_water'))
for item in ['portable_oxygen_pump','junction_calibrator']:grant(item,'finite_home_tool_pickup',item,loc('SynapticSea/Assets/_Project/Core/Session/RunSession.Objectives.cs',item))
repairs=[]
for s in systems:
 for dep in s['dependency_ids']:edge('system:'+dep,'system:'+s['system_id'],'operational_dependency',dl('ship_systems/systems.json',s['system_id']))
 for sub in s['subcomponents']:
  rid=s['system_id']+'/'+sub['subcomponent_id'];ev=dl('ship_systems/systems.json',sub['subcomponent_id']);row={'system_id':s['system_id'],**sub,'evidence':ev,'classes_meeting_start_skill':[c['class_id'] for c in classes if c['starting_skills'].get('repair',0)>=sub['min_skill']]};repairs.append(row)
  for k in sub['required_parts']+sub['required_tools']:edge('item:'+k,'repair:'+rid,'part_or_tool_gate',ev,registered=k in merged,live_category=merged.get(k,{}).get('category',''))
  edge('skill:repair','repair:'+rid,'skill_gate',ev,level=sub['min_skill'])
# Structural availability upper-bound closure; deliberately does not claim scene reachability or skill progression.
seed={k for k,src in source.items() if any(s['kind'] in ['finite_home_authored','finite_home_tool_pickup','finite_expedition_authored','loot_table_potential','production'] or s['kind']=='component_dismount_potential' and s['normal_placement_candidate'] for s in src)}
closure=set(seed)
while True:
 prev=len(closure)
 for r in recipes:
  if set(r['ingredients'])<=closure:closure.add(r['produces']['item_id'])
 for k,v in read('items/junk_items.json')['items'].items():
  if k in closure:closure.update(y['material_id'] for y in v.get('yields',[]))
 if len(closure)==prev:break
# Item graph strongly connected components using Tarjan, independent of gate reachability.
adj=collections.defaultdict(set)
for r in recipes:
 for k in r['ingredients']:adj[k].add(r['produces']['item_id'])
idx={};low={};stack=[];on=set();scc=[]
def visit(v):
 idx[v]=low[v]=len(idx);stack.append(v);on.add(v)
 for w in adj[v]:
  if w not in idx:visit(w);low[v]=min(low[v],low[w])
  elif w in on:low[v]=min(low[v],idx[w])
 if low[v]==idx[v]:
  a=[]
  while True:
   w=stack.pop();on.remove(w);a.append(w)
   if w==v:break
  if len(a)>1 or v in adj[v]:scc.append(sorted(a))
for v in list(set(adj)|{w for vs in adj.values() for w in vs}):
 if v not in idx:visit(v)
items=[{'item_id':k,'definition':v,'definition_origins':origins[k],'acquisition_sources':source.get(k,[]),'structural_upper_bound_available':k in closure,'registered':True} for k,v in sorted(merged.items())]
refs=set(source)|{k for r in recipes for k in r['ingredients']}|{k for s in repairs for k in s['required_parts']+s['required_tools']}
summary={'revision':REV,'audit_mode':'static read-only; no Unity or compiled artifact executed','denominators':{'raw_definition_files':rawcounts,'merged_registered_items':len(merged),'materials':len(read('materials/material_definitions.json')['materials']),'recipes':len(recipes),'loot_tables':len(loot),'loot_entries':sum(len(v['entries']) for v in loot.values()),'component_types':len(comps['components']),'component_role_sets':len(comps['role_sets']),'ship_systems':len(systems),'repair_definitions':len(repairs),'classes':len(classes),'initially_selectable_classes':sum(not c.get('unlockable',False) for c in classes),'skills':len(skills),'books':len(books),'prerequisite_rows':len(prereqs),'crops':len(crops),'runtime_spatial_crafting_stations':6,'runtime_production_stations':2,'starting_inventory_stacks':0},'recipe_station_counts':dict(collections.Counter(r['station_kind'] for r in recipes)),'recipe_knowledge_counts':dict(collections.Counter(r.get('knowledge_source','starter') for r in recipes)),'recipe_category_counts':dict(collections.Counter(r.get('category','') for r in recipes)),'undefined_referenced_items':sorted(refs-set(merged)),'registered_items_no_enumerated_source':sorted(k for k in merged if not source.get(k)),'recipe_ingredients_outside_upper_bound_closure':sorted({k for r in recipes for k in r['ingredients']}-closure),'recipes_blocked_by_source_upper_bound':[r['recipe_id'] for r in recipes if not set(r['ingredients'])<=closure],'recipe_item_cycles':scc,'cycles_without_direct_seed':[c for c in scc if not set(c)&seed],'source_warning':'loot table, component role and production edges are potential source upper bounds, NOT runtime reachable guarantees; finite source edges are current live home asset/current generator and remain geometry/ownership gated','starting_classes':[{'class_id':c['class_id'],'unlockable':c.get('unlockable',False),'starting_skills':c['starting_skills'],'opening_shuttle_repair_skill_ok':c['starting_skills'].get('repair',0)>=2,'home_engine_commission_skill_ok':c['starting_skills'].get('repair',0)>=4,'station_recipes_start_skill_ok':[r['recipe_id'] for r in recipes if r['station_kind']!='field_crafting' and r.get('category')!='deconstruction' and c['starting_skills'].get('fabrication',0)>=r.get('required_skill_level',0)]} for c in classes]}
ledger={'summary':summary,'items':items,'unregistered_source_items':[{'item_id':k,'sources':source[k]} for k in sorted(refs-set(merged))],'recipes':[{**r,'evidence':dl('recipes/recipe_definitions.json',r['recipe_id'])} for r in recipes],'loot_tables':loot,'components':comps,'repairs':repairs,'classes':classes,'skills':skills,'books':books,'prerequisites':prereqs,'training_actions':training,'crops':crops}
for name,obj in [('dependency-ledger.json',ledger),('dependency-graph.json',{'revision':REV,'edges':edges}),('denominators.json',summary)]: (OUT/name).write_text(json.dumps(obj,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
hashes={str(p.relative_to(ROOT)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in DATA.rglob('*.json')}; (OUT/'data-file-hashes.json').write_text(json.dumps(hashes,indent=2)+'\n',encoding='utf-8')
print(json.dumps(summary,indent=2))
