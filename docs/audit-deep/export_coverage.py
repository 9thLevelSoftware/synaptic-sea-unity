"""Audit-only evidence reconciliation; citing a symbol does not imply whole-file review."""
import csv,json,pathlib,re,collections
p=pathlib.Path(__file__).resolve().parent
rows=list(csv.DictReader((p/'first-party-files.csv').open(encoding='utf-8')))
citations=collections.defaultdict(list)
def visit(node):
    if isinstance(node,dict):
        if isinstance(node.get('file'),str) and 'symbol' in node:
            entry={k:node[k] for k in ('file','line','symbol','revision') if k in node}
            if entry not in citations[node['file']]:citations[node['file']].append(entry)
        for value in node.values():visit(value)
    elif isinstance(node,list):
        for value in node:visit(value)
for name in ['content/dependency-ledger.json','content/findings-and-traces.json','content/predeparture-repair-producers.json']:
    visit(json.loads((p/name).read_text(encoding='utf-8')))
for row in rows:
    row['coverage']='specific_symbols_cited_not_whole_file' if row['path'] in citations else 'inventoried_not_semantically_certified'
    row['evidence_symbols']=len(citations.get(row['path'],[]))
with (p/'reconciled-file-coverage.csv').open('w',newline='',encoding='utf-8') as f:
    writer=csv.DictWriter(f,fieldnames=rows[0].keys());writer.writeheader();writer.writerows(rows)
(p/'trace-evidence-index.json').write_text(json.dumps(citations,indent=2),encoding='utf-8')
summary={'revision':'840f14fb4450dcfba2a0bbce7cb8527e7dfa8e48','indexed_files':len(rows),'nonmeta_files':sum(r['extension']!='.meta' for r in rows),'files_with_exact_catalog_producer_evidence':sum(r['path'] in citations for r in rows),'nonmeta_without_reconciled_exact_symbol_evidence':sum(r['extension']!='.meta' and r['path'] not in citations for r in rows),'limits':['Exact-symbol reconciliation covers machine-readable content/producer ledgers only; generation/UI/root reports add scoped evidence not automatically promoted to exhaustive review.','All unknown rows remain enumerated; no completion percentage or whole-file semantic review claim.','Compilation, JSON parsing, GUID resolution and hashing are separate from live registration and normal use.']}
(p/'coverage-reconciliation.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
print(json.dumps(summary))
