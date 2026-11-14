# Components

Every TeamBeats screen is built from these components. They live in `src/Web/components/ui/`, read only `--tb-*` tokens, work in both directions, and appear in the living gallery (`/ui-gallery`, `/ui-gallery/components` for the extended catalogue and `/ui-gallery/patterns` for the page patterns) in the tenant's active design. Feature code composes them; it does not restyle them.

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
| Dialog | `Dialog.tsx` (`Dialog`) | A task or question that must be finished or dismissed before going on, and needs room | Native modal `dialog`: focus stays inside, Escape and a click outside close it, focus returns to the opener; named by its title |
| Drawer | `Dialog.tsx` (`Drawer`) | Details or filters beside a list, attached to an edge (end by default) | Same as Dialog; the edge follows the reading direction |
| Popover | `Popover.tsx` | A few settings or details beside the button that opens them | Popover API; the button has `aria-expanded`; Escape closes and returns focus; the page stays usable |
| Tooltip | `Tooltip.tsx` | One short line that adds to a control's name | Shows on hover and focus, can be hovered, Escape hides it; tied with `aria-describedby` |
| Menu | `Menu.tsx` (`Menu`) | Actions on the thing beside the button; groups, checked items, shortcuts, one level of submenu | WAI-ARIA menu: arrows, Home, End, type ahead; disabled items are focusable; focus returns to the button |
| Context menu | `Menu.tsx` (`ContextMenu`) | The same actions on the thing itself, by secondary click, long press or the menu key | A shortcut only: every action is also reachable from a visible control |
| Command palette | `CommandPalette.tsx` | Go to a page or run a command from anywhere; the shell opens it with Ctrl+K or Command+K | A modal dialog holding a combobox and a list box; focus stays in the box |
| Notification card | `Messages.tsx` (a message with a `title`) | A message that deserves a title and an icon, such as "Invitations sent" | Same live regions and timing as other messages |
| Breadcrumb | `Breadcrumb.tsx` | Where the page sits; long trails collapse the middle levels into a menu | A named `nav`; the page itself has `aria-current="page"` |
| Field parts | `FormField.tsx` (`Field`, `InputGroup`, `leading`, `trailing`) | A unit, prefix or icon inside a field | The label, help and error stay tied to the input; added parts are decoration or named buttons |
| Search field | `Inputs.tsx` (`SearchField`) | Search within a page or a list | `type="search"` with a label; Escape and the named clear button empty it; Enter searches |
| Number field | `Inputs.tsx` (`NumberField`) | A count or amount with a range and step | `role="spinbutton"`: arrows step, Page keys step by ten, Home and End go to the limits; the value is clamped on leaving |
| Code field | `Inputs.tsx` (`OtpField`) | A one time code of fixed length | One named box per digit, always left to right; pasting fills every box |
| Time field | `Inputs.tsx` (`TimeField`) | A time of day | Native time input with a label |
| Colour field | `Inputs.tsx` (`ColorField`) | A brand or label colour, with presets | Hex text with a named native picker; presets are toggle buttons with names |
| Combobox | `Combobox.tsx` | Pick one of many known options by typing | WAI-ARIA combobox with a list box; Enter picks, Escape closes and then clears |
| Calendar | `Calendar.tsx` (`Calendar`) | Pick a day or a range in Gregorian or Umm al-Qura | Grid with roving focus: arrows by direction, Home and End for the week, Page keys for months, Shift for years; unavailable days say so |
| Date field | `Calendar.tsx` (`DateField`, `DateRangeField`) | A date or a range in a form | A button that shows the chosen date as text and opens the calendar in a popover; hidden inputs carry ISO days |
| Slot picker | `Calendar.tsx` (`SlotPicker`) | Pick a free time, such as a room or a meeting | A named radio group; taken slots are disabled with a reason |
| File upload | `FileUpload.tsx` | Add files to a request or a document | A labelled file input with a drop area; each file shows size, progress and its own error, and has a named remove button |
| Carousel | `Carousel.tsx` | A row of cards that does not fit, such as featured news | A named scrolling list; previous and next buttons are disabled at the ends; nothing moves by itself |
| Tree view | `TreeView.tsx` | Folders and nested sections | WAI-ARIA tree: arrows move and open by direction, type ahead finds a node |
| Data table | `DataTable.tsx` | Registers and reports with sorting, row selection, a columns menu, a toolbar and paging | Native table with a caption; sortable headers are buttons and the header states `aria-sort`; the select all box shows the mixed state; the scroll area is a named, focusable region |
| Data grid | `DataGrid.tsx` | Editing many values in place, such as a budget | WAI-ARIA grid: one tab stop, arrows by cell in the reading direction, Home and End, Enter or F2 to edit, Escape to return to the cell |
| Stat cards | `Blocks.tsx` (`StatCards`) | Headline numbers with their change against the previous period | A named list; the change is a signed percent with an arrow and the words "against {period}"; the tone follows whether a rise is good news |
| Month view | `CalendarViews.tsx` (`MonthView`) | Events across a month in the tenant's calendar | A table named by the month, weekday headers with full names; today has `aria-current="date"`; extra events are counted in words |
| Week view | `CalendarViews.tsx` (`WeekView`) | Timed events across a few days, with overlaps side by side | Each day is a section named by its heading with an ordered list of events and their times in words |
| Task list | `Blocks.tsx` (`TaskList`) | Tasks to tick off, with due date, priority and owner | Each box is named "Mark {title} done"; due and priority are text badges |
| Notification panel | `Blocks.tsx` (`NotificationPanel`) | The latest notifications, in a popover or on a page | A named region; unread is said in words; the filter is a segmented control; mark all read is disabled when nothing is unread |
| Settings dialog | `Blocks.tsx` (`SettingsDialog`) | Personal settings in sections | A dialog with vertical tabs (`aria-orientation="vertical"`, up and down arrows); each section saves on its own |
| Sign-in card | `Blocks.tsx` (`SignInCard`) | The tenant's sign-in choices | A named region; providers are links; a form, when allowed, follows a labelled divider |
| Thinking indicator | `Assistant.tsx` (`ThinkingIndicator`) | While the assistant or an agent works, with what it is doing now | A status region in words; the dots are decoration and stop with reduced motion |
| Composer | `Assistant.tsx` (`Composer`) | The message box of the assistant and of agents, with attachments | Labelled text box that grows; Enter sends, Shift and Enter adds a line (stated in its description); named attach, remove, send and stop buttons |
| Source list and citations | `Assistant.tsx` (`SourceList`, `Cite`) | The sources an answer used, in the order it cites them | An ordered list under a heading; each citation is a link named "Source n: title" to its entry |
| Usage limit | `Assistant.tsx` (`UsageLimit`) | A quota such as questions today or agent runs this month | A meter with the amount in words and when it resets; warning from 80 percent, danger at the limit |
| Question steps | `Assistant.tsx` (`QuestionSteps`) | A few answers an agent needs before it can go on | One radio group per step, "Step n of m" takes focus on each step, Back keeps answers, a summary before sending |
| Agent form | `Assistant.tsx` (`AgentForm`) | The inputs of an agent run, from its published schema | Labelled fields with "(optional)" where it applies; the API's errors sit beside each field |
| Run progress | `Assistant.tsx` (`RunProgress`) | The steps of an agent run and where it is now | An ordered list; each step states its status in words; the running one has `aria-current="step"` and is announced |
| Chart card | `Charts.tsx` (`ChartCard`, `ChartLegend`, `seriesTable`) | Every chart: title, hint, optional headline figure, legend and a "Show as a table" toggle | A region named by its title; the toggle is a pressed button that swaps the drawing for a table with column and row headers |
| Line and area chart | `Charts.tsx` (`LineChart`) | A measure over time, stacked areas for parts of a whole, a step line for counts that change on a date | One image named by a summary in words; each series has its own colour, marker and dash; the value axis is on the start side |
| Bar chart | `Charts.tsx` (`BarChart`) | Comparing categories, grouped or stacked, with an optional target line | As the line chart; stacked series also differ by pattern |
| Bar list and funnel | `Charts.tsx` (`BarList`, `FunnelChart`) | Ranked items such as top searches; stages that people drop out of | Ordered lists with the label and value in text; the funnel says each stage's share of the first |
| Heat map and contributions | `Charts.tsx` (`HeatMap`, `Contributions`) | Activity by day and hour, or by day over weeks | One image with a summary; the value is written in each heat cell; levels use the accent at set opacities |
| Radar, radial and rings | `Charts.tsx` (`RadarChart`, `RadialChart`, `Rings`) | Skills against an average, one percentage, a few goals against their targets | One image with a summary; the value and each ring's progress are also written as text |
| Flow, scatter and stage bars | `Charts.tsx` (`FlowChart`, `ScatterChart`, `StageBars`) | Where people go from where they start, two measures per group, items by stage | One image with a summary; node labels carry their totals; stage bars list each stage with its count and share |
| Mosaic | `Patterns.tsx` (`Mosaic`) | The tiles of a dashboard (layout L08) | Tiles keep reading order; a third, half, two thirds or the full row, and every tile takes the full row when the region is narrow |
| Chat layout and transcript | `Patterns.tsx` (`ChatLayout`, `ChatTranscript`) | The assistant and agent conversations | Past conversations in a named navigation with the current one marked `aria-current`; the transcript is a log so new messages are read out |
| Profile layout | `Patterns.tsx` (`ProfileLayout`, `ProfileIcon`, `ProfileBlock`, `FactList`) | A person, an agent or a record: who or what, status, facts and actions, details beside supporting blocks | One heading at the level the page gives; the status is a badge in words; facts are a description list |
| Timeline | `Patterns.tsx` (`Timeline`) | What happened to a record, newest first | A named ordered list; each entry says who and when, with a machine readable time |
| Board | `Patterns.tsx` (`Board`) | Work in columns by stage | Each column is a region named by its heading and count; a card moves from its menu (a radio group of columns) or by dragging; the move is announced and focus stays on the card |
| Filter toolbar | `FilterToolbar.tsx` (`FilterToolbar`, `ToolbarSearch`, `ToolbarSelect`) | Search and filters above a list (DLS-106): one row that is not a card, search first, at most three compact filters, view and sort controls at the end, filters in use as removable chips with Clear all; on a phone the filters open from a Filters button | A named search form; the search label is read but not shown; each chip's remove control names what it removes; works as a plain GET form or a client form |
| Filter chips | `.ui-chips` | A row of choices that narrow a list, such as request status | Links with `aria-current` on the active choice |
| List rows | `.ui-rows`, `.ui-row` | Tasks and other actionable items: icon tile, title, meta, one action | Each row is a list item; the action names its item |
| Plain table | `main table` | A short read-only table inside page text | Native table with header cells; rows highlight on hover |
| Split view | `.ui-split` | A main column with a side column (Tasks and delegation) | The side column is an `aside` with a label; stacks below 64rem |
| Person | `PersonItem` with `form` chip, row or card | People directory, team, any place a person is named | Chip: avatar and name. Row: adds title, unit and location. Card: the row in a frame with up to two quick actions (email, call) for channels the API gave. Avatar with a steady tone; the name link covers the card. The directory offers cards or a list |
| Briefing hero | `home.briefing` | Top of the home: greeting, date, weather, next three things with actions | A section named by its heading; rows are an ordered list; where items came from is stated |
| Your day | `home.your_day` | Tinted counts and today's agenda beside the briefing | A section named by its heading; the agenda is an ordered list; the connect line is a real link |
| Arrange this page | `features/personalization/Personalization.tsx` | Edit icon in the shell bar next to settings that starts personalization; Done, Undo and Reset appear at the top of the page while arranging | A button named "Arrange this page" with `aria-pressed`; focus returns to it when arranging ends |
| Drag handle | `lib/experience/drag.ts` | Moves an item in personalization mode and in the page builder; the item fades and an accent bar marks where it lands | Pointer capture so touch works; the same moves stay as buttons and shortcuts |
| Shell panels | `lib/shell/panel.ts` | Settings, notifications and account panels in the bar | One open at a time; outside press and Escape close; focus returns to the button |
| Ask TeamBeats | `features/shell/Shell.tsx` | One floating button that opens the assistant, when the tenant has it | A link with a visible name; it never covers the focused element's own actions |
| Shell footer | `features/shell/Footer.tsx` | Organization, the tenant's policy and accessibility links and the classification under every page | A `footer` (contentinfo) with a labelled `nav`; drawn only from tenant settings, absent when there are none; secondary text size, lock icon beside the classification |
| Home view tabs | `app/page.tsx` with `SectionNav` | My day and the tenant's other home views at the top of the home | Section tabs as links (`/?view=<id>`); My day first; no tabs when the viewer has no other view |
| Featured news | `content.news_feed` with layout `featured` | Lead story large, the rest as rows, with reading time and reactions | One link per story |
| Campaign band | `content.call_to_action` with emphasis `campaign` | One campaign at the bottom of the home | Heading and one action; decoration is hidden |

