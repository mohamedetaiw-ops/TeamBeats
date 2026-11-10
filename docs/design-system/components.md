# Components

Every TeamBeats screen is built from these components. They live in `src/Web/components/ui/`, read only `--tb-*` tokens, work in both directions, and appear in the living gallery (`/ui-gallery`, and `/ui-gallery/components` for the extended catalogue) in the tenant's active design. Feature code composes them; it does not restyle them.

| Component | File | Use | Accessibility built in |
|---|---|---|---|
| Button | `Button.tsx` | Actions in four variants: primary, secondary, quiet, destructive; busy state | Native button, 44 px target, `aria-busy`, no double submit |
| Text box | `FormField.tsx` (`FormField`) | Single-line input with label, hint and error | Label bound to input, hint and error in `aria-describedby`, error announced |
| Text area | `FormField.tsx` (`TextAreaField`) | Multi-line input with optional counter | Same as text box; counter is a polite live region |
| Select | `FormField.tsx` (`SelectField`) | Five to fifteen known options | Native select, labelled |
| Card | `Card.tsx` | One topic: news, event, service, person, file. Optional media, eyebrow, footer actions | Heading names the card; when linked, the heading link covers the card so it is one target with one name |
| Badge | `Badge.tsx` | Short status or category: Draft, Published, Overdue | Text carries the meaning; optional icon helps |
| Avatar | `Avatar.tsx` | A person, as photo or initials | `role="img"` with the person's name |
| Alert | `Alert.tsx` | Inline information, success, warning or error in a page or form | Icon plus title text; announced when it appears after an action |
| Media | `Media.tsx` | Image with a fixed aspect ratio and optional caption | Required `alt` or explicit `decorative`; lazy loading; no layout shift |
| Tabs | `Tabs.tsx` | Switch between sibling views of one object | WAI-ARIA tabs; arrow keys follow the reading direction; Home and End |
| Icon | `Icon.tsx` | The icon set | Decorative by default; `label` for meaningful icons; mirrors directional icons |
| State view | `StateView.tsx` | Loading, success, empty, error, denied, offline, unsupported | One pattern for every state |
| Skeleton | `Skeleton.tsx` | Loading placeholder that matches the final layout | Hidden from screen readers; busy state on the region |
| Confirm | `ConfirmAction.tsx` | Confirm a destructive action in place | Focus moves to the question and back |
| Selection bar | `SelectionBar.tsx` | Bulk actions on selected items | Count and consequence stated in text |
| Messages | `Messages.tsx` | Toast-style messages with priority and Undo | Polite or assertive live region by priority |
| Page header | `PageHeader.tsx` | Top of every module page: icon tile, h1, one line description, page actions, section tabs | One h1; the icon is decorative |
| Section tabs | `SectionNav.tsx` | Pill tabs between the pages of one area (My policies, Register, Campaigns) | A labelled `nav`; the current page has `aria-current="page"` |
| Tile card | `TileCard.tsx` | A service, knowledge space or library: icon tile, title, meta, footer | The title link covers the card; one target, one name |
| Button sizes and icons | `Button.tsx` (`Button`, `ButtonLink`) | Sizes medium (44 px, page actions), small (36 px) and compact (28 px, only in toolbars, tables and dense panels); a leading or trailing icon; a link that looks like a button | The icon is decorative; a link stays a link |
| Icon button | `IconButton.tsx` (`IconButton`, `CloseButton`) | An action shown as an icon only: edit, more, close | The label is required and becomes the accessible name |
| Button group | `ButtonGroup.tsx` | Two to five related actions joined in one row | A named group; toggles use `aria-pressed` |
| Checkbox | `Checkbox.tsx` | One yes or no choice, or several from a list; mixed state for "select all"; card form with a description | Native checkbox; hint and error in `aria-describedby`; mixed state announced |
| Radio group | `RadioGroup.tsx` | One choice from two to five options; card form with descriptions | Native radios in a `fieldset` with a legend; arrow keys move the choice |
| Switch | `Switch.tsx` | A setting that applies at once, on or off; card form | `role="switch"` on a native checkbox; Space flips it |
| Segmented control | `SegmentedControl.tsx` | One view setting from two to five short options (Day, Week, Month) | A radio group; arrow keys follow the reading direction |
| Slider | `Slider.tsx` | An approximate number in a range | Native range input; the value is shown as text and in `aria-valuetext` |
| Chip and tag group | `Chip.tsx` (`Chip`, `TagGroup`) | A short label for a filter, topic or status; removable or selectable | Remove buttons name what they remove; selectable chips are toggle buttons; a tag group is a named list |
| Status dot | `Status.tsx` (`StatusDot`) | Presence or sync state beside its words | Never colour alone: the text is required |
| Keyboard key | `Status.tsx` (`Kbd`) | A key or shortcut in help text | A `kbd` element |
| Divider | `Status.tsx` (`Divider`) | A line between groups, with an optional word such as "or" | A separator; decorative when it has no label |
| Spinner | `Status.tsx` (`Spinner`) | A short wait inside a control or a small region; pages use Skeleton | A status with its label as text; still under reduced motion |
| Progress | `Status.tsx` (`Progress`) | How far a task is, or that it is running when the length is unknown | Native `progress` with a name and value text |
| Meter | `Status.tsx` (`Meter`) | A measured amount against a limit, such as storage or a quota | `role="meter"` with a name, range and value text; the tone repeats what the text says |
| Avatar group | `Avatar.tsx` (`AvatarGroup`) | The people on a card or a thread, at most four and then a count | A named list; the count is text |
| Accordion | `Accordion.tsx` | Questions and settings that open in place | Native `details`; works without script; found by find in page |
| Pagination | `Pagination.tsx` | Pages of a list that the API pages | A named `nav` of links; the current page has `aria-current="page"` |
| Announcement | `Announcement.tsx` | News about the product or workplace, with at most one action; not for errors | A named region; the close button is named |
| Filter bar | `.ui-filter-bar` | Search box, selects and Apply above a list | Real labels; Apply is a button |
| Filter chips | `.ui-chips` | A row of choices that narrow a list, such as request status | Links with `aria-current` on the active choice |
| List rows | `.ui-rows`, `.ui-row` | Tasks and other actionable items: icon tile, title, meta, one action | Each row is a list item; the action names its item |
| Data table | `main table` | Registers and reports | Native table with header cells; rows highlight on hover |
| Split view | `.ui-split` | A main column with a side column (Tasks and delegation) | The side column is an `aside` with a label; stacks below 64rem |
| Person card | `people-cards` | People directory and team | Avatar with a steady tone, name link covers the card |
| Briefing hero | `home.briefing` | Top of the home: greeting, date, weather, next three things with actions | A section named by its heading; rows are an ordered list; where items came from is stated |
| Your day | `home.your_day` | Tinted counts and today's agenda beside the briefing | A section named by its heading; the agenda is an ordered list; the connect line is a real link |
| Arrange this page | `features/personalization/Personalization.tsx` | Edit icon in the shell bar next to settings that starts personalization; Done, Undo and Reset appear at the top of the page while arranging | A button named "Arrange this page" with `aria-pressed`; focus returns to it when arranging ends |
| Drag handle | `lib/experience/drag.ts` | Moves an item in personalization mode and in the page builder; the item fades and an accent bar marks where it lands | Pointer capture so touch works; the same moves stay as buttons and shortcuts |
| Shell panels | `lib/shell/panel.ts` | Settings, notifications and account panels in the bar | One open at a time; outside press and Escape close; focus returns to the button |
| Ask TeamBeats | `features/shell/Shell.tsx` | One floating button that opens the assistant, when the tenant has it | A link with a visible name; it never covers the focused element's own actions |
| Featured news | `content.news_feed` with layout `featured` | Lead story large, the rest as rows, with reading time and reactions | One link per story |
| Campaign band | `content.call_to_action` with emphasis `campaign` | One campaign at the bottom of the home | Heading and one action; decoration is hidden |

