"""
Correlation ID extraction verification against real log samples.
Simulates the same regexes and DisjointSet logic from LogIdExtractor in Pipeline.cs.
"""
import re
import json
from collections import defaultdict

# ── Same regexes as LogIdExtractor (Pipeline.cs) ──
GUID_REGEX = re.compile(r'\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b')

# KeyValue: request_id|correlation_id|transaction_id|trace_id|brn := value (now includes : in capture)
KEY_VALUE_ID_REGEX = re.compile(
    r'\b(?:request_?id|correlation_?id|transaction_?id|trace_?id|brn)[:= ]+\s*([a-zA-Z0-9/\-._:]+)\b',
    re.IGNORECASE)

# APM: Id|TraceId|TransactionId|ParentId := hex{16,32}
APM_ID_REGEX = re.compile(
    r'\b(?:Id|TraceId|TransactionId|ParentId)[:= ]+\s*([a-fA-F0-9]{16,32})\b',
    re.IGNORECASE)

# Kestrel: 0HNLLJNFUULGS:00000001
KESTREL_REGEX = re.compile(r'\b[a-zA-Z0-9]{8,20}:\d{4,10}\b')

# ── DisjointSet (Union-Find) ──
class DisjointSet:
    def __init__(self):
        self.parent = {}
    def find(self, i):
        if i not in self.parent:
            self.parent[i] = i
            return i
        if self.parent[i] == i:
            return i
        self.parent[i] = self.find(self.parent[i])
        return self.parent[i]
    def union(self, i, j):
        ri, rj = self.find(i), self.find(j)
        if ri != rj:
            self.parent[ri] = rj
    def components(self):
        comps = defaultdict(set)
        for k in self.parent:
            comps[self.find(k)].add(k)
        return comps

def extract_ids(text):
    """Same logic as LogIdExtractor.ExtractFromText"""
    ids = set()
    for m in GUID_REGEX.finditer(text):
        ids.add(m.group())
    for m in KEY_VALUE_ID_REGEX.finditer(text):
        val = m.group(1).strip().strip('"')
        if val and val.lower() not in ('null', 'undefined', 'n/a'):
            ids.add(val)
    for m in APM_ID_REGEX.finditer(text):
        val = m.group(1).strip().strip('"')
        if val and val.lower() not in ('null', 'undefined', 'n/a'):
            ids.add(val)
    for m in KESTREL_REGEX.finditer(text):
        ids.add(m.group())
    return ids

# ── Read all sample logs ──
log_dir = "sample-logs"
files = {
    "app.log": "app",
    "ACL.log": "anti-corruption-layer",
    "Ocelot.log": "ocelot",
    "SourceOfFund.log": "source-of-fund",
}

all_logs = []  # (source_id, source_type, text)
for filename, source_type in files.items():
    try:
        with open(f"{log_dir}/{filename}", "r", encoding="utf-8") as f:
            # Read lines — simulate FileLogSource event detection
            for i, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                all_logs.append((filename, source_type, line))
    except FileNotFoundError:
        pass

print(f"Total log lines read: {len(all_logs)}")

# ── Extract IDs from each log line ──
dsu = DisjointSet()
log_ids = []  # (line_index, source_id, ids_set)
total_with_ids = 0

for idx, (source_id, source_type, text) in enumerate(all_logs):
    ids = extract_ids(text)
    if ids:
        total_with_ids += 1
        id_list = list(ids)
        # Union all IDs found in this log line
        for i in range(1, len(id_list)):
            dsu.union(id_list[0], id_list[i])
        log_ids.append((source_id, text[:120], ids))

print(f"Lines with correlation IDs: {total_with_ids}")

# ── Show connected components ──
components = dsu.components()
print(f"\nConnected components (correlation groups): {len(components)}")

# Show components with multiple related logs
multi_log_components = []
for root, ids in components.items():
    if len(ids) >= 2:
        multi_log_components.append((root, ids))

multi_log_components.sort(key=lambda x: -len(x[1]))
print(f"Components linking 2+ IDs: {len(multi_log_components)}")

# Show top components
print("\n" + "="*80)
print("TOP CORRELATION CHAINS (multi-log groups)")
print("="*80)
for i, (root, ids) in enumerate(multi_log_components[:8]):
    display_ids = sorted(ids)[:8]
    print(f"\nGroup {i+1}: {len(ids)} linked IDs")
    for id_val in display_ids[:5]:
        # Find which source files reference this ID
        sources = set()
        for src_id, text, log_ids_set in log_ids:
            if id_val in log_ids_set:
                sources.add(src_id)
        print(f"  └─ {id_val[:60]}  ← found in: {', '.join(sorted(sources))}")

# ── Show analysis by source type ──
print("\n" + "="*80)
print("CORRELATION COVERAGE BY SOURCE")
print("="*80)
source_stats = defaultdict(lambda: {"total": 0, "with_ids": 0, "id_types": defaultdict(int)})
for source_id, source_type, text in all_logs:
    source_stats[source_id]["total"] += 1
    ids = extract_ids(text)
    if ids:
        source_stats[source_id]["with_ids"] += 1
        for id_val in ids:
            if GUID_REGEX.fullmatch(id_val):
                source_stats[source_id]["id_types"]["GUID"] += 1
            elif KESTREL_REGEX.fullmatch(id_val):
                source_stats[source_id]["id_types"]["KestrelRequestId"] += 1
            else:
                source_stats[source_id]["id_types"]["other"] += 1

for src, stats in sorted(source_stats.items()):
    pct = stats["with_ids"] / stats["total"] * 100 if stats["total"] else 0
    print(f"  {src}: {stats['with_ids']}/{stats['total']} lines have IDs ({pct:.0f}%)")
    for id_type, count in sorted(stats["id_types"].items()):
        print(f"    {id_type}: {count}")

# ── Show a concrete example: a complete correlation chain ──
print("\n" + "="*80)
print("EXAMPLE: Full correlation chain for one CorrelationId")
print("="*80)
if multi_log_components:
    # Take the first component with at least 3 IDs
    example = None
    for root, ids in multi_log_components:
        if len(ids) >= 3:
            example = (root, ids)
            break
    if example:
        root, ids = example
        # Find all log lines that reference any ID in this component
        related_logs = []
        for src_id, text, log_ids_set in log_ids:
            if log_ids_set & ids:
                related_logs.append((src_id, text, log_ids_set & ids))
        
        print(f"Component has {len(ids)} IDs, referenced by {len(related_logs)} log lines")
        for src_id, text, shared_ids in related_logs[:8]:
            preview = text[:130].replace('\n', '\\n')
            print(f"  [{src_id}] {preview}...")
            print(f"        shared IDs: {', '.join(sorted(shared_ids))[:80]}")
else:
    print("  No multi-ID components found.")

print("\n✅ Correlation chain verification complete.")
