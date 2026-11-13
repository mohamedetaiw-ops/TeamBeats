# Content and UX writing

Words are the interface. TeamBeats content is conversational, professional and clear, in Arabic and English. UX writing runs in parallel with design from the first sketch, never as filler added at the end.

## Voice

| We are | So we write | Not |
|---|---|---|
| Helpful | "Submit your leave request. Your manager gets it straight away." | "Leave request submission functionality" |
| Respectful | "We could not save your changes. Your text is kept on this page." | "Error 500. Operation failed." |
| Clear | "Your password expires in 3 days." | "Credential validity period nearing termination." |
| Warm, not casual | "Good morning, Layla" | "Hey there!!" |

## Writing rules

- **Lead with the task.** Headings and buttons say what the person gets or does: "Request a laptop", not "IT Hardware Procurement Portal".
- **Plain words, short sentences.** Aim for 15 to 20 words per sentence and a reading age of about 12 in both languages. Explain terms the first time.
- **Address the reader.** "You" and "your". Use "we" for the organization or TeamBeats when it helps.
- **Sentence case** for every heading, button and label. No title case, no all caps.
- **Buttons are verbs**: "Save draft", "Submit request", "Delete file". Not "OK", "Yes" or "Click here". A confirmation repeats the action: "Delete the draft Spring campaign?" with "Delete" and "Keep draft".
- **Links say where they go**: "Read the hybrid work policy", never "here" or "more".
- **Errors** say what happened and how to fix it, next to the field: "Enter a complete email address, for example layla@company.com." Never blame the person.
- **Empty states** explain why it is empty and the next step: "No services are open to you yet. Ask your HR team which services your role can use."
- **Numbers and dates** follow the page language and the tenant's calendar settings (spec 007). Write "3 days", not "three days", in interface text.
- **Inclusive language.** Use "people of determination" in line with UAE practice, avoid gendered defaults in English, and use the correct gendered and plural forms in Arabic through the message format, not string concatenation.
- **No em dashes in interface text.** Use a full stop, a comma or "and".

## Arabic

- Write Arabic first or alongside English, never as a literal translation. Modern Standard Arabic, natural and professional.
- Plurals use the full ICU forms (zero, one, two, few, many, other).
- Keep brand and product names in their own script inside `<bdi>`.
- Punctuation: Arabic comma "،" and question mark "؟" in Arabic text.

## Content in the process

1. Research sets a content strategy for each journey: who reads it, what they need, which words they use (from search logs and interviews).
2. UX writers draft real copy in both languages before high fidelity design. Designs never use lorem ipsum; the gallery and the staging seed use realistic text.
3. Content is reviewed with the design (see [testing](../testing.md), content tests) and with a native Arabic reviewer.
4. All interface strings live in `src/Web/messages/{en,ar}.json` and the API catalogues; the build fails on literal strings and on missing keys in either language.

## Naming glossary

One name per area (spec 008 FR-064). The navigation item, the page heading and any tab that opens the area use the same words in each language. Tenants may rename an area in the global menu, but the seeded menu and the page headings follow this list.

| Area | English | Arabic | Route | Not called |
|---|---|---|---|---|
| Home | Home | الرئيسية | `/` | Dashboard |
| Services | Services | الخدمات | `/services` | Catalogue, Requests |
| Tasks | Tasks | المهام | `/tasks` | Inbox, Approvals (as an area name) |
| People | People | الأشخاص | `/people` | Directory |
| Documents | Documents | المستندات | `/documents` | Files, Libraries (a library is one item inside Documents) |
| Knowledge | Knowledge | المعرفة | `/kb` | Knowledge base (as a tab of Documents) |
| Policies | Policies | السياسات | `/policies` | Policy register (as an area name; the register is the list on the page) |
| Assistant | Assistant | المساعد | `/assistant` | Chat, Copilot |

A destination appears once in the navigation. Tasks is not a tab of Services, and Knowledge is not a tab of Documents.
