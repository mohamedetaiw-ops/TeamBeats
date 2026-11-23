"use client";

import { useRef, useState, type ChangeEvent } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import type {
  AssetView,
  BrandCheck,
  BrandDraft,
  BrandSummary,
  BrandView,
  ContrastFinding,
  ProblemErrors,
  TokenSchema,
  TokenSchemaEntry,
  TokenValue,
} from "@/lib/brand/types";
import { BrandPreview, type PreviewSample } from "./BrandPreview";
import { ContrastTable } from "./ContrastTable";
import { DesignPicker } from "./DesignPicker";
import { EmptyCell } from "@/components/ui/EmptyCell";
import { KeyName } from "@/components/ui/KeyName";
import { keyName } from "@/lib/admin/keys";
import { Checkbox } from "@/components/ui/Checkbox";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";

export type { PreviewSample };

type Status = { kind: "idle" | "saving" | "saved" | "blocked" | "failed"; message?: string };

const hex = /^#[0-9a-f]{6}$/i;
const groups = ["layout", "density", "imagery", "variant", "color", "font", "space", "radius", "border", "shadow", "motion", "state"] as const;
const presetToken = "design.preset";

/**
 * Brand editor (spec 033 T011). It holds only the values the brand sets; everything else shows the platform default
 * as a placeholder. Validation, contrast, alias resolution and the preview values all come from the API check, so the
 * page applies no theme rules of its own.
 */