Shell parts (header, navigation, search, settings, account menu, notifications, task badge) live in `src/Web/features/shell` and follow the same rules.

## Rules for components

- **One component per pattern.** Before building something new, check this list and the gallery. A second way to show the same thing is a defect.
- **Tokens only.** No raw colours, sizes or fonts (stylelint, token gate, design gate).
- **States.** Every component designs and builds default, hover, focus, active, disabled or busy, error, loading and empty states, in light and dark, LTR and RTL.
- **No business rules.** Components render and collect input. Authorization, entitlements and validation rules come from the API (constitution XXX).
- **No imports between feature components.** Placements talk through declared events (ADR-0014).
- **Every new component** gets a gallery entry with realistic Arabic and English text, unit or e2e coverage of its keyboard behaviour, and a visual baseline in the design suite.

## Do and do not

| Do | Do not |
|---|---|
| Use `Card` with `href` for a clickable card | Wrap a whole card in `<a>` containing other links and buttons |
| Use `Badge` text "Overdue" with the danger tone | Show a red dot with no text |
| Use `Alert` next to the form that failed | Show a toast for a validation error |
| Use `Media` with a ratio for every image | Use a bare `<img>` with no size |
| Use `Tabs` for sibling views | Use tabs as main navigation between pages |
| Use `Switch` for a setting that applies at once | Use a switch inside a form that needs Save (use `Checkbox`) |
| Use `SegmentedControl` to change how one view is shown | Use it to move between pages (use section tabs) |
| Use the compact button size inside a toolbar or a table | Use compact buttons for the main action of a page |
