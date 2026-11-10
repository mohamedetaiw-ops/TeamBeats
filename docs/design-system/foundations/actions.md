# Actions

Actions are how people get things done. They must be easy to find, clear about what happens, and safe to use.

## Hierarchy

| Kind | Component | Use |
|---|---|---|
| Primary | `<Button variant="primary">` | The one main action of a context: Submit, Save and publish, Start request. At most one per form, dialog or card |
| Secondary | `<Button variant="secondary">` | Other useful actions: Save draft, Add to calendar |
| Quiet | `<Button variant="quiet">` | Low emphasis actions: Cancel, Clear filters, Show more |
| Destructive | `<Button variant="destructive">` | Delete, Remove, Revoke. Never the default, never styled as primary, always confirmed or undoable |
| Link | `<a>` | Navigation to another page. Use a link to go somewhere, a button to do something |

## Rules

- **Targets** are at least 44 by 44 px for buttons and menu items (spec 008 FR-047), and never below 24 by 24 px for any control (WCAG 2.5.8, checked by the design suite).
- **Labels are verbs** that name the result. Keep them under about 25 characters; they must not wrap at phone width in either language.
- **Placement.** The primary action comes first in the reading direction in a button row (start side), followed by secondary and quiet. On phones, form actions stack full width with the primary on top.
- **Busy state.** A button that starts work shows its busy label, keeps its size and blocks double submits (`busy` prop).
- **Disabled controls** are avoided; prefer an enabled control that explains what is missing. If disabled, say why next to it.
- **Destructive actions** use the confirm pattern (`ConfirmAction`) or offer Undo in the message area for a short time. Say exactly what will be lost.
- **Keyboard.** Every action is reachable with Tab, runs with Enter or Space, and has a visible focus ring from `color.focus.ring`.
- **Feedback.** After an action, show the result where the person is looking: a status message, an inline alert, or the updated item. Critical failures are announced to screen readers.
- **AI actions** (spec 027) never run consequential changes without a human confirmation, and carry the `spark` mark.