export function BrandManager({
  schema,
  brands,
  brand,
  draft,
  initialPreview,
  samples,
  locales,
}: {
  schema: TokenSchema;
  brands: BrandSummary[];
  brand: BrandView;
  /** The kept draft, which the editor opens on; null when the person may not read drafts. */
  draft: BrandDraft | null;
  initialPreview: BrandCheck["preview"] | null;
  samples: { ltr: PreviewSample; rtl: PreviewSample };
  /** The tenant's languages, each of which may have its own title, logo and lockup (R2 T018). */
  locales: string[];
}) {
  const t = useTranslations("brand");
  const languageName = useTranslations("locale");
  const locale = useLocale();
  const router = useRouter();
  const start = draft?.hasDraft ? draft : null;
  const [name, setName] = useState(start?.name ?? brand.name);
  const [tokens, setTokens] = useState<Record<string, TokenValue>>(start?.tokens ?? brand.tokens);
  const [assets, setAssets] = useState<Record<string, string>>(start?.assets ?? brand.assets);
  // Draft images are shown only once uploaded again in this session; the saved ones keep their URLs.
  const [assetUrls, setAssetUrls] = useState<Record<string, string>>(brand.assetUrls);
  const [strict, setStrict] = useState(start?.strictContrast ?? brand.strictContrast);
  const [findings, setFindings] = useState<ContrastFinding[]>(start?.findings ?? brand.findings);
  // When the version in force or the draft changes on the server (publish, approval, revert, discard), the fields follow it.
  const source = `${brand.version}:${draft?.hasDraft ? (draft.updatedAt ?? "") : "none"}`;
  const [shown, setShown] = useState(source);
  if (shown !== source) {
    setShown(source);
    setName(start?.name ?? brand.name);
    setTokens(start?.tokens ?? brand.tokens);
    setAssets(start?.assets ?? brand.assets);
    setAssetUrls(brand.assetUrls);
    setStrict(start?.strictContrast ?? brand.strictContrast);
    setFindings(start?.findings ?? brand.findings);
  }
  const [preview, setPreview] = useState(initialPreview);
  const [errors, setErrors] = useState<ProblemErrors>({});
  const [status, setStatus] = useState<Status>({ kind: "idle" });
  const [uploading, setUploading] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const sequence = useRef(0);

  function scheduleCheck(next: Record<string, TokenValue>, nextStrict: boolean) {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => void runCheck(next, nextStrict), 350);
  }

  async function runCheck(next: Record<string, TokenValue>, nextStrict: boolean) {
    const id = ++sequence.current;
    const response = await fetch(`/api/v1/experience/brands/${brand.id}/check`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ name: name || brand.name, tokens: next, strictContrast: nextStrict }),
    });
    if (id !== sequence.current) return;
    if (response.ok) {
      const check = (await response.json()) as BrandCheck;
      setFindings(check.findings);
      setPreview(check.preview);
      setErrors({});
    } else if (response.status === 400) {
      const problem = (await response.json().catch(() => null)) as { errors?: ProblemErrors } | null;
      setErrors(problem?.errors ?? {});
    }
  }

  function setToken(entry: TokenSchemaEntry, mode: string | null, value: string) {
    const current = tokens[entry.name];
    const modes = { ...(current?.$modes ?? {}) };
    let base = current?.$value ?? "";
    if (mode === null) base = value.trim();
    else if (value.trim() === "") delete modes[mode];
    else modes[mode] = value.trim();
    const next = { ...tokens };
    if (base === "" && Object.keys(modes).length === 0) {
      delete next[entry.name];
    } else {
      // A mode value needs a base value; the platform default stands in until the editor sets one.
      next[entry.name] = { $value: base === "" ? entry.light : base, ...(Object.keys(modes).length > 0 ? { $modes: modes } : {}) };
    }
    setTokens(next);
    setStatus({ kind: "idle" });
    scheduleCheck(next, strict);
  }

  async function upload(slot: string, event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    setUploading(slot);
    const response = await fetch("/api/v1/experience/assets", { method: "POST", headers: { "content-type": file.type }, body: file });
    setUploading(null);
    if (response.status === 201) {
      const asset = (await response.json()) as AssetView;
      setAssets({ ...assets, [slot]: asset.reference });
      setAssetUrls({ ...assetUrls, [slot]: asset.url });
      setErrors((e) => ({ ...e, [`assets.${slot}`]: [] }));
      setStatus({ kind: "idle" });
      return;
    }
    const problem = (await response.json().catch(() => null)) as { errors?: ProblemErrors } | null;
    const message = problem?.errors?.asset?.[0]?.message ?? t("assets.failed");
    setErrors((e) => ({ ...e, [`assets.${slot}`]: [{ key: "asset", message }] }));
  }

  function removeAsset(slot: string) {
    const nextAssets = { ...assets };
    delete nextAssets[slot];
    const nextUrls = { ...assetUrls };
    delete nextUrls[slot];
    setAssets(nextAssets);
    setAssetUrls(nextUrls);
    setStatus({ kind: "idle" });
  }

  /** The title is text kept with the brand images (R2 T018); an empty field removes it, so the fallback applies. */
  function setTitle(slot: string, value: string) {
    const next = { ...assets };
    if (value.trim() === "") delete next[slot];
    else next[slot] = value;
    setAssets(next);
    setStatus({ kind: "idle" });
  }

  function assetItem(slot: string, label: string) {
    return (
      <li key={slot} className="brand-asset" data-slot={slot}>
        <span className="brand-asset-name">{label}</span>
        {assetUrls[slot] ? (
          // Brand assets are session-protected API files, so the image optimizer cannot fetch them.
          // eslint-disable-next-line @next/next/no-img-element
          <img className="brand-asset-image" src={assetUrls[slot]} alt={label} />
        ) : (
          <span className="notice">{assets[slot] ? t("assets.saved") : t("assets.none")}</span>
        )}
        <label className="button secondary">
          {uploading === slot ? t("assets.uploading") : t("assets.upload")}
          <input
            className="visually-hidden"
            type="file"
            accept="image/png,image/jpeg,image/svg+xml"
            onChange={(e) => void upload(slot, e)}
            aria-label={t("assets.uploadFor", { slot: label })}
          />
        </label>
        {assets[slot] ? (
          <button type="button" className="button secondary" onClick={() => removeAsset(slot)}>
            {t("assets.remove")}
          </button>
        ) : null}
        {fieldError(`assets.${slot}`) ? (
          <p className="field-error" role="alert">
            {fieldError(`assets.${slot}`)}
          </p>
        ) : null}
      </li>
    );
  }

  function titleField(slot: string, label: string, hint: string) {
    return (
      <label className="field">
        <span>{label}</span>
        <input
          type="text"
          value={assets[slot] ?? ""}
          maxLength={schema.titleLength}
          placeholder={hint}
          onChange={(e) => setTitle(slot, e.target.value)}
          aria-invalid={fieldError(`assets.${slot}`) ? true : undefined}
        />
        {fieldError(`assets.${slot}`) ? (
          <p className="field-error" role="alert">
            {fieldError(`assets.${slot}`)}
          </p>
        ) : null}
      </label>
    );
  }

  /** A design replaces the token values set so far; brand images, name and contrast policy stay (spec 036). */
  function chooseDesign(key: string) {
    const next: Record<string, TokenValue> = { [presetToken]: { $value: key } };
    setTokens(next);
    setStatus({ kind: "idle" });
    scheduleCheck(next, strict);
  }

  async function save(asDraft = false) {
    setStatus({ kind: "saving" });
    const response = await fetch(`/api/v1/experience/brands/${brand.id}${asDraft ? "/draft" : ""}`, {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ name, tokens, assets, strictContrast: strict }),
    });
    const body = (await response.json().catch(() => null)) as
      | (Partial<BrandView> & { errors?: ProblemErrors; findings?: ContrastFinding[]; detail?: string })
      | null;
    if (response.ok && asDraft) {
      setErrors({});
      setStatus({ kind: "saved", message: t("draftSaved") });
      router.refresh();
    } else if (response.ok) {
      setErrors({});
      setStatus({ kind: "saved", message: t("saved", { version: body?.version ?? brand.version + 1 }) });
      router.refresh();
    } else if (response.status === 422 && body?.findings) {
      setFindings(body.findings);
      setStatus({ kind: "blocked", message: t("blocked") });
    } else if (response.status === 400) {
      setErrors(body?.errors ?? {});
      setStatus({ kind: "failed", message: t("invalid") });
    } else {
      setStatus({ kind: "failed", message: body?.detail ?? t("saveFailed") });
    }
  }

  const fieldError = (field: string) => errors[field]?.[0]?.message;
  const modeName = (mode: string) => (["light", "dark", "high_contrast", "comfortable", "compact"].includes(mode) ? t(`modes.${mode}`) : mode);

  function input(entry: TokenSchemaEntry, mode: string | null) {
    const value = mode === null ? (tokens[entry.name]?.$value ?? "") : (tokens[entry.name]?.$modes?.[mode] ?? "");
    const placeholder = (mode === "dark" ? entry.dark : mode === "compact" ? entry.compact : entry.light) ?? entry.light;
    const label = t("tokenInput", { token: keyName(t, "tokenNames", entry.name), mode: modeName(mode ?? "light") });
    const field = `tokens.${entry.name}${mode ? `.${mode}` : ""}`;
    const error = fieldError(field) ?? (mode === null ? fieldError(`tokens.${entry.name}`) : undefined);
    const choices =
      entry.type === "font_family"
        ? (schema.fonts[entry.script ?? ""] ?? [])
        : entry.type === "easing"
          ? schema.easings
          : entry.type === "choice"
            ? (schema.choices[entry.name] ?? [])
            : null;
    return (
      <div className="brand-input">
        {choices ? (
          <select aria-label={label} value={value} onChange={(e) => setToken(entry, mode, e.target.value)} aria-invalid={error ? true : undefined}>
            <option value="">{t("useDefault", { value: placeholder })}</option>
            {choices.map((c) => (
              <option key={c} value={c}>
                {c}
              </option>
            ))}
          </select>
        ) : (
          <>
            {entry.type === "color" ? (
              <input
                type="color"
                className="brand-swatch"
                aria-label={t("pickColor", { token: keyName(t, "tokenNames", entry.name), mode: modeName(mode ?? "light") })}
                value={hex.test(value) ? value : hex.test(placeholder) ? placeholder : "#000000"}
                onChange={(e) => setToken(entry, mode, e.target.value)}
              />
            ) : null}
            <input
              type="text"
              dir="ltr"
              aria-label={label}
              value={value}
              placeholder={placeholder}
              spellCheck={false}
              onChange={(e) => setToken(entry, mode, e.target.value)}
              aria-invalid={error ? true : undefined}
            />
          </>
        )}
        {error ? (
          <p className="field-error" role="alert">
            {error}
          </p>
        ) : null}
      </div>
    );
  }

  return (
    <div className="brand-manager" data-testid="brand-manager">
      {/* Name, policy and save on the field grid (DLS-094, DLS-099): nothing shares a cramped row with the button. */}
      <section className="brand-settings" aria-label={t("name")}>
        <div className="ui-field-grid">
          {brands.length > 1 ? (
            <label className="field">
              <span>{t("brandLabel")}</span>
              <select value={brand.id} onChange={(e) => router.push(`/admin/branding?brand=${encodeURIComponent(e.target.value)}`)}>
                {brands.map((b) => (
                  <option key={b.id} value={b.id}>
                    {b.name}
                  </option>
                ))}
              </select>
            </label>
          ) : null}
          <label className="field">
            <span>{t("name")}</span>
            <input
              type="text"
              value={name}
              maxLength={80}
              onChange={(e) => {
                setName(e.target.value);
                setStatus({ kind: "idle" });
              }}
              aria-invalid={fieldError("name") ? true : undefined}
            />
            {fieldError("name") ? (
              <span className="field-error" role="alert">
                {fieldError("name")}
              </span>
            ) : null}
          </label>
        </div>
        <Checkbox
          checked={strict}
          label={t("strict")}
          onChange={(e) => {
            setStrict(e.target.checked);
            scheduleCheck(tokens, e.target.checked);
          }}
        />
        <div className="ui-form-actions">
          <Button variant="primary" onClick={() => void save()} busy={status.kind === "saving"} busyLabel={t("saving")}>
            {t("save")}
          </Button>
          {draft ? (
            <Button variant="secondary" onClick={() => void save(true)} disabled={status.kind === "saving"}>
              {t("saveDraft")}
            </Button>
          ) : null}
          <Badge>{t("version", { version: brand.version })}</Badge>
          <p className={`brand-status ${status.kind === "saved" ? "notice" : "field-error"}`} role="status" data-testid="brand-status">
            {status.message ?? ""}
          </p>
        </div>
      </section>

      <DesignPicker
        designs={schema.designs}
        current={tokens[presetToken]?.$value ?? schema.choices[presetToken]?.[0] ?? ""}
        onChoose={chooseDesign}
        labels={{
          title: t("design.title"),
          intro: t("design.intro"),
          replaces: t("design.replaces"),
          current: t("design.current"),
          choose: (name) => t("design.choose", { name }),
          name: (key) => (["beats", "atlas", "pulse", "studio"].includes(key) ? t(`design.names.${key}`) : key),
          summary: (key) => (["beats", "atlas", "pulse", "studio"].includes(key) ? t(`design.summaries.${key}`) : ""),
          navigation: (style) => (["rail", "top", "sidebar"].includes(style) ? t(`design.navigation.${style}`) : style),
        }}
      />

      <div className="brand-columns">
        <div className="brand-editor">
          {groups.map((group) => {
            const entries = schema.tokens.filter((e) => e.group === group);
            if (entries.length === 0) return null;
            const showDark = entries.some((e) => e.modes.includes("dark"));
            const showCompact = entries.some((e) => e.modes.includes("compact"));
            return (
              <details key={group} className="brand-group" open={group === "color"}>
                <summary>{t(`groups.${group}`)}</summary>
                <div className="brand-table-wrap">
                  <table className="brand-table">
                    <thead>
                      <tr>
                        <th scope="col">{t("columns.token")}</th>
                        <th scope="col">{showCompact ? t("modes.comfortable") : t("modes.light")}</th>
                        {showDark ? <th scope="col">{t("modes.dark")}</th> : null}
                        {showCompact ? <th scope="col">{t("modes.compact")}</th> : null}
                      </tr>
                    </thead>
                    <tbody>
                      {entries.map((entry) => (
                        <tr key={entry.name} data-token={entry.name}>
                          <th scope="row">
                            <KeyName name={keyName(t, "tokenNames", entry.name)} code={entry.name} />
                          </th>
                          <td>{input(entry, null)}</td>
                          {showDark ? <td>{entry.modes.includes("dark") ? input(entry, "dark") : <EmptyCell />}</td> : null}
                          {showCompact ? <td>{entry.modes.includes("compact") ? input(entry, "compact") : <EmptyCell />}</td> : null}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </details>
            );
          })}

          <section className="brand-section" aria-labelledby="brand-assets-title">
            <h2 id="brand-assets-title">{t("assets.title")}</h2>
            <p className="notice">{t("assets.rules")}</p>
            <div className="ui-field-grid">{titleField(schema.titleSlot, t("assets.titleAll"), t("assets.titleFallback"))}</div>
            <ul className="brand-assets">{schema.assetSlots.map((slot) => assetItem(slot, t(`assets.slots.${slot}`)))}</ul>
          </section>

          <section className="brand-section" aria-labelledby="brand-languages-title" data-testid="brand-languages">
            <h2 id="brand-languages-title">{t("assets.byLanguage")}</h2>
            <p className="notice">{t("assets.byLanguageRules")}</p>
            {locales.map((l) => (
              <fieldset key={l} className="brand-language" data-locale={l}>
                <legend>
                  <bdi lang={l}>{languageName(l)}</bdi>
                </legend>
                <div className="ui-field-grid">
                  {titleField(`${schema.titleSlot}.${l}`, t("assets.titleIn", { language: languageName(l) }), assets[schema.titleSlot] ?? t("assets.titleFallback"))}
                </div>
                <ul className="brand-assets">
                  {schema.languageSlots
                    .filter((slot) => slot === "logo" || slot === "lockup")
                    .map((slot) => assetItem(`${slot}.${l}`, t("assets.slotIn", { slot: t(`assets.slots.${slot}`), language: languageName(l) })))}
                </ul>
              </fieldset>
            ))}
          </section>
        </div>

        <div className="brand-side">
          <ContrastTable
            findings={findings}
            locale={locale}
            labels={{
              title: t("contrast.title"),
              token: t("contrast.token"),
              against: t("contrast.against"),
              mode: t("contrast.mode"),
              ratio: t("contrast.ratio"),
              required: t("contrast.required"),
              result: t("contrast.result"),
              blocking: t("contrast.blocking"),
              warning: t("contrast.warning"),
              empty: t("contrast.empty"),
              modeName,
            }}
          />
          <BrandPreview
            title={t("preview.title")}
            preview={preview}
            logoUrl={assetUrls.logo ?? null}
            logoUrls={{ en: assetUrls["logo.en"] ?? null, ar: assetUrls["logo.ar"] ?? null }}
            logoAlt={t("assets.slots.logo")}
            panes={[
              { mode: "light", dir: "ltr", lang: "en", sample: samples.ltr, label: t("preview.pane", { mode: t("modes.light"), direction: t("preview.ltr") }) },
              { mode: "dark", dir: "ltr", lang: "en", sample: samples.ltr, label: t("preview.pane", { mode: t("modes.dark"), direction: t("preview.ltr") }) },
              { mode: "light", dir: "rtl", lang: "ar", sample: samples.rtl, label: t("preview.pane", { mode: t("modes.light"), direction: t("preview.rtl") }) },
              { mode: "dark", dir: "rtl", lang: "ar", sample: samples.rtl, label: t("preview.pane", { mode: t("modes.dark"), direction: t("preview.rtl") }) },
            ]}
          />
        </div>
      </div>
    </div>
  );
}
