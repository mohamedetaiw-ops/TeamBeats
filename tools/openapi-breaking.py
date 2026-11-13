#!/usr/bin/env python3
"""Fails when a public OpenAPI document breaks clients of the same major version (spec 035 FR-014).

Usage: openapi-breaking.py BASE HEAD

Within a major version only additive changes are allowed. Breaking changes found here:
- an operation (path and method) removed;
- a parameter removed, or a parameter added as required or changed to required;
- a request body that becomes required, or a request schema that gains a required property;
- a property removed from a schema, or a value removed from an enum;
- a success response removed.
"""
import json
import sys

METHODS = ("get", "put", "post", "delete", "patch", "head", "options")


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def resolve(doc, schema):
    seen = set()
    while isinstance(schema, dict) and "$ref" in schema:
        ref = schema["$ref"]
        if ref in seen:
            return {}
        seen.add(ref)
        node = doc
        for part in ref.lstrip("#/").split("/"):
            node = node.get(part, {})
        schema = node
    return schema if isinstance(schema, dict) else {}


def compare_schema(base_doc, head_doc, base, head, where, request, problems, depth=0):
    if depth > 12:
        return
    base, head = resolve(base_doc, base), resolve(head_doc, head)
    head_enum = head.get("enum", base.get("enum", []))
    for value in base.get("enum", []):
        if value not in head_enum:
            problems.append(f"{where}: enum value {value!r} removed")
    base_props, head_props = base.get("properties", {}), head.get("properties", {})
    for name, prop in base_props.items():
        if name not in head_props:
            problems.append(f"{where}: property {name!r} removed")
        else:
            compare_schema(base_doc, head_doc, prop, head_props[name], f"{where}.{name}", request, problems, depth + 1)
    if request:
        for name in sorted(set(head.get("required", [])) - set(base.get("required", []))):
            problems.append(f"{where}: property {name!r} is now required")
    if "items" in base and "items" in head:
        compare_schema(base_doc, head_doc, base["items"], head["items"], f"{where}[]", request, problems, depth + 1)


def body_schema(doc, body):
    content = resolve(doc, body).get("content", {})
    for media in ("application/json", "multipart/form-data"):
        if media in content:
            return content[media].get("schema", {})
    return next(iter(content.values()), {}).get("schema", {}) if content else {}


def breaking_changes(base, head):
    problems = []
    for path, base_item in base.get("paths", {}).items():
        head_item = head.get("paths", {}).get(path)
        for method in METHODS:
            if method not in base_item:
                continue
            op = f"{method.upper()} {path}"
            if head_item is None or method not in head_item:
                problems.append(f"{op}: operation removed")
                continue
            b, h = base_item[method], head_item[method]
            b_params = {(p.get("in"), p.get("name")): p for p in (resolve(base, p) for p in b.get("parameters", []))}
            h_params = {(p.get("in"), p.get("name")): p for p in (resolve(head, p) for p in h.get("parameters", []))}
            for key, p in b_params.items():
                if key not in h_params:
                    problems.append(f"{op}: parameter {key[1]!r} removed")
                elif h_params[key].get("required") and not p.get("required"):
                    problems.append(f"{op}: parameter {key[1]!r} is now required")
            for key, p in h_params.items():
                if key not in b_params and p.get("required"):
                    problems.append(f"{op}: new required parameter {key[1]!r}")
            if "requestBody" in h:
                if resolve(head, h["requestBody"]).get("required") and not resolve(base, b.get("requestBody", {})).get("required"):
                    problems.append(f"{op}: request body is now required")
                if "requestBody" in b:
                    compare_schema(base, head, body_schema(base, b["requestBody"]), body_schema(head, h["requestBody"]), f"{op} request", True, problems)
            for status, response in b.get("responses", {}).items():
                if not status.startswith("2"):
                    continue
                if status not in h.get("responses", {}):
                    problems.append(f"{op}: response {status} removed")
                    continue
                compare_schema(base, head, body_schema(base, response), body_schema(head, h["responses"][status]), f"{op} {status}", False, problems)
    return problems


def main(base_path, head_path):
    problems = breaking_changes(load(base_path), load(head_path))
    for problem in problems:
        print(f"breaking: {problem}")
    if problems:
        print(f"{len(problems)} breaking change(s) in the public API. Add an operation or a field instead, or start a new major version.")
        return 1
    print("No breaking change in the public API.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2]))
