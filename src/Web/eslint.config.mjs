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
];

export default config;