Shell parts (header, navigation, search, settings, account menu, notifications, task badge) live in `src/Web/features/shell` and follow the same rules.

## Rules for components

- **One component per pattern.** Before building something new, check this list and the gallery. A second way to show the same thing is a defect.
- **Tokens only.** No raw colours, sizes or fonts (stylelint, token gate, design gate).
- **States.** Every component designs and builds default, hover, focus, active, disabled or busy, error, loading and empty states, in light and dark, LTR and RTL.
- **No business rules.** Components render and collect input. Authorization, entitlements and validation rules come from the API (constitution XXX).
- **No imports between feature components.** Placements talk through declared events (ADR-0014).
- **Overlays use the platform.** Dialogs are the native `dialog` element, popovers and menus use the Popover API, and placement and list keys come from `overlay.ts`. Do not build another focus trap, click outside handler or positioning routine.
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
| Use `ConfirmAction` to confirm a destructive action in place | Open a dialog only to ask "Are you sure?" |
| Use `Popover` for a few settings beside a button | Put a form with Save in a popover (use `Dialog` or `Drawer`) |
| Give every context menu action a visible control too | Hide the only way to an action behind a secondary click |
| Use a tooltip for an extra hint | Put the only name of a control, or a link, in a tooltip |
| Use `Combobox` for a long list of known options | Use it for free text with suggestions only (use `SearchField`) |
| Use `DateField` and keep ISO days in the form | Parse or store dates as localized text |
| Use `Carousel` buttons that stop at the ends | Rotate slides by themselves |
| Use `DataTable` for lists people read, sort and select | Use `DataGrid` for a read-only list (it adds keys people do not expect) |
| Say whether a rise is good with `goodWhen` | Colour a change red or green with no sign or words |
| Keep the notification panel and settings dialog as the shell's only versions | Build a second notification list or settings layout in a feature |
| Show the sources an answer used, numbered as it cites them | Show an answer with no way to see where it came from |
| Say what the assistant is doing while it works | Show only a spinner for a long answer |
