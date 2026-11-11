"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { CommandPalette } from "@/components/ui/CommandPalette";
import { Dialog, Drawer } from "@/components/ui/Dialog";
import { ContextMenu, Menu, MenuList, type MenuEntry } from "@/components/ui/Menu";
import { useMessages } from "@/components/ui/Messages";
import { Popover } from "@/components/ui/Popover";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { Switch } from "@/components/ui/Switch";
import { Tooltip } from "@/components/ui/Tooltip";

/** Key names are the same in every language, so they are identifiers, not messages. */
const keys = { rename: ["F2"], duplicate: ["Ctrl", "D"], palette: ["Ctrl", "K"] } as const;

/** Overlays and menus of the extended catalogue (spec 036 DLS-036, DLS-037), with realistic text in both languages. */
export function OverlaysGallery() {
  const t = useTranslations("components_gallery");
  const messages = useMessages();
  const [dialog, setDialog] = useState(false);
  const [drawer, setDrawer] = useState(false);
  const [palette, setPalette] = useState(false);
  const [details, setDetails] = useState(true);
  const [comments, setComments] = useState(false);
  const [chosen, setChosen] = useState<string | null>(null);

  // The shell binds this shortcut for real; here it shows the palette opening from the keyboard.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setPalette(true);
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);

  const pick = (key: string) => () => setChosen(t(`menus.${key}`));
  const fileActions: MenuEntry[] = [
    { key: "open", label: t("menus.open"), icon: "external", onSelect: pick("open") },
    { key: "rename", label: t("menus.rename"), icon: "edit", shortcut: keys.rename, onSelect: pick("rename") },
    { key: "duplicate", label: t("menus.duplicate"), icon: "files", shortcut: keys.duplicate, onSelect: pick("duplicate") },
    {
      key: "share",
      label: t("menus.share"),
      icon: "people",
      items: [
        { key: "shareLink", label: t("menus.shareLink"), icon: "link", onSelect: pick("shareLink") },
        { key: "shareEmail", label: t("menus.shareEmail"), icon: "inbox", onSelect: pick("shareEmail") },
        { key: "shareTeam", label: t("menus.shareTeam"), icon: "people", onSelect: pick("shareTeam") },
      ],
    },
    { key: "move", label: t("menus.moveLocked"), icon: "lock", disabled: true },
    { kind: "separator", key: "s1" },
    {
      kind: "group",
      key: "view",
      label: t("menus.view"),
      items: [
        { key: "details", label: t("menus.showDetails"), checked: details, onSelect: () => setDetails(!details) },
        { key: "comments", label: t("menus.showComments"), checked: comments, onSelect: () => setComments(!comments) },
      ],
    },
    { kind: "separator", key: "s2" },
    { key: "delete", label: t("menus.delete"), icon: "trash", danger: true, onSelect: pick("delete") },
  ];

  const run = (key: string) => () => setChosen(t(`overlays.${key}`));

  return (
    <>
      <section aria-labelledby="cg-overlays">
        <h2 id="cg-overlays">{t("overlays.title")}</h2>
        <p className="notice">{t("overlays.intro")}</p>
        <div className="gallery-row">
          <Button variant="primary" icon="plus" onClick={() => setDialog(true)}>
            {t("overlays.openDialog")}
          </Button>
          <Button icon="filter" onClick={() => setDrawer(true)}>
            {t("overlays.openDrawer")}
          </Button>
          <Popover label={t("overlays.popover")} title={t("overlays.popoverTitle")}>
            <Switch label={t("overlays.compact")} hint={t("overlays.compactHint")} name="cg-compact" />
            <SegmentedControl
              label={t("overlays.density")}
              name="cg-density"
              defaultValue="standard"
              options={(["few", "standard", "all"] as const).map((key) => ({ value: key, label: t(`overlays.${key}`) }))}
            />
          </Popover>
          <Tooltip text={t("overlays.exportTip")}>
            <Button icon="download">{t("overlays.export")}</Button>
          </Tooltip>
          <Button
            icon="bell"
            onClick={() =>
              messages.show({
                priority: "normal",
                title: t("overlays.invited"),
                text: t("overlays.invitedText"),
                action: { label: t("overlays.undo"), run: () => undefined },
              })
            }
          >
            {t("overlays.notify")}
          </Button>
          <Button icon="search" onClick={() => setPalette(true)}>
            {t("overlays.openPalette")}
          </Button>
        </div>

        <Dialog
          open={dialog}
          onClose={() => setDialog(false)}
          title={t("overlays.dialogTitle")}
          description={t("overlays.dialogText")}
          footer={
            <>
              <Button onClick={() => setDialog(false)}>{t("overlays.cancel")}</Button>
              <Button
                variant="primary"
                onClick={() => {
                  setDialog(false);
                  messages.show({ priority: "normal", title: t("overlays.invited"), text: t("overlays.invitedText") });
                }}
              >
                {t("overlays.send")}
              </Button>
            </>
          }
        >
          <div className="gallery-stack">
            <RadioGroup
              legend={t("overlays.role")}
              name="cg-role"
              defaultValue="reader"
              options={(["reader", "editor"] as const).map((key) => ({ value: key, label: t(`overlays.${key}`) }))}
            />
            <Checkbox label={t("overlays.dialogNotify")} hint={t("overlays.dialogNotifyHint")} name="cg-invite-email" defaultChecked />
          </div>
        </Dialog>

        <Drawer
          open={drawer}
          onClose={() => setDrawer(false)}
          title={t("overlays.drawerTitle")}
          description={t("overlays.drawerText")}
          footer={
            <>
              <Button variant="quiet" onClick={() => setDrawer(false)}>
                {t("overlays.clear")}
              </Button>
              <Button variant="primary" onClick={() => setDrawer(false)}>
                {t("overlays.apply")}
              </Button>
            </>
          }
        >
          <div className="gallery-stack">
            <Checkbox label={t("overlays.typePolicy")} name="cg-type-policy" defaultChecked />
            <Checkbox label={t("overlays.typeForm")} name="cg-type-form" />
            <Checkbox label={t("overlays.typeReport")} name="cg-type-report" />
          </div>
        </Drawer>

        <CommandPalette
          open={palette}
          onClose={() => setPalette(false)}
          label={t("overlays.paletteLabel")}
          placeholder={t("overlays.palettePlaceholder")}
          empty={t("overlays.paletteEmpty")}
          groups={[
            {
              key: "pages",
              label: t("overlays.pages"),
              commands: (
                [
                  ["pageHome", "home"],
                  ["pageServices", "services"],
                  ["pagePolicies", "policy"],
                  ["pagePeople", "people"],
                ] as const
              ).map(([key, icon]) => ({ key, label: t(`overlays.${key}`), hint: t(`overlays.${key}Hint`), icon, onSelect: run(key) })),
            },
            {
              key: "commands",
              label: t("overlays.commands"),
              commands: [
                { key: "commandLeave", label: t("overlays.commandLeave"), icon: "calendar", onSelect: run("commandLeave") },
                { key: "commandTheme", label: t("overlays.commandTheme"), icon: "settings", onSelect: run("commandTheme") },
                { key: "commandLanguage", label: t("overlays.commandLanguage"), icon: "globe", onSelect: run("commandLanguage") },
              ],
            },
          ]}
        />
      </section>

      <section aria-labelledby="cg-menus">
        <h2 id="cg-menus">{t("menus.title")}</h2>
        <p className="notice">{t("menus.intro")}</p>
        <div className="gallery-columns">
          <div className="gallery-stack">
            <div className="gallery-row">
              <Menu label={t("menus.actions")} items={fileActions} />
              <Menu label={t("menus.more")} iconOnly variant="quiet" items={fileActions} placement="bottom-end" />
            </div>
            <ContextMenu label={t("menus.contextLabel")} items={fileActions} className="gallery-stack">
              <p className="notice">{t("menus.contextHint")}</p>
              <div className="gallery-row">
                <Button variant="quiet" icon="files">
                  {t("menus.file")}
                </Button>
              </div>
            </ContextMenu>
            <p className="notice" role="status" data-testid="cg-chosen">
              {chosen ? t("menus.chosen", { name: chosen }) : t("menus.none")}
            </p>
            <Breadcrumb
              label={t("menus.trail")}
              crumbs={(["crumbHome", "crumbKnowledge", "crumbFinance", "crumbTravel", "crumbPolicies", "crumbPage"] as const).map((key) => ({
                href: `#${key}`,
                label: t(`menus.${key}`),
              }))}
            />
          </div>
          <div className="gallery-stack">
            <p className="notice">{t("menus.preview")}</p>
            <div className="ui-menu-preview">
              <MenuList label={t("menus.preview")} items={fileActions} onDone={() => undefined} />
            </div>
          </div>
        </div>
      </section>
    </>
  );
}
