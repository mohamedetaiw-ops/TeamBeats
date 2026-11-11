"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Calendar, DateField, DateRangeField, SlotPicker } from "@/components/ui/Calendar";
import { Card } from "@/components/ui/Card";
import { Carousel } from "@/components/ui/Carousel";
import { Combobox } from "@/components/ui/Combobox";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { FormField } from "@/components/ui/FormField";
import { ColorField, NumberField, OtpField, SearchField, TimeField } from "@/components/ui/Inputs";
import { TreeView, type TreeNode } from "@/components/ui/TreeView";
import { weekdayOf, type DateRange } from "@/components/ui/dates";

/** A fixed day, so the gallery and its visual baselines do not change from one day to the next. */
const today = "2026-11-10";
const presets = [
  ["presetIndigo", "#3b2fc9"],
  ["presetGreen", "#0b6e4f"],
  ["presetRose", "#c2185b"],
  ["presetBlue", "#155dfc"],
] as const;
const departments = ["roleEngineering", "roleFinance", "rolePeople", "roleOperations", "roleLegal", "roleSales"] as const;
const services = ["cardTravel", "cardEquipment", "cardParking", "cardLetter", "cardTraining"] as const;
/** Friday and Saturday, the weekend of the sample tenant. */
const weekend = (iso: string) => weekdayOf(iso) === 5 || weekdayOf(iso) === 6;

