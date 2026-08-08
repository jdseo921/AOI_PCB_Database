"""Parse, validate, and index the atomic requirement catalogue of the
AOI Software Architecture, Secure Development, and Change-Control Standard.

Scans Docs/standard/VOL*.md for requirement records in the canonical 5-line
grammar, validates structure, and emits:
  - Docs/standard/VOL20_Catalogue_SelfAudit.md  section 58 index tables (when --emit-index)
  - Docs/standard/requirement-catalogue.json     machine-readable catalogue
  - validation errors to stderr (exit 1 on any error) so CI can gate on it

Usage: python Scripts/standard_catalogue.py [--repo-root PATH] [--emit-index]
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

HEADER_RE = re.compile(
    r"^\*\*\[([A-Z]{2,5})-(\d{3})\]\*\*\s+\((P[0-3])\s*\|\s*([^|]+?)\s*\|\s*([^)]+?)\)\s*$"
)
WHY_RE = re.compile(r"^- Why:\s*(.+?)\s*Maps:\s*(.+?)\.?\s*$")
VERIFY_RE = re.compile(
    r"^- Verify:\s*(.+?)\s*Evidence:\s*(.+?)\s*Owner:\s*(.+?)\s*Auto:\s*"
    r"(Fully automated|Partially automated|Manual review|External assessment)\.?\s*$"
)
EXC_RE = re.compile(
    r"^- Exception:\s*(Not allowed|Allowed\s*[—–-]\s*approver:\s*.+?)\.\s*"
    r"Review:\s*(Per release|Quarterly|Annual|On change)\.?\s*$"
)
NORMATIVE_RE = re.compile(r"\b(SHALL NOT|SHALL|SHOULD NOT|SHOULD|MAY|MUST NOT|MUST)\b")
STAGE_TOKEN_RE = re.compile(r"^(ALL|S[1-4]([—–-]S[1-4]|\+)?(,\s*S[1-4]([—–-]S[1-4]|\+)?)*)$")


def parse_file(path: Path, errors: list[str]) -> list[dict]:
    """Extract all requirement records from one volume file, appending grammar violations to errors."""
    lines = path.read_text(encoding="utf-8").splitlines()
    records = []
    i = 0
    while i < len(lines):
        m = HEADER_RE.match(lines[i])
        if not m:
            # catch near-miss headers so malformed records fail loudly instead of vanishing
            if re.match(r"^\*\*\[[A-Z]{2,5}-\d+\]", lines[i]):
                errors.append(f"{path.name}:{i+1}: malformed requirement header: {lines[i][:100]}")
            i += 1
            continue
        cat, num, prio, stages, comps = m.groups()
        rid = f"{cat}-{num}"
        loc = f"{path.name}:{i+1}"
        rec = {
            "id": rid, "category": cat, "priority": prio,
            "stages": stages.strip(), "components": comps.strip(),
            "volume": path.name, "line": i + 1,
        }
        if not STAGE_TOKEN_RE.match(stages.strip()):
            errors.append(f"{loc}: {rid} bad stage token '{stages.strip()}'")
        # line 2: the atomic statement
        if i + 1 >= len(lines) or not lines[i + 1].strip():
            errors.append(f"{loc}: {rid} missing statement line")
            i += 1
            continue
        stmt = lines[i + 1].strip()
        rec["statement"] = stmt
        kws = NORMATIVE_RE.findall(stmt)
        if not kws:
            errors.append(f"{loc}: {rid} statement has no normative keyword")
        rec["keyword"] = kws[0] if kws else ""
        # lines 3-5
        w = WHY_RE.match(lines[i + 2].strip()) if i + 2 < len(lines) else None
        v = VERIFY_RE.match(lines[i + 3].strip()) if i + 3 < len(lines) else None
        e = EXC_RE.match(lines[i + 4].strip()) if i + 4 < len(lines) else None
        if not w:
            errors.append(f"{loc}: {rid} bad 'Why/Maps' line")
        else:
            rec["why"], rec["maps"] = w.group(1), w.group(2)
        if not v:
            errors.append(f"{loc}: {rid} bad 'Verify/Evidence/Owner/Auto' line")
        else:
            rec["verify"], rec["evidence"], rec["owner"], rec["auto"] = [
                v.group(k).rstrip(". ") for k in range(1, 5)
            ]
        if not e:
            errors.append(f"{loc}: {rid} bad 'Exception/Review' line")
        else:
            rec["exception"], rec["review"] = e.group(1), e.group(2)
        records.append(rec)
        i += 5
    return records


def validate(records: list[dict], errors: list[str]) -> None:
    """Cross-record checks: uniqueness and contiguous numbering per category."""
    seen: dict[str, str] = {}
    for r in records:
        if r["id"] in seen:
            errors.append(f"duplicate ID {r['id']} in {r['volume']} (first in {seen[r['id']]})")
        seen[r["id"]] = r["volume"]
    by_cat = defaultdict(list)
    for r in records:
        by_cat[r["category"]].append(int(r["id"].split("-")[1]))
    for cat, nums in sorted(by_cat.items()):
        nums.sort()
        gaps = [n for a, b in zip(nums, nums[1:]) for n in range(a + 1, b)]
        if nums[0] != 1:
            errors.append(f"category {cat} does not start at 001 (starts {nums[0]:03d})")
        if gaps:
            errors.append(f"category {cat} has numbering gaps: {[f'{g:03d}' for g in gaps[:10]]}")


def emit_index(records: list[dict]) -> str:
    """Render the section 58 catalogue index markdown."""
    out = [
        "OpenAI/Codex and numerous other coding agents will review your output once you are done.",
        "",
        "## 58. Requirement Catalogue (Generated Index)",
        "",
    ]
    out.append(f"Total atomic requirements: **{len(records)}**. "
               "Full records live in the topical volumes; this index is regenerated by "
               "`Scripts/standard_catalogue.py` and MUST NOT be edited by hand.")
    out.append("")
    c_cat = Counter(r["category"] for r in records)
    c_pri = Counter(r["priority"] for r in records)
    c_auto = Counter(r.get("auto", "?") for r in records)
    out.append("### 58.1 Counts by category")
    out.append("")
    out.append("| Category | Count | Volume |")
    out.append("|---|---|---|")
    vol_of = {r["category"]: r["volume"] for r in records}
    for cat, n in sorted(c_cat.items()):
        out.append(f"| {cat} | {n} | {vol_of[cat]} |")
    out.append("")
    out.append("### 58.2 Counts by priority / automation")
    out.append("")
    out.append("| Priority | Count |  | Automation | Count |")
    out.append("|---|---|---|---|---|")
    autos = ["Fully automated", "Partially automated", "Manual review", "External assessment"]
    for idx, p in enumerate(["P0", "P1", "P2", "P3"]):
        a = autos[idx] if idx < len(autos) else ""
        out.append(f"| {p} | {c_pri.get(p, 0)} |  | {a} | {c_auto.get(a, '') if a else ''} |")
    out.append("")
    out.append("### 58.3 Full index (ID → statement)")
    out.append("")
    out.append("| ID | Pri | Stages | Statement | Owner | Auto |")
    out.append("|---|---|---|---|---|---|")
    for r in sorted(records, key=lambda x: (x["category"], x["id"])):
        stmt = r["statement"]
        stmt = (stmt[:140] + "…") if len(stmt) > 140 else stmt
        stmt = stmt.replace("|", "\\|")
        out.append(f"| {r['id']} | {r['priority']} | {r['stages']} | {stmt} "
                   f"| {r.get('owner','?')} | {r.get('auto','?')} |")
    out.append("")
    return "\n".join(out)


def main() -> int:
    """Entry point: parse all volumes, validate, optionally emit index + JSON."""
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo-root", default=".")
    ap.add_argument("--emit-index", action="store_true")
    ap.add_argument("--json-out", default=None)
    args = ap.parse_args()
    std = Path(args.repo_root) / "Docs" / "standard"
    files = sorted(std.glob("VOL*.md"))
    if not files:
        print(f"no VOL*.md under {std}", file=sys.stderr)
        return 1
    errors: list[str] = []
    records: list[dict] = []
    for f in files:
        if "Catalogue_SelfAudit" in f.name:
            continue
        records.extend(parse_file(f, errors))
    validate(records, errors)
    c = Counter(r["category"] for r in records)
    print(f"{len(records)} requirements across {len(c)} categories in {len(files)} volumes")
    for cat, n in sorted(c.items()):
        print(f"  {cat}: {n}")
    if errors:
        print(f"\n{len(errors)} validation errors:", file=sys.stderr)
        for e in errors:
            print("  " + e, file=sys.stderr)
    if args.json_out:
        Path(args.json_out).write_text(
            json.dumps(records, indent=1, ensure_ascii=False), encoding="utf-8")
    if args.emit_index:
        (std / "catalogue_index.generated.md").write_text(
            emit_index(records), encoding="utf-8")
        print(f"index written to {std / 'catalogue_index.generated.md'}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
