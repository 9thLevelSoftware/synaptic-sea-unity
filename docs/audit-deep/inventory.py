"""Read-only first-party inventory; writes only audit outputs in its own directory."""
import collections, csv, hashlib, json, pathlib, re, subprocess

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = pathlib.Path(__file__).resolve().parent
tracked = subprocess.check_output(['git', '-c', f'safe.directory={ROOT.as_posix()}', 'ls-files', '-z'], cwd=ROOT).decode().split('\0')
revision = subprocess.check_output(['git', '-c', f'safe.directory={ROOT.as_posix()}', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip()
prefixes = ('SynapticSea/Assets/', 'SynapticSea/ProjectSettings/', 'tools/', 'fixtures/')
records, guid_paths = [], {}
for name in tracked:
    if not name or not name.startswith(prefixes): continue
    path = ROOT / name
    if not path.is_file(): continue
    blob = path.read_bytes()
    if path.suffix == '.meta':
        match = re.search(rb'^guid:\s*(\w+)', blob, re.M)
        if match: guid_paths[match.group(1).decode()] = name[:-5]
    domain = name.split('/')[3] if name.startswith('SynapticSea/Assets/_Project/') else '/'.join(name.split('/')[:3])
    records.append({'path':name, 'domain':domain, 'extension':path.suffix.lower(), 'bytes':len(blob), 'sha256':hashlib.sha256(blob).hexdigest(), 'coverage':'inventoried_not_traced', 'registration':'not_yet_traced'})
source_fingerprint = hashlib.sha256('\n'.join(f"{r['path']}:{r['sha256']}" for r in records if r['extension'] != '.meta').encode()).hexdigest()
with (OUT/'first-party-files.csv').open('w', newline='', encoding='utf-8') as f:
    writer = csv.DictWriter(f, fieldnames=list(records[0])); writer.writeheader(); writer.writerows(records)
catalogs, bindings, assemblies = [], [], []
for row in records:
    path = ROOT / row['path']
    if row['extension'] in ('.json', '.asmdef'):
        try: data = json.loads(path.read_text(encoding='utf-8-sig'))
        except (ValueError, UnicodeError): continue
        if row['extension'] == '.asmdef': assemblies.append({'path':row['path'], 'definition':data})
        else: catalogs.append({'path':row['path'], 'root_type':type(data).__name__, 'top_level': {k:len(v) if isinstance(v, (dict,list)) else type(v).__name__ for k,v in data.items()} if isinstance(data,dict) else {'count':len(data) if isinstance(data,list) else None}})
    if row['extension'] in ('.unity', '.prefab', '.asset'):
        text = path.read_text(encoding='utf-8-sig', errors='replace')
        for match in re.finditer(r'm_Script:\s*\{fileID:\s*[^,]+, guid:\s*(\w+), type:\s*\d+\}', text):
            guid = match.group(1)
            bindings.append({'asset':row['path'], 'line':text.count('\n',0,match.start())+1, 'script_guid':guid, 'resolved_script':guid_paths.get(guid, ''), 'scope':'serialized_reference_not_runtime_instantiation_proof'})
counts = collections.Counter((r['domain'],r['extension']) for r in records)
summary = {'revision':revision, 'tracked_scope_prefixes':prefixes, 'tracked_file_count_including_meta':len(records), 'nonmeta_count':sum(r['extension']!='.meta' for r in records), 'domain_extension_counts':[{'domain':k[0],'extension':k[1],'count':v} for k,v in sorted(counts.items())], 'source_content_fingerprint_sha256':source_fingerprint, 'catalogs':catalogs, 'assemblies':assemblies, 'serialized_script_bindings':bindings, 'limitations':['Inventory does not imply inspected coverage or live registration.','Ignored proprietary assets, Unity-generated project/Library/Temp outputs and embedded third-party package are excluded from first-party denominators.','Runtime-created objects require caller tracing and instantiated diagnostics; serialized script GUID does not prove activation.']}
(OUT/'inventory.json').write_text(json.dumps(summary, indent=2),encoding='utf-8')
print(json.dumps({'revision':revision,'files_including_meta':len(records),'nonmeta':summary['nonmeta_count'],'catalogs':len(catalogs),'assembly_definitions':len(assemblies),'serialized_bindings':len(bindings),'source_content_sha256':source_fingerprint}))
