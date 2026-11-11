import { getTranslations } from "next-intl/server";
import { Icon } from "@/components/ui/Icon";
import { getShell } from "@/lib/shell/client";
import "./footer.css";

/**
 * The page footer (spec 008 FR-056, docs/design-system/components.md Shell footer): the organization, the policy
 * and accessibility links and the classification, all as the tenant configured them in the shell settings. Nothing
 * is invented: a tenant that configured no footer, or a signed-out page, gets none.
 */
export async function ShellFooter() {
  const shell = await getShell();
  const footer = shell.ok ? shell.data.footer : null;
  if (!footer) {
    return null;
  }
  const t = await getTranslations("shell.footer");
  return (
    <footer className="shell-footer">
      <div className="shell-footer-inner">
        {footer.organization ? <p className="shell-footer-org">{footer.organization}</p> : null}
        {footer.links.length > 0 ? (
          <nav aria-label={t("label")}>
            <ul className="shell-footer-links">
              {footer.links.map((link) => (
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
