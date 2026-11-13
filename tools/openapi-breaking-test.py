#!/usr/bin/env python3
"""Checks of tools/openapi-breaking.py: additive changes pass, each kind of breaking change is found."""
import copy
import importlib.util
import pathlib
import sys

spec = importlib.util.spec_from_file_location("breaking", pathlib.Path(__file__).with_name("openapi-breaking.py"))
breaking = importlib.util.module_from_spec(spec)
spec.loader.exec_module(breaking)

BASE = {
    "paths": {
        "/api/public/v1/items": {
            "get": {
                "parameters": [{"in": "query", "name": "limit", "required": False}],
                "responses": {"200": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/Page"}}}}},
            },
            "post": {
                "requestBody": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/New"}}}},
                "responses": {"201": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/Item"}}}}},
            },
        }
    },
    "components": {
        "schemas": {
            "Page": {"properties": {"items": {"items": {"$ref": "#/components/schemas/Item"}}, "nextCursor": {}}},
            "Item": {"properties": {"id": {}, "kind": {"enum": ["news", "page"]}}},
            "New": {"properties": {"title": {}}, "required": ["title"]},
        }
    },
}


def changed(change):
    head = copy.deepcopy(BASE)
    change(head)
    return breaking.breaking_changes(BASE, head)


def main():
    failures = []

    def expect(name, change, broken):
        found = changed(change)
        if bool(found) != broken:
            failures.append(f"{name}: expected {'a breaking change' if broken else 'none'}, found {found}")

    expect("unchanged", lambda h: None, False)
    expect("new operation", lambda h: h["paths"].update({"/api/public/v1/other": {"get": {"responses": {}}}}), False)
    expect("new optional parameter", lambda h: h["paths"]["/api/public/v1/items"]["get"]["parameters"].append({"in": "query", "name": "q"}), False)
    expect("new response property", lambda h: h["components"]["schemas"]["Item"]["properties"].update({"title": {}}), False)
    expect("new enum value", lambda h: h["components"]["schemas"]["Item"]["properties"]["kind"]["enum"].append("event"), False)
    expect("operation removed", lambda h: h["paths"]["/api/public/v1/items"].pop("post"), True)
    expect("parameter removed", lambda h: h["paths"]["/api/public/v1/items"]["get"].update({"parameters": []}), True)
    expect("parameter now required", lambda h: h["paths"]["/api/public/v1/items"]["get"]["parameters"][0].update({"required": True}), True)
    expect("new required parameter", lambda h: h["paths"]["/api/public/v1/items"]["get"]["parameters"].append({"in": "query", "name": "q", "required": True}), True)
    expect("property removed in a list", lambda h: h["components"]["schemas"]["Item"]["properties"].pop("id"), True)
    expect("enum value removed", lambda h: h["components"]["schemas"]["Item"]["properties"]["kind"].update({"enum": ["news"]}), True)
    expect("request property now required", lambda h: h["components"]["schemas"]["New"]["properties"].update({"body": {}}) or h["components"]["schemas"]["New"].update({"required": ["title", "body"]}), True)
    expect("success response removed", lambda h: h["paths"]["/api/public/v1/items"]["post"].update({"responses": {"200": {}}}), True)

    for failure in failures:
        print(failure)
    print("openapi-breaking checks:", "failed" if failures else "passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
