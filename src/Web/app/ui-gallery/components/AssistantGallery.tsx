"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { AgentForm, Cite, Composer, QuestionSteps, RunProgress, SourceList, ThinkingIndicator, UsageLimit, type Attachment } from "@/components/ui/Assistant";

/** Assistant and agent parts of the extended catalogue (spec 036 DLS-039), with the content of a real answer. */
export function AssistantGallery() {
  const t = useTranslations("components_gallery");
  const [text, setText] = useState("");
  const [sent, setSent] = useState<string | null>(null);
  const [files, setFiles] = useState<Attachment[]>([{ key: "trip", name: t("assistant.fileTrip"), detail: t("assistant.fileTripSize") }]);
  const [answers, setAnswers] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});

  return (
    <>
      <section aria-labelledby="cg-assistant">
        <h2 id="cg-assistant">{t("assistant.title")}</h2>
        <p className="notice">{t("assistant.intro")}</p>
        <div className="gallery-columns">
          <div className="gallery-stack">
            <p className="ui-bubble">{t("assistant.question")}</p>
            <ThinkingIndicator step={t("assistant.searching")} />
            <p>
              {t("assistant.answerStart")}
              <Cite number={1} idPrefix="cg-src" title={t("assistant.sourceBalance")} />
              {t("assistant.answerMiddle")}
              <Cite number={2} idPrefix="cg-src" title={t("assistant.sourcePolicy")} />
              {t("assistant.answerEnd")}
            </p>
            <SourceList
              label={t("assistant.sources")}
              idPrefix="cg-src"
              sources={[
                { key: "balance", title: t("assistant.sourceBalance"), href: "/ui-gallery/components", origin: t("assistant.sourceBalanceOrigin"), icon: "calendar" },
                {
                  key: "policy",
                  title: t("assistant.sourcePolicy"),
                  href: "/ui-gallery/components",
                  origin: t("assistant.sourcePolicyOrigin"),
                  icon: "policy",
                  excerpt: t("assistant.sourcePolicyExcerpt"),
                },
              ]}
            />
          </div>
          <div className="gallery-stack">
            <Composer
              label={t("assistant.composer")}
              placeholder={t("assistant.placeholder")}
              value={text}
              onValueChange={setText}
              onSend={(value) => {
                setSent(value);
                setText("");
              }}
              attachments={files}
              onAttach={(chosen) => setFiles((all) => [...all, ...chosen.map((f) => ({ key: f.name, name: f.name }))])}
              onRemove={(key) => setFiles((all) => all.filter((f) => f.key !== key))}
              notice={t("assistant.limitLine")}
            />
            <p className="notice" data-testid="cg-sent" aria-live="polite">
              {sent === null ? "" : t("assistant.sent", { text: sent })}
            </p>
            <UsageLimit label={t("assistant.usage")} used={18} limit={20} valueText={t("assistant.usageValue")} resetText={t("assistant.usageReset")} />
            <UsageLimit label={t("assistant.usageTeam")} used={500} limit={500} valueText={t("assistant.usageTeamValue")} resetText={t("assistant.usageTeamReset")} />
          </div>
        </div>
      </section>

      <section aria-labelledby="cg-agents">
        <h2 id="cg-agents">{t("agents.title")}</h2>
        <p className="notice">{t("agents.intro")}</p>
        <div className="gallery-columns">
          <div className="gallery-stack">
            <QuestionSteps
              title={t("agents.stepsTitle")}
              summaryTitle={t("agents.summary")}
              onDone={(all) => setAnswers(Object.values(all).join(", "))}
              questions={[
                {
                  key: "city",
                  question: t("agents.q1"),
                  options: [
                    { value: "riyadh", label: t("agents.q1a") },
                    { value: "london", label: t("agents.q1b") },
                    { value: "singapore", label: t("agents.q1c") },
                  ],
                },
                {
                  key: "class",
                  question: t("agents.q2"),
                  options: [
                    { value: "economy", label: t("agents.q2a"), hint: t("agents.q2aHint") },
                    { value: "business", label: t("agents.q2b"), hint: t("agents.q2bHint") },
                  ],
                },
                {
                  key: "hotel",
                  question: t("agents.q3"),
                  options: [
                    { value: "yes", label: t("agents.q3a") },
                    { value: "no", label: t("agents.q3b") },
                  ],
                },
              ]}
            />
            <p className="notice" data-testid="cg-answers" aria-live="polite">
              {answers === null ? "" : t("agents.stepsSent")}
            </p>
            <RunProgress
              title={t("agents.runTitle")}
              steps={[
                { key: "read", label: t("agents.stepRead"), status: "done", duration: t("agents.seconds") },
                { key: "find", label: t("agents.stepFind"), status: "done", duration: t("agents.seconds") },
                { key: "draft", label: t("agents.stepDraft"), status: "running", detail: t("agents.draftDetail") },
                { key: "approve", label: t("agents.stepApprove"), status: "waiting" },
                { key: "send", label: t("agents.stepSend"), status: "waiting" },
              ]}
            />
          </div>
          <div className="gallery-stack">
            <AgentForm
              title={t("agents.formTitle")}
              intro={t("agents.formIntro")}
              submitLabel={t("agents.run")}
              errors={errors}
              onSubmit={(values) => setErrors(values.item ? {} : { item: t("agents.itemMissing") })}
              inputs={[
                { key: "item", label: t("agents.item"), hint: t("agents.itemHint"), required: true, kind: "text" },
                {
                  key: "budget",
                  label: t("agents.budget"),
                  required: true,
                  kind: "choice",
                  options: [
                    { value: "finance", label: t("agents.budgetFinance") },
                    { value: "engineering", label: t("agents.budgetEngineering") },
                  ],
                },
                { key: "neededBy", label: t("agents.neededBy"), kind: "date" },
                { key: "notes", label: t("agents.notes"), kind: "long" },
              ]}
            />
          </div>
        </div>
      </section>
    </>
  );
}
