"""Independent test-only reference for a canonical JSON golden vector.

This deliberately does not import or invoke .NET. The complete cross-language
numeric suite will be added before the canonical contract is published.
"""

import hashlib
import json
import runpy
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

# V2 is a separate executable contract.  It has an explicit region/phase
# schedule; this vector is independently parsed and canonicalized without
# importing the .NET implementation.
v2_plan_path = Path(__file__).with_name("workflow_plan_v2.json")
v2_plan = json.loads(v2_plan_path.read_text(encoding="utf-8"))
assert v2_plan["irVersion"] == "fuwen-ir/v2"
assert v2_plan["compilerSemanticVersion"] == "compiler-semantics/2"
assert v2_plan["fingerprintVersion"] == "fuwen-execution/v2"
assert v2_plan["executionOrder"]["regions"][0]["phases"][-1]["nodePaths"] == [
    "answer/return_result"
]
canonical_v2_plan = json.dumps(
    v2_plan, ensure_ascii=True, separators=(",", ":"), sort_keys=True
).encode("utf-8")
assert canonical_v2_plan == v2_plan_path.read_bytes().rstrip(b"\r\n")
v2_plan_digest = hashlib.sha256(canonical_v2_plan).hexdigest()
assert v2_plan_digest == "2bf5c628bcecfdb0970730bc160ee10f2bcbf326875f30430e5b832fafe96571"

# V3 is a separate pre-release executable contract. Context dependencies are
# named, typed requirements and legacy contextSnapshots remain empty.
v3_plan_path = Path(__file__).with_name("workflow_plan_v3.json")
v3_plan = json.loads(v3_plan_path.read_text(encoding="utf-8"))
assert v3_plan["irVersion"] == "fuwen-ir/v3"
assert v3_plan["compilerSemanticVersion"] == "compiler-semantics/3"
assert v3_plan["fingerprintVersion"] == "fuwen-execution/v3"
inference = next(node for node in v3_plan["nodes"] if node["$kind"] == "inference")
assert inference["contextSnapshots"] == []
assert inference["contextRequirements"][0]["name"] == "answer_context"
canonical_v3_plan = json.dumps(
    v3_plan, ensure_ascii=True, separators=(",", ":"), sort_keys=True
).encode("utf-8")
assert canonical_v3_plan == v3_plan_path.read_bytes().rstrip(b"\r\n")
v3_plan_digest = hashlib.sha256(canonical_v3_plan).hexdigest()
assert v3_plan_digest == "53134b2d7566a22889e5a751b27a6367f8159b1686baf4e6d6260e99acfd0c33"

# Run the expanded independent numeric and Unicode portability vectors as part
# of the existing CI golden-vector entry point.
runpy.run_path(str(Path(__file__).with_name("canonical_json_v1_portability.py")))
