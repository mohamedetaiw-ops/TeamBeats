# Template document, format version 1

Spec 032 FR-022 (R2 T028). A tenant template designer exports a template with `GET /api/v1/experience/templates/{id}/export` and imports one with `POST /api/v1/experience/templates/import`. Both need `template.edit` for the whole tenant and the page builder in the package.

## Shape

```json
{
  "format": "teambeats.template",
  "formatVersion": 1,
  "id": "gulf-news",
  "basedOn": "T02",
  "basedOnVersion": "1",
  "names": { "en": "Gulf news", "ar": "أخبار الخليج" },
  "descriptions": { "en": "News pages for the Gulf companies." },
  "variants": ["standard", "wide"],
  "regions": [
    {
      "id": "main",
      "policy": {
        "allowedCategories": ["content", "media"],
        "minPlacements": 0,
        "maxPlacements": 12,
        "protection": "protected",
        "personal": false
      }
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `format`, `formatVersion` | Always `teambeats.template` and `1`. Any other value is refused (`validation.template.document_format`). |
| `id` | The template id in the exporting tenant. On import the request's `id` wins when given. 3 to 40 lowercase letters, digits or hyphens, starting with a letter. |
| `basedOn`, `basedOnVersion` | The platform template the copy starts from. The import uses the version this platform has; the layout comes from it. |
| `names`, `descriptions` | Per locale, at most 80 and 300 characters. |
| `variants` | Variants the platform template offers. |
| `regions` | One entry per region of the layout, each with its policy and lock (`optional`, `mandatory` or `protected`). |

## What a document never holds

Tenant ids, org unit ids (the template's distribution is not exported), people (who made or changed it), version history, pages, fragments, themes, secrets or personal data. An exported document can be shared between tenants and environments.

## Import

The request is `{ "document": { ... }, "id": "optional-new-id" }`. The import creates a new tenant template whose only version is a draft, checked as any template draft is: the platform template must exist and be released, the variants must be ones it offers, the regions must match its layout and each policy must be valid. Nothing is released or distributed by an import, and the tenant's custom template limit applies. A refused import keeps nothing. Exports and imports are audited (`template.exported`, `template.imported`).
