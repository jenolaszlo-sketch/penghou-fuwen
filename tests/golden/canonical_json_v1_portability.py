"""Independent portability vectors for penghou-canonical-json/v1.

This oracle intentionally does not import the .NET implementation.  Its JSON
string encoder follows the default .NET JavaScriptEncoder block list, while
the accepted numeric vectors stay inside the explicitly lossless decimal
range used by Fuwen v1.
"""

import hashlib
import json
from decimal import Decimal
from pathlib import Path


def escape_string(value: str) -> str:
    result = ['"']
    for character in value:
        code = ord(character)
        if character == '"':
            result.append(r"\u0022")
        elif character == "\\":
            result.append(r"\\")
        elif code < 0x20 or code in (0x7F,):
            result.append(f"\\u{code:04X}")
        elif character in "<>&'":
            result.append(f"\\u{code:04X}")
        elif code <= 0x7F:
            result.append(character)
        elif code <= 0xFFFF:
            result.append(f"\\u{code:04X}")
        else:
            scalar = code - 0x10000
            result.append(f"\\u{0xD800 + (scalar >> 10):04X}")
            result.append(f"\\u{0xDC00 + (scalar & 0x3FF):04X}")
    result.append('"')
    return ''.join(result)


def canonical_number(value: Decimal) -> str:
    if value == 0:
        return "0"
    if value.adjusted() < -4 or value.adjusted() >= 29:
        return format(value.normalize(), "E")
    text = format(value, "f")
    if "." in text:
        text = text.rstrip("0").rstrip(".")
    return text


def canonical(value):
    if isinstance(value, dict):
        ordered = sorted(value.items(), key=lambda item: item[0].encode("utf-16-be"))
        return "{" + ",".join(escape_string(key) + ":" + canonical(item) for key, item in ordered) + "}"
    if isinstance(value, list):
        return "[" + ",".join(canonical(item) for item in value) + "]"
    if isinstance(value, str):
        return escape_string(value)
    if isinstance(value, Decimal):
        return canonical_number(value)
    if value is True:
        return "true"
    if value is False:
        return "false"
    if value is None:
        return "null"
    raise TypeError(type(value))


vectors = [
    (
        "decimal-precision-boundary",
        '{"value":0.1234567890123456789012345678}',
        '{"value":0.1234567890123456789012345678}',
        "d115ce19f5f21df14db1b11c4f459bb8d5c7f990482a65d0b17ef7fa84ac2fcc",
    ),
    (
        "decimal-range-boundary",
        '{"value":79228162514264337593543950335}',
        '{"value":79228162514264337593543950335}',
        "5a8fa6b318b0e9294bff0600107886cc87081660d6a9522827818c478ab3b99f",
    ),
    (
        "exponents-and-negative-zero",
        '{"tiny":1e-28,"huge":1e+28,"zero":-0.0,"equivalent":1.2300e+3}',
        '{"equivalent":1230,"huge":10000000000000000000000000000,"tiny":1E-28,"zero":0}',
        "528e9d27a2b9cafbbe249ca361a72fefe6fe062ab1048cc05b44d4d7a2eadfea",
    ),
    (
        "unicode-escaping-and-property-order",
        '{"😀":"é😀<>&\'\\"\\\\/\\u0001\\u2028","a":"value","A":"value"}',
        '{"A":"value","a":"value","\\uD83D\\uDE00":"\\u00E9\\uD83D\\uDE00\\u003C\\u003E\\u0026\\u0027\\u0022\\\\/\\u0001\\u2028"}',
        "efddeb97585d0c34157e51629ee165bf73714c8fbf077064b0b764457ce46ec1",
    ),
]

for name, source, expected, expected_digest in vectors:
    value = json.loads(source, parse_int=Decimal, parse_float=Decimal)
    actual = canonical(value)
    assert actual == expected, (
        f"{name}: canonical mismatch; actual={actual!r}, expected={expected!r}"
    )
    actual_digest = hashlib.sha256(actual.encode("utf-8")).hexdigest()
    assert actual_digest == expected_digest, (
        f"{name}: digest mismatch; actual={actual_digest}, expected={expected_digest}, canonical={actual!r}"
    )

# Keep the original independent full-plan checks in this script as well.
root = Path(__file__).parent
for filename, expected_digest in (
    ("workflow_plan_v1.json", "e3a76cc4128c703637fd14555e95725908f3170c69831033413f5f9a11bfad8a"),
    ("workflow_plan_v2.json", "2bf5c628bcecfdb0970730bc160ee10f2bcbf326875f30430e5b832fafe96571"),
):
    plan = json.loads((root / filename).read_text(encoding="utf-8"), parse_int=Decimal, parse_float=Decimal)
    actual = canonical(plan).encode("utf-8")
    assert actual == (root / filename).read_bytes().rstrip(b"\r\n")
    assert hashlib.sha256(actual).hexdigest() == expected_digest
