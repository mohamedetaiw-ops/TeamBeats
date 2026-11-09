import type { Block, BodyDocument, Inline } from "@/lib/content/types";

/** Inline runs with their marks; links open only http, https and mailto, which the API already enforces. */
function Runs({ runs }: { runs: Inline[] | null | undefined }) {
  return (
    <>
      {(runs ?? []).map((r, i) => {
        let node: React.ReactNode = r.text;
        if (r.marks?.includes("bold")) node = <strong>{node}</strong>;
        if (r.marks?.includes("italic")) node = <em>{node}</em>;
        if (r.link) node = <a href={r.link}>{node}</a>;
        return <bdi key={i}>{node}</bdi>;
      })}
    </>
  );
}

function BlockView({ block, embedTitle, anchor }: { block: Block; embedTitle: string; anchor?: string }) {
  switch (block.type) {
    case "heading": {
      const level = Math.min(Math.max(block.level ?? 2, 2), 4);
      const Tag = `h${level}` as "h2" | "h3" | "h4";
      return (
        <Tag id={anchor}>
          <Runs runs={block.children} />
        </Tag>
      );
    }
    case "quote":
      return (
        <blockquote>
          <Runs runs={block.children} />
        </blockquote>
      );
    case "list": {
      const items = (block.items ?? []).map((item, i) => (
        <li key={i}>
          <Runs runs={item} />
        </li>
      ));
      return block.ordered ? <ol>{items}</ol> : <ul>{items}</ul>;
    }
    case "table":
      return (
        <div className="content-table">
          <table>
            <tbody>
              {(block.rows ?? []).map((row, i) => (
                <tr key={i}>
                  {row.map((cell, j) => (
                    <td key={j}>
                      <Runs runs={cell} />
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      );
    case "image":
      // Content media comes with the document library (spec 011); until then the description stands in.
      return block.alt ? (
        <figure className="content-figure">
          <figcaption>
            <bdi>{block.alt}</bdi>
          </figcaption>
        </figure>
      ) : null;
    case "link":
      return block.url ? (
        <p>
          <a href={block.url}>
            <bdi>{block.text || block.url}</bdi>
          </a>
        </p>
      ) : null;
    case "embed":
      // Only hosts on the tenant's embed allowlist reach here (US7 scenario 3); the frame runs sandboxed.
      return block.url ? (
        <div className="content-embed">
          <iframe src={block.url} title={embedTitle} sandbox="allow-scripts allow-same-origin allow-presentation" loading="lazy" referrerPolicy="no-referrer" />
        </div>
      ) : null;
    default:
      return (
        <p>
          <Runs runs={block.children} />
        </p>
      );
  }
}

/**
 * A content body (spec 009 plan, body format) rendered as plain elements; the layout follows the document direction.
 * Anchors, when the API gives them, go on the headings in order, so a citation link (spec 011 US4) lands on its section.
 */
export function BodyView({ body, embedTitle, anchors }: { body: BodyDocument; embedTitle: string; anchors?: string[] | null }) {
  const headingIndex = body.blocks.map((b, i) => body.blocks.slice(0, i).filter((x) => x.type === "heading").length);
  return (
    <div className="content-body">
      {body.blocks.map((b, i) => (
        <BlockView key={i} block={b} embedTitle={embedTitle} anchor={b.type === "heading" ? (anchors?.[headingIndex[i] ?? -1] ?? undefined) : undefined} />
      ))}
    </div>
  );
}
