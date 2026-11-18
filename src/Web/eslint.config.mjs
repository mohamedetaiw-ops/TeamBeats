import next from "eslint-config-next";

/**
 * Constitution: no user-facing literal strings. Literal JSX text is an error; every visible string comes from
 * messages/*.json through next-intl (spec 007 T007).
 */
const config = [
  ...next,
  { ignores: [".next/**", "node_modules/**", "next-env.d.ts"] },
  {
    files: ["**/*.tsx"],
    rules: {
      "react/jsx-no-literals": ["error", { noStrings: true, ignoreProps: true, allowedStrings: ["·", "/", ":", "(", ")"] }],
    },
  },
  {
    // Accessibility lint (spec 038 A11Y-017): the recommended jsx-a11y rules, from the plugin eslint-config-next already
    // registers, at error level. Options that differ from the recommended set, and why:
    //  - a keyboard handler on a container is allowed: overlays close on Escape and composite widgets (tabs, menus,
    //    listboxes, tree, grid) take arrow keys on the container with a roving tabindex (WAI-ARIA practices);
    //  - tabindex 0 is allowed on scrollable regions, code blocks, tab panels and composite containers, which must be
    //    focusable to scroll with the keyboard;
    //  - ARIA composite roles on lists and tables (tree, listbox, grid) follow the WAI-ARIA patterns;
    //  - `role` on our own components is a prop, not an ARIA role;
    //  - interactive-supports-focus is off: it flags the same roving tabindex containers, whose items take focus.
    files: ["**/*.tsx"],
    rules: {
      "jsx-a11y/alt-text": "error",
      "jsx-a11y/anchor-ambiguous-text": "off",
      "jsx-a11y/anchor-has-content": "error",
      "jsx-a11y/anchor-is-valid": "error",
      "jsx-a11y/aria-activedescendant-has-tabindex": "error",
      "jsx-a11y/aria-props": "error",
      "jsx-a11y/aria-proptypes": "error",
      "jsx-a11y/aria-role": ["error", { ignoreNonDOM: true }],
      "jsx-a11y/aria-unsupported-elements": "error",
      "jsx-a11y/autocomplete-valid": "error",
      "jsx-a11y/click-events-have-key-events": "error",
      "jsx-a11y/heading-has-content": "error",
      "jsx-a11y/html-has-lang": "error",
      "jsx-a11y/iframe-has-title": "error",
      "jsx-a11y/img-redundant-alt": "error",
      "jsx-a11y/interactive-supports-focus": "off",
      "jsx-a11y/label-has-associated-control": "error",
      "jsx-a11y/media-has-caption": "error",
      "jsx-a11y/mouse-events-have-key-events": "error",
      "jsx-a11y/no-access-key": "error",
      "jsx-a11y/no-autofocus": ["error", { ignoreNonDOM: true }],
      "jsx-a11y/no-distracting-elements": "error",
      "jsx-a11y/no-interactive-element-to-noninteractive-role": "error",
      "jsx-a11y/no-noninteractive-element-interactions": ["error", { handlers: ["onClick", "onMouseDown", "onMouseUp", "onKeyPress"] }],
      "jsx-a11y/no-noninteractive-element-to-interactive-role": [
        "error",
        {
          ul: ["listbox", "menu", "menubar", "radiogroup", "tablist", "tree", "treegrid"],
          ol: ["listbox", "menu", "menubar", "radiogroup", "tablist", "tree", "treegrid"],
          li: ["menuitem", "menuitemradio", "menuitemcheckbox", "option", "row", "tab", "treeitem"],
          table: ["grid"],
          td: ["gridcell"],
          fieldset: ["radiogroup", "presentation"],
        },
      ],
      "jsx-a11y/no-noninteractive-tabindex": ["error", { tags: ["pre"], roles: ["tabpanel", "region", "group", "grid", "listbox", "tree", "dialog"], allowExpressionValues: true }],
      "jsx-a11y/no-redundant-roles": "error",
      "jsx-a11y/no-static-element-interactions": ["error", { allowExpressionValues: true, handlers: ["onClick", "onMouseDown", "onMouseUp", "onKeyPress"] }],
      "jsx-a11y/role-has-required-aria-props": "error",
      "jsx-a11y/role-supports-aria-props": "error",
      "jsx-a11y/scope": "error",
      "jsx-a11y/tabindex-no-positive": "error",
    },
  },
];

export default config;
