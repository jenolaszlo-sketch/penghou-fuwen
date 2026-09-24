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
# current IR, then verifies the execution fingerprint produced by .NET.
# The current IR is the only supported contract; legacy versioned vectors
# were removed as part of CI-2.
plan_path = Path(__file__).with_name("workflow_plan_v1.json")
plan = json.loads(plan_path.read_text(encoding="utf-8"))
assert plan["irVersion"] == "fuwen-ir/v1"
assert plan["compilerSemanticVersion"] == "compiler-semantics/1"
assert plan["fingerprintVersion"] == "fuwen-execution/v1"
assert plan["executionOrder"]["regions"][0]["phases"][-1]["nodePaths"] == [
    "answer/return_result"
]
inference = next(node for node in plan["nodes"] if node["$kind"] == "inference")
assert inference["contextSnapshots"] == []
assert inference["contextRequirements"][0]["name"] == "context"
assert inference["protocol"]["limits"]["maxTurns"] == 1
assert inference["protocol"]["limits"]["maxModelCalls"] == 1
canonical_plan = json.dumps(
    plan, ensure_ascii=True, separators=(",", ":"), sort_keys=True
).encode("utf-8")
assert canonical_plan == plan_path.read_bytes().rstrip(b"\r\n")
plan_digest = hashlib.sha256(canonical_plan).hexdigest()
assert plan_digest == "abefd1352b2211428a098711f49b813a97c71babe10368184b2169a9f73b7cfb"

# Run the expanded independent numeric and Unicode portability vectors as part
# of the existing CI golden-vector entry point.
runpy.run_path(str(Path(__file__).with_name("canonical_json_v1_portability.py")))
