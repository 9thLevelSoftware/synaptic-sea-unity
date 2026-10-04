"""Validate the draft design packet and untouched baseline, never the game.

Default: read-only, JSON summary on stdout. --report writes only the packet's
validation-report.json after checking. No product tests/builds/imports/network.
"""
import argparse
import csv
import datetime
import hashlib
import json
import pathlib
import re
import subprocess
import sys

OUT = pathlib.Path(__file__).resolve().parent
ROOT = OUT.parents[2]
errors = []
checks = []

def read(name):
    return json.loads((OUT / name).read_text(encoding='utf-8-sig'))

def check(ok, message):
    if not ok:
        errors.append(message)

def unique(rows, label):
    ids = [r['id'] for r in rows]
    check(len(ids) == len(set(ids)), label + ' IDs duplicate')
    return set(ids)

def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

def git(*args):
    return subprocess.check_output(['git', '-c', 'safe.directory=' + ROOT.as_posix(), *args], cwd=ROOT).decode().strip()

ap = argparse.ArgumentParser()
ap.add_argument('--report', action='store_true')
ap.add_argument('--observe-concurrent-engineering', action='store_true', help='Report exact admitted engineer changes without resetting the authoring baseline')
ap.add_argument('--write-hashes', action='store_true', help='After successful validation, write only the documentation artifact hash manifest')
ap.add_argument('--verify-hashes', action='store_true', help='Read-only verification of the complete finalized documentation hash manifest')
args = ap.parse_args()
if args.verify_hashes and (args.report or args.write_hashes):
    ap.error('--verify-hashes is read-only and cannot be combined with report/hash writes')
work_register = read('work-packages.json')
w = work_register['work_packages']
t = read('acceptance-tests.json')['tests']
r = read('traceability.json')['rows']
wids, tids, rids = unique(w,'work'), unique(t,'test'), unique(r,'trace')
requirements = set(read('work-packages.json')['requirements'])
tracked = set(p for p in git('ls-files', '-z').split('\0') if p)

for row in w:
    check(set(row['depends_on']) <= wids, row['id'] + ' unknown dependency')
    check(set(row['acceptance_tests']) <= tids, row['id'] + ' unknown test')
    check(set(row.get('package_exit_tests',[])) <= tids, row['id'] + ' unknown package exit test')
    check(set(row.get('downstream_integration_tests',[])) <= tids, row['id'] + ' unknown integration test')
    if row['id'].startswith('F'):
        check(bool(row.get('package_exit_scope')),row['id']+' missing package/integration boundary')
    check(set(row['requirements']) <= requirements, row['id'] + ' unknown requirement')
    if row['id'] in ['F01','F04','F05','F07']:
        check(row['status']=='partially_implemented_bounded_scope' and row.get('full_package_acceptance')=='pending' and bool(row.get('delivered_scope')),row['id']+' missing bounded delivery/future distinction')
        check((ROOT/row.get('delivery_evidence','')).is_file(),row['id']+' missing attributed engineering evidence')
    else:
        check(row['status']=='proposed_not_implemented',row['id']+' falsely claims implementation')
    for ref in row['current_refs']:
        p = ROOT / ref['file']
        check(p.is_file(), row['id'] + ' missing current file ' + ref['file'])
        check(ref['file'] in tracked, row['id'] + ' current path spelling/case absent from tracked inventory: ' + ref['file'])
        if p.is_file():
            check(ref['symbol'] in p.read_text(encoding='utf-8-sig'), row['id'] + ' symbol absent ' + ref['symbol'])

state = {}
index = {row['id']: row for row in w}
def visit(wid):
    if state.get(wid) == 1:
        errors.append('Dependency cycle at ' + wid)
        return
    if state.get(wid) == 2:
        return
    state[wid] = 1
    for dep in index[wid]['depends_on']:
        if dep in index:
            visit(dep)
    state[wid] = 2
for wid in sorted(wids):
    visit(wid)
checks.append('work/test/requirement IDs, acyclic dependencies and current tracked file/symbol references')
check('F03' in index['F05']['depends_on'],'Full F05 must depend on F03 committed bundle/revision')
check({'A35','A36','A37','A38'} <= tids,'Revision3 legacy/study/utility acceptance records missing')
test_index={row['id']:row for row in t}
check(test_index['A33']['test_class_or_record']=='PaidCraftStateTests','Paid craft fixture creation contract missing')
check('ClassBootstrapRouteTests' in json.dumps(test_index['A02']),'Core class-route discovery/execution target missing')
checks.append('bounded revision3 test ownership and F03->F05 assembly dependency')
def ancestors(wid):
    found=set()
    pending=list(index[wid]['depends_on'])
    while pending:
        dep=pending.pop()
        if dep in found or dep not in index:
            continue
        found.add(dep)
        pending.extend(index[dep]['depends_on'])
    return found
