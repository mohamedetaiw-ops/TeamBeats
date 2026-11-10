import Link from "next/link";

/**
 * Section tabs (docs/design-system/components.md): the pill segmented control under a page header that moves between
 * the pages of one area, such as My policies and the register. Each item is a link, the current one is marked with
 * aria-current, and the row scrolls sideways on a phone instead of wrapping.
 */
export function SectionNav({
  label,
  items,
  current,
}: {
  label: string;
  items: readonly { key: string; href: string; label: string }[];
  current: string | null;
}) {
  return (
    <nav aria-label={label} className="ui-section-nav">
      <ul>
        {items.map((item) => (
          <li key={item.key}>
            <Link href={item.href} aria-current={current === item.key ? "page" : undefined}>
              {item.label}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
