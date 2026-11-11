# UI detail review of the staging screens

Review of ten staging screenshots taken on 2026-11-10 (account menu, notifications panel, settings panel, home, services, service form, my requests, people, my profile, knowledge article, documents library). The rules that come out of it are DLS-042 to DLS-055 in `specs/036-design-language-system/spec.md` and the checklist in `docs/design-system/foundations/details.md`.

## Why the screens look generated

The layouts are right, but the details were left at browser defaults. Generated interfaces give themselves away in the same few places:

1. **Raw values on screen.** `en-US`, `2015-11-01`, `Africa/Cairo` and `REQ-2026-000020` are machine values. People read "English (United States)", "1 November 2015" and "Cairo time (GMT+2)".
2. **Browser default controls.** Plain radio buttons, a checkbox used as a sort direction, a select for every setting, links underlined like a 1998 web page.
3. **Run-on text.** "Source: Directory Who sees this: Everyone" and "Operations Dubai HQ, Floor 7" join separate facts with no separator, so the eye cannot split them.
4. **Everything has the same weight.** Bold titles on every row, the same tinted box on every notification, a full-width Send button with the same weight as the page title.
5. **Empty space nobody designed.** Your day stops halfway down its card, the service form is a single field in a page-wide box, the empty documents state is a dashed rectangle with bold text.
6. **Things in the way.** The Ask TeamBeats button sits on top of cards, the third people card and the Send button.

## Panels (account, notifications, settings)

| Issue | Fix |
| --- | --- |
| Account menu is a name and two centred buttons; "Sign out" is a large outlined button with the same weight as the name. | A menu card: avatar, name and email in a header, then a list of menu items with icons (My profile, Notification preferences, Sign out). Items are full-width rows, start aligned, with hover and focus fills. Sign out sits last, after a divider. |
| Notification items are boxed tinted cards with a thick start border, all bold, two lines of 18 px text. | Plain rows separated by hairlines. Unread is a small accent dot plus a slightly stronger title, never a whole tinted box. Title is regular weight and clamps to two lines. |
| Time shows as "Nov 9, 2026, 5:03 PM" on every row. | Relative time for the last week ("12 min ago", "Yesterday"), the full date in the tooltip and `datetime`. |
| Category and time run together ("Attestations due Nov 9, 2026"). | Category as a quiet label, a middle dot, then the time, in one muted line. "Required" is a warning badge, not an outlined pill. |
| No icon per kind. | A small icon tile per category (approval, policy, request, mention) so the list can be scanned. |
| Browser scrollbar with arrows inside the card. | Thin scrollbar in the border colour, list scrolls inside a max height, header and footer stay put. |
| Footer is two loose text links. | A footer bar with a divider: "See all notifications" as a quiet button and the preferences link as an icon button with a label. |
| Settings panel is a column of selects with no grouping. | Grouped sections (Language, Appearance, Accessibility, Connections) with small headings. Mode, density, motion and text size become segmented controls, so every choice is visible and one click. |
| Panels have different widths, paddings and radii. | One popover surface for all three: same radius, shadow, 1 px border, 22 rem width (account 18 rem), a header row with title and action, and the same inner padding. |

## Home

| Issue | Fix |
| --- | --- |
| Ask TeamBeats covers the end of the Your day card and later cards. | The page frame keeps space at its end for the button, and the button shrinks to an icon with a tooltip on phones. Cards are never under it when scrolled to the end. |
| Your day ends halfway; the rest of the card is empty. | The calendar prompt becomes a small empty state with an icon, one line and a secondary "Connect Outlook" button, and it fills the space. |
| "Connect Outlook" is an underlined link in a sentence. | A secondary button. A connect action is an action, not a link in text. |
| "All tasks" is repeated on every briefing row. | One "All tasks" link in the hero footer; each row keeps only "Review". |
| Overdue and Task labels are coloured text. | Status badges with the warning tone, the same badge used everywhere. |
| My calendar and My mail draw a dashed box inside a card. | Empty states have no border of their own inside a card. |

## Services and the service form

| Issue | Fix |
| --- | --- |
| The search hint sits between the label and the box, so the search box is lower than the Category select. | Hints go under the control (or become the placeholder in a filter bar), so every control in a row lines up. |
| The service form page has no icon tile and puts the section tabs between the description and the form. | Detail pages use the page header with the service's own icon; section tabs are for top level pages, a detail page has breadcrumbs only. |
| "What is not working? (required)" in the label. | Required fields carry a small "Required" tag after the label in muted text. |
| One input stretches across 1,700 px. | Forms sit in a readable column (max 44 rem); the submit row aligns to the start. |

## My requests

| Issue | Fix |
| --- | --- |
| The service name is an underlined blue link, the number, status and step run together. | Each request is a row in a raised list: service icon, service name (no underline), a status badge whose tone follows the state (in approval = info, returned = warning, approved and completed = success, rejected = danger, cancelled = neutral), then a muted meta line "REQ-2026-000025 · Manager approval · Submitted 8 Nov 2026" and a chevron. |
| The selected "All" chip is a faint outline. | The selected chip is filled. |

## People and my profile

| Issue | Fix |
| --- | --- |
| Person cards centre the avatar against one line or three lines, so names do not line up. | Avatar and text align to the top; unit and location are separated by a middle dot. |
| "Source: Directory Who sees this: Everyone" under every value. | Two small tags under the value: a source tag and a visibility tag with an eye icon. |
| One long label and value list. | Profile fields in a two column description grid with row separators. |
| `en-US`, `2015-11-01`, `Africa/Cairo`. | Language through `Intl.DisplayNames`, dates through `Intl.DateTimeFormat` with the long style, time zone as a select of named zones with their offset. |
| Save button and "View my public profile" link do not line up. | An action row: primary Save and a secondary "View my public profile" button, same height, start aligned. |

## Knowledge article

| Issue | Fix |
| --- | --- |
| The section tabs sit on top of the title with no gap; the article has no header. | Detail header: breadcrumbs above, a template badge and the meta line under the title, "Edit this article" as a secondary button with an edit icon at the end of the header. |
| An article with no sections shows nothing. | An empty state: "This article has no text yet." |
| Plain radio buttons for Yes / No and a full-width Send button. | Two toggle buttons with thumb icons ("Yes" and "No", `aria-pressed`), the comment field opens after a choice, and a normal size Send button at the start. |
| Helpfulness is a large heading over one sentence. | A small side card with a meter, or the "No ratings yet" empty line, beside the article on wide screens. |

## Documents library

| Issue | Fix |
| --- | --- |
| The Search button is outlined, taller and lower than the inputs. | Fields in a filter bar lose their bottom margin, so buttons line up with the controls; search is an input with a leading search icon. |
| A checkbox "Newest or last first" for the sort direction. | A sort direction icon button next to the Sort select (`aria-pressed`, label says the current direction). |
| "Folder: Top of the library" and folders as floating blue words. | A folder strip: a breadcrumb of the current folder and folder chips with a folder icon. |
| "No documents match." in bold inside a dashed box. | The shared empty state: icon, title, one line of help, no dashed border. |

## Global rules

- No underline on links whose position says they are links (titles, rows, breadcrumbs, menus, chips). Underline only inside running text (DLS-027, now applied to list items too).
- Breadcrumbs: muted text, chevron separators, current page in primary text.
- Every value from a machine goes through a formatter (DLS-043).
- Native radios and checkboxes get the system look (size, border, accent fill, focus ring).
- One primary action per area, at its natural width; full-width buttons only on phones.
- Status always as a badge with a tone; never coloured text.
- Floating actions never cover content (DLS-050).