check('F04A' in wids and 'F04A' in index['F03']['depends_on'],'F03 overhaul missing early shared work provider')
check('F03' in index['F04']['depends_on'],'F04 downstream F03 dependency must not be reversed')
check(not {'F03','F04','F07','F08'} & ancestors('F04A'),'Early kernel has a downstream dependency')
providers=work_register.get('service_providers',{})
for name in ['WorkEligibility','WorkTransactionState','IWorkCommitPort','TrainingEventBus.RecordApplied']:
    check(providers.get(name)=='F04A','Wrong early provider for '+name)
for row in w:
    for use in row.get('interface_dependencies',[]):
        check(use['provider']==providers.get(use['service']),row['id']+' interface provider mismatch')
        check(use['provider'] in ancestors(row['id']),row['id']+' interface provider is not upstream: '+use['service'])
check('F04A' in index['F07']['depends_on'],'Study missing early RecordApplied/work dependency')
check('A39' in tids and index['F04A']['package_exit_tests']==['A39'],'Early primitive fixture ownership missing')
for tid,folder,cls,mode,expected_count in [
    ('A37','PlayMode','AuxiliaryUtilityViewPlayModeTests','PlayMode',4),
    ('A38','EditModeUnity','ManualStudyPanelTests','EditMode',3)]:
    targets=test_index[tid].get('additional_targets',[])
    expected_path='SynapticSea/Assets/_Project/Tests/'+folder+'/'+cls+'.cs'
    selected=[x for x in targets if x.get('target_path')==expected_path]
    check(len(selected)==1,tid+' required Unity fixture path missing')
    if selected:
        runner=selected[0]['runner']
        check(runner['mode']==mode and ('--mode '+mode) in runner['execute'],tid+' Unity mode mismatch')
        check(cls in runner['execute'] and '--output ' in runner['execute'],tid+' unscoped Unity runner')
        check(len(runner.get('expected_case_names',[]))==expected_count and 'count > 0' in runner.get('xml_requirement',''),tid+' named/nonzero XML assertion missing')
        asm=ROOT/'SynapticSea/Assets/_Project/Tests'/folder/('SynapticSea.Tests.'+folder+'.asmdef')
        check(asm.is_file(),tid+' actual Unity assembly absent')
defaults=work_register.get('implementation_defaults',{})
check(defaults.get('status')=='selected_for_provisional_implementation' and defaults.get('owner_approved_balance') is False and defaults.get('accepted_gameplay') is False,'Provisional selection falsely claims accepted balance/gameplay')
check('Never delete, overwrite' in defaults.get('migration_refusal_policy',''),'Legacy refusal preservation rule missing')
checks.append('C6 early kernel/record provider ancestors, retained F04 dependency, A37/A38 real assemblies/scoped XML and provisional default boundaries')

expected = {'CONTENT-%02d'%i for i in range(1,13)} | {'GEN-%02d'%i for i in range(1,4)} | {'UI-%d'%i for i in range(1,9)} | {'NEG-%02d'%i for i in range(1,7)} | {'INTEGRATED-%02d'%i for i in range(1,9)}
check(expected <= rids, 'Missing required findings/negatives: ' + ','.join(sorted(expected-rids)))
v7 = json.loads((ROOT/'docs/audit-deep/runtime/results-v7/model-results.json').read_text(encoding='utf-8-sig'))['results']
check({'DIAG-'+row['id'] for row in v7} <= rids, 'Missing v7 diagnostic trace')
for row in r:
    check(bool(row['work_packages']) and set(row['work_packages']) <= wids, row['id'] + ' unmapped work')
    check(bool(row['acceptance_tests']) and set(row['acceptance_tests']) <= tids, row['id'] + ' unmapped tests')
    check((ROOT/row['audit_source']).is_file(), row['id'] + ' missing audit evidence')
    for ref in row['evidence_refs']:
        p=ROOT/ref['file']
        check(p.is_file(), row['id'] + ' missing evidence path ' + ref['file'])
check(set().union(*(set(row['acceptance_tests']) for row in w)) == tids, 'Planned test without owning work')
with (OUT/'traceability.csv').open(encoding='utf-8-sig',newline='') as f:
    csvrows=list(csv.DictReader(f))
check({row['id'] for row in csvrows} == rids, 'CSV and JSON trace IDs disagree')
checks.append('all23 primary findings,8 integrated findings,6 retained negatives and21 v7 diagnostics mapped to work/tests')

