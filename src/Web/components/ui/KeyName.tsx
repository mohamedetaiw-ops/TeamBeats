/**
 * A stored key shown by its name (spec 036 DLS-093): the name is the text people read; the key follows as quiet
 * secondary text for administrators who match it against a log or an API call. Pass `showKey={false}` where the key
 * adds nothing.
 */
export function KeyName({ name, code, showKey = true }: { name: string; code: string; showKey?: boolean }) {
  return (
    <span className="ui-key-name">
      <span>{name}</span>
      {showKey ? (
        <code className="ui-key" dir="ltr">
          {code}
        </code>
      ) : null}
    </span>
  );
}
