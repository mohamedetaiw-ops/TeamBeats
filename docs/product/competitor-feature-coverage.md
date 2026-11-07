# Competitor feature coverage

Compared on 2026-11-05 against two competing products. Product A is a modular intranet sold mainly to regulated industries, as SaaS or self-hosted. Product B is an enterprise employee experience platform, SaaS only, aimed at large global companies. Product B's public material is short, so its rows describe the claim, not the detail.

"Spec" is where the capability lives in TeamBeats. All referenced specs are written; see [specs/README.md](../../specs/README.md).

## Communications

| Capability | Seen in | Spec |
|---|---|---|
| News and blogs, written or drafted with AI (text and images) | A, B | 009 (US1, FR-011) |
| Urgent announcements, banners and pop-ups with required "I have read this" | A | 009 (US2, FR-006, FR-007) |
| Targeting by role, location, group and device | A, B | 009 (FR-005) |
| Sites for each location, brand or franchise | A | 009 (US6) |
| Campaigns across several channels, with measurement | B | 009 (US8), 015, 021 (US2) |
| Personal feed and recommendations from user preferences | B | 009 (FR-017), 008 |
| @mentions, #hashtags, reactions, comments | A, B | 009 (US9), 023 |
| Multilingual content and AI translation into many languages | A, B | 009 (US5), 007 |
| Events calendar | A | 009 (FR-018) |
| Share to external social networks | A | 009 (section 12) |

## Site building and branding

| Capability | Seen in | Spec |
|---|---|---|
| Themes for colors, logos, fonts and images | A | 009 (FR-014), 001 |
| Drag-and-drop page builder with widgets and layouts | A | 009 (US7) |
| Store of ready-made widgets added without development | B | 032 (component registry and builder), 008 (personal regions), 009 (content components) |
| One branded app for desktop and mobile, office and frontline | A, B | 030 |

## Knowledge and documents

| Capability | Seen in | Spec |
|---|---|---|
| Version-controlled knowledge base for SOPs, walkthroughs and FAQs | A | 011 (US4) |
| Document management with check-in, check-out and version control | A | 011 (US1, US2) |
| Controlled documents with approval, effective dates and read confirmation | A (implied by regulated focus) | 011 (US3) |
| Retention, disposition and legal hold | A (implied) | 011 (US6, US7) |
| Digital asset portal for brand materials | A | 011 (US5) |
| Policy lifecycle: review workflows, distribution, acceptance tracking | A | 025 (US1 to US5) |
| Chat assistant that answers questions about each policy | A | 025 (US4, FR-010, FR-011) |

## AI and search

| Capability | Seen in | Spec |
|---|---|---|
| AI search across pages, documents, policies and processes, with summaries | A, B | 012, 014 |
| Search that respects permissions | A | constitution VII, 012 |
| Unified search across other platforms | B | 012 (federated results) |
| AI assistant for questions and tech support | A, B | 014; agents in 020 and agent governance in 027 |
| Q&A assistants for documents and policies | A | 011 (section 11), 025 (US4) |
| AI sentiment on audience engagement | B | 021 (US8) |
| A layer that turns information into action (no detail published) | B | Task inbox (016), process engine (024), agents (020) |

## Collaboration and social

| Capability | Seen in | Spec |
|---|---|---|
| Messaging, comment threads, discussion rooms | A | 023, 009 (comments) |
| Profiles with skills and badges, people directory, org charts | A | 010 |
| Project spaces with tasks, deadlines, milestones and progress | A | 029 |
| Calendars for events, holidays and team availability | A | 009 (events), 022 (US2, team availability) |

## Engagement

| Capability | Seen in | Spec |
|---|---|---|
| Anonymous polls and surveys | A | 023 |
| Thank-you shout-outs and custom badges | A | 023 |

## HR and operations

| Capability | Seen in | Spec |
|---|---|---|
| Leave requests | A | 022 (US1) |
| Expenses | A | 022 (US8) |
| Room booking | A | 018 (Microsoft 365 rooms), 031 |
| Onboarding and offboarding journeys | A (LMS for onboarding) | 022 (US4, US5) |
| No-code workflows and e-forms | A | 016, 024 |
| LMS for onboarding and mandatory training | A | 028 |
| Dashboard per location or department, including business performance | A | 021 (US6) |

## Governance, security and compliance

| Capability | Seen in | Spec |
|---|---|---|
| Content approval workflows, content owners, review dates, version control | A | 009 (US1, US3, US4), 011 (US3), 025 |
| Granular roles and permissions on content, pages, apps and search | A | 004, 009, 011 |
| 2FA, SSO, IP-based access controls, encryption | A | 003 (SSO, MFA at the identity provider), 026 (MFA check, step-up, IP rules, encryption, customer keys) |
| Audit logs across every application | A | 006, every spec's section 13 |
| Secure extranet for auditors, contractors and customers | A | 026 (US3 guest spaces) |
| Compliance projects with assigned tasks for audit readiness | A | 026 (US7) |
| ISO 27001:2022 certification | A | 026 (section 6 roadmap) |
| HIPAA compliance | A | Not planned unless a US healthcare customer needs it (026) |
| Analyst recognition | B | Not a product feature |

## Analytics

| Capability | Seen in | Spec |
|---|---|---|
| Engagement and activity tracking across all apps | A | 021 (adoption, content) |
| Internal comms return on investment | A | 021 (US2) |
| Performance by department or location | A | 021 (US6) |
| AI reporting on engagement | B | 021 (US8) |

## Integrations and deployment

| Capability | Seen in | Spec |
|---|---|---|
| Microsoft Active Directory | A | 002, 003 |
| SharePoint | B | 011 (US9 connected libraries, US10 import), 006 (Connections area) |
| Google Docs | A | INT-004, 011 (section 12) |
| Slack | A | 017 |
| Custom APIs | A | 017 |
| SaaS or self-hosted on premises | A | Foundation deployment profiles, R3 |

## Where TeamBeats goes further

- Arabic and right-to-left are first class from R0 (007), with Gulf working weeks, holidays and Hijri display (022).
- Tenant and subsidiary isolation inside one customer (002, 004), not only roles.
- Approvals for every module in one task inbox (016) on one BPMN and DMN engine (024).
- Regional compliance: NCA ECC and CCC, SAMA, Dubai ISR, ADHICS, Qatar NIA and the GCC data protection laws are on the certification roadmap (026).
- Privacy by default in analytics: minimum group size and no individual tracking (021).