/** Inputs, pickers and dates of the extended catalogue (spec 036 DLS-035, DLS-036, DLS-038). */
export function InputsGallery() {
  const t = useTranslations("components_gallery");
  const [term, setTerm] = useState("");
  const [searched, setSearched] = useState<string | null>(null);
  const [days, setDays] = useState<number | null>(5);
  const [code, setCode] = useState("");
  const [color, setColor] = useState("#155dfc");
  const [department, setDepartment] = useState<string | null>("roleFinance");
  const [files, setFiles] = useState<UploadItem[]>([
    { key: "report", name: t("inputs.fileReport"), size: 4_400_000, progress: 60 },
    { key: "budget", name: t("inputs.fileBudget"), size: 27_300_000, error: t("inputs.uploadTooLarge") },
  ]);
  const [start, setStart] = useState<string | null>("2026-11-12");
  const [hijri, setHijri] = useState<DateRange>({ start: "2026-11-15", end: "2026-11-19" });
  const [date, setDate] = useState<string | null>(null);
  const [range, setRange] = useState<DateRange>({ start: "2026-11-22", end: "2026-11-26" });
  const [slot, setSlot] = useState<string | null>("10:30");
  const [node, setNode] = useState<string | null>("nodeTravel");

  const tree: TreeNode[] = [
    {
      key: "nodeCompany",
      label: t("dates.nodeCompany"),
      icon: "home",
      children: [
        { key: "nodeHandbook", label: t("dates.nodeHandbook"), icon: "book" },
        { key: "nodeBenefits", label: t("dates.nodeBenefits"), icon: "star" },
        { key: "nodeLeave", label: t("dates.nodeLeave"), icon: "calendar" },
      ],
    },
    {
      key: "nodeFinance",
      label: t("dates.nodeFinance"),
      icon: "files",
      children: [
        { key: "nodeExpenses", label: t("dates.nodeExpenses") },
        { key: "nodeTravel", label: t("dates.nodeTravel") },
        { key: "nodeProcurement", label: t("dates.nodeProcurement") },
      ],
    },
    {
      key: "nodeIt",
      label: t("dates.nodeIt"),
      icon: "settings",
      children: [
        { key: "nodeSecurity", label: t("dates.nodeSecurity"), icon: "lock" },
        { key: "nodeDevices", label: t("dates.nodeDevices") },
      ],
    },
  ];

  return (
    <>
      <section aria-labelledby="cg-inputs">
        <h2 id="cg-inputs">{t("inputs.title")}</h2>
        <p className="notice">{t("inputs.intro")}</p>
        <div className="gallery-columns">
          <div className="gallery-stack">
            <FormField label={t("inputs.amount")} hint={t("inputs.amountHint")} name="cg-amount" inputMode="decimal" defaultValue="1250.00" leading={t("inputs.currency")} />
            <FormField label={t("inputs.site")} name="cg-site" dir="ltr" defaultValue="finance" trailing={<bdi>{t("inputs.siteDomain")}</bdi>} />
            <SearchField label={t("inputs.search")} name="cg-search" value={term} onValueChange={setTerm} onSearch={setSearched} />
            <p className="notice" role="status" data-testid="cg-searched">
              {searched ? t("inputs.searched", { term: searched }) : t("inputs.notSearched")}
            </p>
            <NumberField
              label={t("inputs.days")}
              hint={t("inputs.daysHint")}
              error={days !== null && days > 22 ? t("inputs.daysError") : null}
              name="cg-days"
              unit={t("inputs.daysUnit")}
              min={1}
              max={22}
              value={days}
              onValueChange={setDays}
            />
          </div>
          <div className="gallery-stack">
            <OtpField label={t("inputs.code")} hint={t("inputs.codeHint")} name="cg-code" value={code} onValueChange={setCode} />
            <p className="notice" role="status" data-testid="cg-code">
              {code.length === 6 ? t("inputs.codeDone", { code }) : null}
            </p>
            <TimeField label={t("inputs.time")} name="cg-time" defaultValue="09:30" step={900} />
            <ColorField
              label={t("inputs.color")}
              hint={t("inputs.colorHint")}
              name="cg-color"
              value={color}
              onValueChange={setColor}
              presets={presets.map(([key, value]) => ({ value, label: t(`inputs.${key}`) }))}
            />
          </div>
          <div className="gallery-stack">
            <Combobox
              label={t("inputs.manager")}
              hint={t("inputs.managerHint")}
              name="cg-department"
              placeholder={t("inputs.managerPlaceholder")}
              empty={t("inputs.managerEmpty")}
              options={departments.map((key) => ({ value: key, label: t(`inputs.${key}`) }))}
              value={department}
              onValueChange={setDepartment}
            />
            <FileUpload
              label={t("inputs.upload")}
              hint={t("inputs.uploadHint")}
              accept=".pdf,.doc,.docx"
              items={files}
              onAdd={(added) => setFiles([...files, ...added.map((file, i) => ({ key: `${file.name}-${files.length + i}`, name: file.name, size: file.size }))])}
              onRemove={(key) => setFiles(files.filter((file) => file.key !== key))}
            />
          </div>
        </div>
      </section>

      <section aria-labelledby="cg-dates">
        <h2 id="cg-dates">{t("dates.title")}</h2>
        <p className="notice">{t("dates.intro")}</p>
        <div className="gallery-stack">
          <div className="gallery-columns">
            <div className="gallery-stack">
              <p className="notice">{t("dates.gregorianCaption")}</p>
              <Calendar label={t("dates.gregorian")} today={today} value={start} onValueChange={setStart} min="2026-11-01" max="2027-01-31" isUnavailable={weekend} />
            </div>
            <div className="gallery-stack">
              <p className="notice">{t("dates.hijriCaption")}</p>
              <Calendar label={t("dates.hijri")} today={today} calendar="islamic-umalqura" range={hijri} onRangeChange={setHijri} />
            </div>
            <div className="gallery-stack">
              <DateField
                label={t("dates.date")}
                hint={t("dates.dateHint")}
                name="cg-date"
                placeholder={t("dates.datePlaceholder")}
                clearLabel={t("dates.clear")}
                today={today}
                value={date}
                onValueChange={setDate}
                isUnavailable={weekend}
              />
              <DateRangeField
                label={t("dates.range")}
                name="cg-range"
                placeholder={t("dates.rangePlaceholder")}
                clearLabel={t("dates.clear")}
                today={today}
                range={range}
                onRangeChange={setRange}
              />
              <SlotPicker
                legend={t("dates.slots")}
                name="cg-slot"
                empty={t("dates.slotsEmpty")}
                value={slot}
                onValueChange={setSlot}
                slots={[
                  { time: "09:00" },
                  { time: "09:30", unavailable: true, note: t("dates.booked") },
                  { time: "10:00" },
                  { time: "10:30" },
                  { time: "11:00", unavailable: true, note: t("dates.booked") },
                  { time: "14:00" },
                ]}
              />
            </div>
          </div>
          <div className="gallery-columns">
            <Carousel label={t("dates.carousel")}>
              {services.map((key) => (
                <Card key={key} title={t(`dates.${key}`)}>
                  <p>{t(`dates.${key}Text`)}</p>
                </Card>
              ))}
            </Carousel>
            <div className="gallery-stack">
              <TreeView label={t("dates.tree")} nodes={tree} selected={node} onSelect={setNode} defaultExpanded={["nodeFinance"]} />
              <p className="notice" role="status" data-testid="cg-node">
                {node ? t("dates.treeChosen", { name: t(`dates.${node}`) }) : null}
              </p>
            </div>
          </div>
        </div>
      </section>
    </>
  );
}
