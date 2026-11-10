"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import {
  INPUT_TYPES,
  METHODS,
  SIDE_EFFECTS,
  formatPairs,
  parsePairs,
  type RestActionDefinition,
  type RestActionTestResult,
  type RestActionView,
  type RestInput,
} from "@/lib/integrations/types";
import { ErrorText, Messages } from "./parts";
import { send } from "./request";

const EMPTY: RestActionDefinition = {
  method: "GET",
  path: "/",
  query: null,
  headers: null,
  body: null,
  inputs: [],
  output: {},
  referencePath: null,
  sideEffect: "read",
  idempotencyHeader: null,
  businessErrors: [],
};

function Select({ label, value, options, onChange, labelFor }: { label: string; value: string; options: readonly string[]; onChange: (v: string) => void; labelFor?: (v: string) => string }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
        {options.map((o) => (
          <option key={o} value={o}>
            {labelFor ? labelFor(o) : o}
          </option>
        ))}
      </select>
    </div>
  );
}

function TextArea({ label, hint, value, onChange }: { label: string; hint?: string; value: string; onChange: (v: string) => void }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {hint ? (
        <p id={`${id}-hint`} className="field-hint">
          {hint}
        </p>
      ) : null}
      <textarea id={id} dir="ltr" aria-describedby={hint ? `${id}-hint` : undefined} value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

/**
 * Custom REST actions without code (spec 017 US4, FR-011): method, path, query, headers and body with
 * <c>{{input}}</c> placeholders of declared inputs, the output fields to keep, and the 4xx answers that are business
 * errors. A definition can be tried with sample input before it is saved. The API validates everything.
 */
export function ActionBuilder({ connectionId, initial }: { connectionId: string; initial: RestActionView[] }) {
  const t = useTranslations("connections.builder");
  const router = useRouter();
  const returnTo = `/admin/connections/${connectionId}`;
  const base = `/api/v1/integrations/pack-connections/${connectionId}`;
  const [actions, setActions] = useState(initial);
  const [name, setName] = useState("");
  const [existing, setExisting] = useState(false);
  const [d, setD] = useState<RestActionDefinition>(EMPTY);
  const [inputs, setInputs] = useState<RestInput[]>([]);
  const [query, setQuery] = useState("");
  const [headers, setHeaders] = useState("");
  const [body, setBody] = useState("");
  const [output, setOutput] = useState("");
  const [errors, setErrors] = useState("");
  const [sample, setSample] = useState("{}");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const [tested, setTested] = useState<RestActionTestResult | null>(null);

  function edit(view: RestActionView | null) {
    const def = view?.definition ?? EMPTY;
    setName(view?.name ?? "");
    setExisting(view !== null);
    setD(def);
    setInputs(def.inputs ?? []);
    setQuery(formatPairs(def.query));
    setHeaders(formatPairs(def.headers));
    setBody(def.body == null ? "" : JSON.stringify(def.body, null, 2));
    setOutput(formatPairs(def.output));
    setErrors((def.businessErrors ?? []).map((b) => `${b.status}: ${b.code}`).join("\n"));
    setTested(null);
    setMessages({ ok: true, list: [] });
  }

  /** The definition as typed; text that is not JSON is sent as it is, so the API's validation names the field. */
  function definition(): RestActionDefinition {
    let parsedBody: unknown = null;
    if (body.trim()) {
      try {
        parsedBody = JSON.parse(body);
      } catch {
        parsedBody = body;
      }
    }
    return {
      ...d,
      inputs,
      query: parsePairs(query),
      headers: parsePairs(headers),
      body: parsedBody,
      output: parsePairs(output),
      referencePath: d.referencePath?.trim() || null,
      idempotencyHeader: d.idempotencyHeader?.trim() || null,
      businessErrors: Object.entries(parsePairs(errors)).map(([status, code]) => ({ status: Number(status), code })),
    };
  }

  async function save() {
    setBusy("save");
    setMessages({ ok: true, list: [] });
    const result = await send<RestActionView>("PUT", `${base}/actions/${encodeURIComponent(name.trim())}`, { definition: definition() }, returnTo, t("failed"));
    setBusy(null);
    if (result.ok) {
      setActions((list) => [...list.filter((a) => a.name !== result.data.name), result.data].sort((x, y) => (x.name < y.name ? -1 : 1)));
      setExisting(true);
      setMessages({ ok: true, list: [t("saved")] });
      // The agent tools section lists the actions with inputs from the page's data.
      router.refresh();
    } else {
      setMessages({ ok: false, list: result.messages });
    }
  }

  async function test() {
    let input: unknown;
    try {
      input = JSON.parse(sample || "{}");
    } catch {
      setMessages({ ok: false, list: [t("sampleNotJson")] });
      return;
    }
    setBusy("test");
    setTested(null);
    setMessages({ ok: true, list: [] });
    const result = await send<RestActionTestResult>("POST", `${base}/actions:test`, { definition: definition(), input }, returnTo, t("failed"));
    setBusy(null);
    if (result.ok) setTested(result.data);
    else setMessages({ ok: false, list: result.messages });
  }

  async function remove(target: string) {
    const result = await send<null>("DELETE", `${base}/actions/${encodeURIComponent(target)}`, undefined, returnTo, t("failed"));
    if (result.ok) {
      setActions((list) => list.filter((a) => a.name !== target));
      edit(null);
      setMessages({ ok: true, list: [t("removed")] });
      router.refresh();
    } else {
      setMessages({ ok: false, list: result.messages });
    }
  }

  const setInput = (i: number, patch: Partial<RestInput>) => setInputs((list) => list.map((x, j) => (j === i ? { ...x, ...patch } : x)));

  return (
    <section className="connections-section" aria-labelledby="builder-heading">
      <h2 id="builder-heading">{t("title")}</h2>
      <p>{t("intro")}</p>
      {actions.length === 0 ? (
        <p className="notice">{t("empty")}</p>
      ) : (
        <ul className="connections-list">
          {actions.map((a) => (
            <li key={a.name} className="connections-row">
              <bdi dir="ltr">{a.name}</bdi>
              <span>
                {a.definition.method} <bdi dir="ltr">{a.definition.path}</bdi>
              </span>
              <Button variant="quiet" onClick={() => edit(a)} aria-label={t("editAction", { name: a.name })}>
                {t("edit")}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <div className="connections-row">
        <Button onClick={() => edit(null)}>{t("new")}</Button>
      </div>

      <form
        className="connections-form"
        aria-label={t("formLabel")}
        onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}
      >
        <FormField label={t("name")} hint={t("nameHint")} name="actionName" dir="ltr" required readOnly={existing} value={name} onChange={(e) => setName(e.target.value)} />
        <div className="connections-grid">
          <Select label={t("method")} value={d.method} options={METHODS} onChange={(v) => setD({ ...d, method: v })} />
          <Select label={t("sideEffect")} value={d.sideEffect} options={SIDE_EFFECTS} onChange={(v) => setD({ ...d, sideEffect: v })} labelFor={(v) => t(`sideEffects.${v}`)} />
        </div>
        <FormField label={t("path")} hint={t("pathHint")} name="path" dir="ltr" required value={d.path} onChange={(e) => setD({ ...d, path: e.target.value })} />

        <fieldset className="connections-fieldset">
          <legend>{t("inputs")}</legend>
          {inputs.map((input, i) => (
            <div key={i} className="connections-grid">
              <FormField label={t("inputName")} name={`input-${i}`} dir="ltr" value={input.name} onChange={(e) => setInput(i, { name: e.target.value })} />
              <Select label={t("inputType")} value={input.type} options={INPUT_TYPES} onChange={(v) => setInput(i, { type: v })} labelFor={(v) => t(`types.${v}`)} />
              <label className="connections-check">
                <input type="checkbox" checked={input.required} onChange={(e) => setInput(i, { required: e.target.checked })} /> {t("required")}
              </label>
              <Button variant="quiet" onClick={() => setInputs((list) => list.filter((_, j) => j !== i))} aria-label={t("removeInput", { name: input.name || String(i + 1) })}>
                {t("remove")}
              </Button>
            </div>
          ))}
          <Button onClick={() => setInputs((list) => [...list, { name: "", type: "string", required: true }])}>{t("addInput")}</Button>
        </fieldset>

        <TextArea label={t("query")} hint={t("pairsHint")} value={query} onChange={setQuery} />
        <TextArea label={t("headers")} hint={t("pairsHint")} value={headers} onChange={setHeaders} />
        {d.method === "GET" || d.method === "DELETE" ? null : <TextArea label={t("body")} hint={t("bodyHint")} value={body} onChange={setBody} />}
        <TextArea label={t("output")} hint={t("outputHint")} value={output} onChange={setOutput} />
        <FormField label={t("referencePath")} hint={t("referencePathHint")} name="referencePath" dir="ltr" value={d.referencePath ?? ""} onChange={(e) => setD({ ...d, referencePath: e.target.value })} />
        <FormField label={t("idempotencyHeader")} hint={t("idempotencyHeaderHint")} name="idempotencyHeader" dir="ltr" value={d.idempotencyHeader ?? ""} onChange={(e) => setD({ ...d, idempotencyHeader: e.target.value })} />
        <TextArea label={t("businessErrors")} hint={t("businessErrorsHint")} value={errors} onChange={setErrors} />

        <TextArea label={t("sample")} hint={t("sampleHint")} value={sample} onChange={setSample} />
        <div className="connections-row">
          <Button busy={busy === "test"} busyLabel={t("working")} onClick={() => void test()}>
            {t("test")}
          </Button>
          <Button type="submit" variant="primary" busy={busy === "save"} busyLabel={t("working")}>
            {t("save")}
          </Button>
          {existing ? (
            <ConfirmAction label={t("delete")} question={t("deleteQuestion", { name })} confirmLabel={t("deleteConfirm")} cancelLabel={t("cancel")} onConfirm={() => remove(name)} />
          ) : null}
        </div>
      </form>

      {tested ? (
        <div role="status" className={tested.outcome === "succeeded" ? "notice" : "notice notice-error"}>
          <p>
            {t(`outcomes.${tested.outcome === "succeeded" || tested.outcome === "business_error" ? tested.outcome : "failed"}`)}
            {tested.httpStatus ? ` ${t("httpStatus", { status: tested.httpStatus })}` : null}
          </p>
          {tested.errorCode ? (
            <p>
              {t("errorCode")} <bdi dir="ltr">{tested.errorCode}</bdi>
            </p>
          ) : null}
          {tested.errorClass ? (
            <p>
              <ErrorText errorClass={tested.errorClass} />
            </p>
          ) : null}
          {tested.externalReference ? (
            <p>
              {t("reference")} <bdi dir="ltr">{tested.externalReference}</bdi>
            </p>
          ) : null}
          {tested.output ? <pre dir="ltr">{JSON.stringify(tested.output, null, 2)}</pre> : null}
        </div>
      ) : null}
      <Messages messages={messages.list} ok={messages.ok} />
    </section>
  );
}