c=read('catalog-dispositions.json')
ledger=json.loads((ROOT/'docs/audit-deep/content/dependency-ledger.json').read_text(encoding='utf-8-sig'))
check(len(c['recipes']) == 62 and {x['recipe_id'] for x in c['recipes']} == {x['recipe_id'] for x in ledger['recipes']}, 'Recipe denominator/IDs mismatch')
check(sum(x['source_blocked_upper_bound'] for x in c['recipes']) == 42, 'Blocked recipe bound mismatch')
check(len(c['missing_input_ids']) == 17 and len(c['items_without_enumerated_source']) == 31, 'Source gap denominator mismatch')
check(len(c['component_forms_without_registered_definition']) == 11, 'Component form denominator mismatch')
nozzle=next(x['audit_definition'] for x in c['recipes'] if x['recipe_id']=='craft_thruster_nozzle')
check(nozzle['knowledge_source']=='book' and nozzle['station_tier_min']==2 and nozzle['required_skill_level']==4, 'Nozzle gate correction lost')
check(all(x['natural_acquisition_certified'] is False for x in c['recipes']), 'Recipe evidence falsely promoted')
classes=read('class-bootstrap.json')['rows']
check(len(classes)==11 and len({x['class_id'] for x in classes})==11, 'Class denominator mismatch')
check(sum(x['title_fresh_available'] for x in classes)==8, 'Fresh title class count mismatch')
check(sum(x['finite_cache_model']['repair2'] for x in classes)==6, 'Corrected six-feasible/five-short baseline lost')
check(all(x['normal_traversal_witness'] is False for x in classes), 'Class diagnostic falsely promoted')
check(all(row['status']=='planned_not_run' for row in t), 'A planned test claims it ran')
checks.append('all62 recipe dispositions,17 missing inputs,31 sourceless items,11 forms and corrected11 class matrix')

specdir=ROOT/'docs/superpowers/specs'
plandir=ROOT/'docs/superpowers/plans'
docs=list(OUT.rglob('*.md'))+[specdir/'2026-10-02-synaptic-sea-master-design.md',specdir/'2026-10-02-synaptic-sea-foundation-design.md',plandir/'2026-10-02-synaptic-sea-foundation.md']
for p in docs:
    check(p.is_file(), 'Missing markdown ' + str(p))
    if not p.is_file():
        continue
    text=p.read_text(encoding='utf-8-sig')
    check(not re.search(r'\b(?:TODO|TBD|FIXME)\b',text), 'Unexplained placeholder in '+str(p))
    for target in re.findall(r'\[[^\]]+\]\(([^)]+)\)',text):
        target=target.split('#')[0]
        if not target or '://' in target:
            continue
        check((p.parent/target).resolve().exists(), 'Broken markdown link '+str(p)+': '+target)
checks.append('markdown links, required files and placeholder scan')

manifest_path=OUT/'artifact-hashes.json'
artifact_paths=sorted([p for p in OUT.rglob('*') if p.is_file() and p!=manifest_path]+[specdir/'2026-10-02-synaptic-sea-master-design.md',specdir/'2026-10-02-synaptic-sea-foundation-design.md',plandir/'2026-10-02-synaptic-sea-foundation.md'])
if args.verify_hashes:
    manifest=read('artifact-hashes.json')
    expected_artifacts={p.relative_to(ROOT).as_posix() for p in artifact_paths}
    check({row['path'] for row in manifest['files']}==expected_artifacts,'Artifact manifest file set differs from complete packet')
    for row in manifest['files']:
        p=ROOT/row['path']
        check(p.is_file() and p.stat().st_size==row['bytes'] and digest(p)==row['sha256'],'Artifact hash/size mismatch '+row['path'])
    checks.append('Complete finalized documentation artifact hash manifest verified')

documentation_errors=list(errors)
b=read('preservation-baseline.json')
current_head=git('rev-parse','HEAD')
scope=set(read('concurrent-engineering-scope.json')['paths']) if args.observe_concurrent_engineering else set()
baseline_paths=set(b['tracked_paths'])
changed_protected=[]
new_product=[]
new_evidence=[]
check(current_head==b['revision'] or args.observe_concurrent_engineering, 'HEAD changed from documentation baseline')
check(not (baseline_paths-tracked), 'Original tracked paths removed')
check(not (tracked-baseline_paths-scope), 'Unexpected new tracked paths')
for row in b['files']:
    p=ROOT/row['path']
    check(p.is_file(), 'Protected file missing '+row['path'])
    if p.is_file():
        actual=digest(p)
        if actual!=row['sha256']:
            changed_protected.append({'path':row['path'],'authoring_sha256':row['sha256'],'observed_sha256':actual,'in_admitted_scope':row['path'] in scope})
            check(row['path'] in scope, 'Protected change outside admitted engineering scope '+row['path'])
