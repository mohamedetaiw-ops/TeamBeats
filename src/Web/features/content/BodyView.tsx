import { topicHref, type Block, type BodyDocument, type Inline } from "@/lib/content/types";
import { assetImageUrl } from "@/lib/assets/types";

const hashtag = /(^|[^\p{L}\p{N}_#&])#([\p{L}\p{N}_]{2,50})(?![\p{L}\p{N}_])/gu;

const hashOf = (tag: string) => "#".concat(tag);

/** Text with each hashtag as a link to its topic page (spec 009 US9 scenario 3). */
function Tagged({ text }: { text: string }) {
  const parts: React.ReactNode[] = [];
  let last = 0;
  for (const match of text.matchAll(hashtag)) {
    const lead = match[1] ?? "";
    const tag = match[2] ?? "";
    const start = (match.index ?? 0) + lead.length;
    if (start > last) parts.push(text.slice(last, start));
    parts.push(
      <a key={start} href={topicHref(tag.toLowerCase())}>
        {hashOf(tag)}
      </a>,
    );
    last = start + tag.length + 1;
  }
  if (parts.length === 0) return <>{text}</>;
  if (last < text.length) parts.push(text.slice(last));
  return <>{parts}</>;
}

/**
 * Inline runs with their marks; links open only http, https and mailto, which the API already enforces. A mention
 * opens the person's profile, which the directory shows only to those allowed to see it.
 */
function Runs({ runs, topics }: { runs: Inline[] | null | undefined; topics?: boolean }) {
  return (
    <>
      {(runs ?? []).map((r, i) => {
        let node: React.ReactNode = r.mention || r.link || !topics ? r.text : <Tagged text={r.text} />;
        if (r.mention) node = <a href={`/people/${encodeURIComponent(r.mention)}`} className="content-mention">{node}</a>;
        if (r.marks?.includes("bold")) node = <strong>{node}</strong>;
        if (r.marks?.includes("italic")) node = <em>{node}</em>;
        if (r.link) node = <a href={r.link}>{node}</a>;
        return <bdi key={i}>{node}</bdi>;
      })}
    </>
  );
}

function BlockView({ block, embedTitle, anchor, topics }: { block: Block; embedTitle: string; anchor?: string; topics?: boolean }) {
  switch (block.type) {
    case "heading": {
      const level = Math.min(Math.max(block.level ?? 2, 2), 4);
      const Tag = `h${level}` as "h2" | "h3" | "h4";
      return (
        <Tag id={anchor}>
          <Runs topics={topics} runs={block.children} />
        </Tag>
      );
    }
    case "quote":
      return (
        <blockquote>
          <Runs topics={topics} runs={block.children} />
        </blockquote>
      );
    case "list": {
      const items = (block.items ?? []).map((item, i) => (
        <li key={i}>
          <Runs topics={topics} runs={item} />
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
                      <Runs topics={topics} runs={cell} />
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      );
    case "image":
      // A brand asset image (spec 011 US5). The API serves it only while its usage rights last and to readers of its
      // collection; otherwise the browser shows the description in its place.
      return block.assetId ? (
        <figure className="content-figure">
          {/* eslint-disable-next-line @next/next/no-img-element -- served by the authenticated API, which checks each reader */}
          <img src={assetImageUrl(block.assetId)} alt={block.alt ?? ""} loading="lazy" />
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
          <Runs topics={topics} runs={block.children} />
        </p>
      );
  }
}

/**
 * A content body (spec 009 plan, body format) rendered as plain elements; the layout follows the document direction.
 * Anchors, when the API gives them, go on the headings in order, so a citation link (spec 011 US4) lands on its section.
 * With `topics`, hashtags link to their content topic pages; other bodies (community posts, policies) keep their own.
 */
export function BodyView({ body, embedTitle, anchors, topics }: { body: BodyDocument; embedTitle: string; anchors?: string[] | null; topics?: boolean }) {
  const headingIndex = body.blocks.map((b, i) => body.blocks.slice(0, i).filter((x) => x.type === "heading").length);
  return (
    <div className="content-body">
      {body.blocks.map((b, i) => (
        <BlockView key={i} block={b} embedTitle={embedTitle} topics={topics} anchor={b.type === "heading" ? (anchors?.[headingIndex[i] ?? -1] ?? undefined) : undefined} />
      ))}
    </div>
  );
}
