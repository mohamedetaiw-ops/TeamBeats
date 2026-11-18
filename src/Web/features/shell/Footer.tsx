import { getTranslations } from "next-intl/server";
import { Icon } from "@/components/ui/Icon";
import { getShell } from "@/lib/shell/client";
import "./footer.css";

/**
 * The page footer (spec 008 FR-056, docs/design-system/components.md Shell footer): the organization, the policy
 * and accessibility links and the classification, all as the tenant configured them in the shell settings. Nothing
 * is invented: a tenant that configured no footer, or a signed-out page, gets none. A footer with no accessibility link
 * also links the TeamBeats accessibility statement.
 */
export async function ShellFooter() {
  const shell = await getShell();
  const footer = shell.ok ? shell.data.footer : null;
  if (!footer) {
    return null;
  }
  const t = await getTranslations("shell.footer");
  // The TeamBeats statement stands in for the tenant's own accessibility page when it links none (spec 038 A11Y-023).
  const links = footer.links.some((link) => link.kind === "accessibility")
    ? footer.links
    : [...footer.links, { kind: "accessibility" as const, label: t("accessibility"), href: "/accessibility" }];
  return (
    <footer className="shell-footer">
      <div className="shell-footer-inner">
        {footer.organization ? <p className="shell-footer-org">{footer.organization}</p> : null}
        {links.length > 0 ? (
          <nav aria-label={t("label")}>
            <ul className="shell-footer-links">
              {links.map((link) => (
                <li key={`${link.kind}-${link.href}-${link.label}`}>
                  <a href={link.href} rel={link.href.startsWith("/") ? undefined : "noopener noreferrer"}>
                    {link.label}
                  </a>
                </li>
              ))}
            </ul>
          </nav>
        ) : null}
        {footer.classification ? (
          <p className="shell-footer-classification">
            <Icon name="lock" size={14} />
            <span>{t("classification", { value: footer.classification })}</span>
          </p>
        ) : null}
      </div>
    </footer>
  );
}