diff_paths=set(filter(None,git('diff','--name-only').splitlines()))
staged_paths=set(filter(None,git('diff','--cached','--name-only').splitlines()))
check(diff_paths<=scope,'Tracked diff outside admitted scope: '+','.join(sorted(diff_paths-scope)))
check(staged_paths<=scope,'Staged diff outside admitted scope: '+','.join(sorted(staged_paths-scope)))
if current_head!=b['revision']:
    commit_paths=set(filter(None,git('diff','--name-only',b['revision'],'HEAD').splitlines()))
    check(commit_paths<=scope,'Commit paths outside admitted scope: '+','.join(sorted(commit_paths-scope)))
untracked=[p for p in git('ls-files','--others','--exclude-standard','-z').split('\0') if p]
allowed=['docs/audit-deep/','docs/design/formal-build-2026-10-02/','docs/superpowers/specs/2026-10-02-synaptic-sea-','docs/superpowers/plans/2026-10-02-synaptic-sea-']
for p in untracked:
    if p in scope:
        (new_product if p.startswith('SynapticSea/') else new_evidence).append({'path':p,'observed_sha256':digest(ROOT/p),'in_admitted_scope':True,'tracked':False})
    else:
        check(p=='docs/whole-game-audit-2026-10-02.md' or any(p.startswith(s) for s in allowed),'New file outside packet/admitted engineering scope '+p)
for p in sorted((tracked-baseline_paths)&scope):
    (new_product if p.startswith('SynapticSea/') else new_evidence).append({'path':p,'observed_sha256':digest(ROOT/p),'in_admitted_scope':True,'tracked':True})
checks.append('%d authoring baseline hashes checked; %d admitted concurrent protected changes and %d unadmitted changes observed without restoring/rebaselining'%(len(b['files']),sum(x['in_admitted_scope'] for x in changed_protected),sum(not x['in_admitted_scope'] for x in changed_protected)))

report={'schema':'synaptic-design-validation-3','utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'revision':b['revision'],'approved_implementation_baseline':'a52a347b30c406e5beedd120f0ed7d46b49a5e97','observed_current_head':current_head,'status':'PASS' if not errors else 'FAIL','scope':'Documentation/reference validation with attributed bounded engineering; no product tests run by this task or campaign claim','counts':{'work_packages':len(w),'planned_tests':len(t),'trace_rows':len(r),'recipes':len(c['recipes']),'classes':len(classes),'protected_files':len(b['files']),'markdown_files':len(docs)},'checks':checks,'errors':errors,'preservation':{'authoring_baseline_retained':True,'unchanged_protected_count':len(b['files'])-len(changed_protected),'concurrent_engineering_mode':args.observe_concurrent_engineering,'changed_protected':changed_protected,'new_product_files':new_product,'new_engineering_evidence_files':new_evidence,'staged_paths':sorted(staged_paths),'product_changes_written_by_planning_task':False},'remaining_review_gates':['Bounded six-item revision3 closure per review-closure-v3.md','Concrete source/recovery/utility/study/overhaul and legacy-decision values before product edits','Actual home kit anchor qualification and underway narrow tier forwarding evidence pending','Later product choices remain outside closure','Supplementary Library helper metadata unavailable on Windows']}
report['documentation_status']='PASS' if not documentation_errors else 'FAIL'
report['approved_implementation_baseline']=read('concurrent-engineering-scope.json')['approved_implementation_baseline']
report['prior_bounded_implementation_baseline']='a52a347b30c406e5beedd120f0ed7d46b49a5e97'
report['remaining_review_gates']=['C6 targeted closure ready for final review; C1-C5 independently cleared','Selected provisional numerical defaults require measured survival and all-class route evidence, not additional owner balance approval','Production descriptor/root/occupancy and authored-row anchor qualification remain required','Broader time/atmosphere/succession/offline/colony/economy choices remain open','Supplementary Library helper metadata unavailable on Windows']
report['documentation_errors']=documentation_errors
report['preservation_status']='PASS' if len(errors)==len(documentation_errors) else 'PENDING_CONCURRENT_CLEANUP'
if args.report:
    (OUT/'validation-report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
if args.write_hashes and not documentation_errors:
    manifest={'schema':'synaptic-design-artifact-hashes-1','revision':b['revision'],'observed_current_head':current_head,'scope':'Complete documentation revision3 including registers/scripts/original baseline/concurrent observations; excludes this manifest. No implementation acceptance implied.','files':[{'path':p.relative_to(ROOT).as_posix(),'bytes':p.stat().st_size,'sha256':digest(p)} for p in artifact_paths]}
    manifest_path.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
sys.exit(1 if errors else 0)
