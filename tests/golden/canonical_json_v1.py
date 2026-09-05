"""Independent test-only reference for a canonical JSON golden vector.

This deliberately does not import or invoke .NET. The complete cross-language
numeric suite will be added before the canonical contract is published.
"""

import hashlib
import json
from pathlib import Path

source = '{"z":1,"a":{"x":"value"},"items":[3,2,1]}'
expected = '{"a":{"x":"value"},"items":[3,2,1],"z":1}'
actual = json.dumps(
    json.loads(source), ensure_ascii=True, separators=(",", ":"), sort_keys=True
)

assert actual == expected
digest = hashlib.sha256(actual.encode("utf-8")).hexdigest()
assert digest == "170b9d495b27a65dfc1c6860caf46f7be9a3d2bea7276137a34704a77d010bbd"

# Full-plan vector: Python independently parses and canonicalizes the checked-in
# resolved IR, then verifies the execution fingerprint produced by .NET.
plan_path = Path(__file__).with_name("workflow_plan_v1.json")
plan = json.loads(plan_path.read_text(encoding="utf-8"))
canonical_plan = json.dumps(
    plan, ensure_ascii=True, separators=(",", ":"), sort_keys=True
).encode("utf-8")
assert canonical_plan == plan_path.read_bytes().rstrip(b"\r\n")
plan_digest = hashlib.sha256(canonical_plan).hexdigest()
assert plan_digest == "e3a76cc4128c703637fd14555e95725908f3170c69831033413f5f9a11bfad8a"
