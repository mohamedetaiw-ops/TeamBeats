import { $createLinkNode, $isLinkNode } from "@lexical/link";
import { $createListItemNode, $createListNode, $isListItemNode, $isListNode } from "@lexical/list";
import { $createHeadingNode, $createQuoteNode, $isHeadingNode, $isQuoteNode, type HeadingTagType } from "@lexical/rich-text";
import {
  $createParagraphNode,
  $createTextNode,
  $getRoot,
  $isElementNode,
  $isLineBreakNode,
  $isParagraphNode,
  $isTextNode,
  type ElementNode,
  type LexicalNode,
} from "lexical";
import type { Block, BodyDocument, Inline } from "@/lib/content/types";
import { $createAtomicBlockNode, $isAtomicBlockNode } from "./AtomicBlockNode";

/** The marks the body document keeps; other Lexical formats (underline, code) are dropped on save. */
const keptMarks = ["bold", "italic"] as const;

function $appendRuns(parent: ElementNode, runs: Inline[] | null | undefined) {
  for (const run of runs ?? []) {
    const text = $createTextNode(run.text);
    for (const mark of run.marks ?? []) {
      if ((keptMarks as readonly string[]).includes(mark)) text.toggleFormat(mark as (typeof keptMarks)[number]);
    }
    if (run.link) {
      const link = $createLinkNode(run.link);
      link.append(text);
      parent.append(link);
    } else {
      parent.append(text);
    }
  }
}

/** Fills the editor from a body document; called inside an editor update. */
export function $loadBody(body: BodyDocument) {
  const root = $getRoot();
  root.clear();
  for (const block of body.blocks) {
    switch (block.type) {
      case "paragraph": {
        const p = $createParagraphNode();
        $appendRuns(p, block.children);
        root.append(p);
        break;
      }
      case "heading": {
        const level = Math.min(Math.max(block.level ?? 2, 2), 4);
        const h = $createHeadingNode(`h${level}` as HeadingTagType);
        $appendRuns(h, block.children);
        root.append(h);
        break;
      }
      case "quote": {
        const q = $createQuoteNode();
        $appendRuns(q, block.children);
        root.append(q);
        break;
      }
      case "list": {
        const list = $createListNode(block.ordered ? "number" : "bullet");
        for (const item of block.items ?? []) {
          const li = $createListItemNode();
          $appendRuns(li, item);
          list.append(li);
        }
        root.append(list);
        break;
      }
      default:
        root.append($createAtomicBlockNode(block));
    }
  }
  if (root.getChildrenSize() === 0) {
    root.append($createParagraphNode());
  }
}

function same(a: Inline, b: Inline) {
  return (a.link ?? null) === (b.link ?? null) && (a.marks ?? []).join() === (b.marks ?? []).join();
}

function $runs(element: ElementNode, link: string | null = null, out: Inline[] = []): Inline[] {
  for (const child of element.getChildren()) {
    if ($isLinkNode(child)) {
      $runs(child, child.getURL(), out);
      continue;
    }
    if ($isListNode(child)) {
      // A nested list keeps its text in the parent item; the body document has one list level.
      for (const item of child.getChildren()) {
        if ($isElementNode(item)) {
          out.push({ text: " " });
          $runs(item, link, out);
        }
      }
      continue;
    }
    let run: Inline | null = null;
    if ($isTextNode(child)) {
      const marks = keptMarks.filter((m) => child.hasFormat(m));
      run = { text: child.getTextContent(), ...(marks.length > 0 ? { marks } : {}), ...(link ? { link } : {}) };
    } else if ($isLineBreakNode(child)) {
      run = { text: "\n", ...(link ? { link } : {}) };
    } else if ($isElementNode(child)) {
      $runs(child, link, out);
    }
    if (!run || run.text === "") continue;
    const last = out[out.length - 1];
    if (last && same(last, run)) {
      last.text += run.text;
    } else {
      out.push(run);
    }
  }
  return out;
}

const hasText = (runs: Inline[]) => runs.some((r) => r.text.trim() !== "");

/** Reads the editor back into a body document; called inside an editor read. Empty text blocks are left out. */
export function $readBody(): BodyDocument {
  const blocks: Block[] = [];
  for (const node of $getRoot().getChildren() as LexicalNode[]) {
    if ($isAtomicBlockNode(node)) {
      blocks.push(node.getBlock());
    } else if ($isHeadingNode(node)) {
      const runs = $runs(node);
      if (hasText(runs)) blocks.push({ type: "heading", level: Number(node.getTag().slice(1)) || 2, children: runs });
    } else if ($isQuoteNode(node)) {
      const runs = $runs(node);
      if (hasText(runs)) blocks.push({ type: "quote", children: runs });
    } else if ($isListNode(node)) {
      const items = node
        .getChildren()
        .filter($isListItemNode)
        .map((li) => $runs(li))
        .filter(hasText);
      if (items.length > 0) blocks.push({ type: "list", ordered: node.getListType() === "number", items });
    } else if ($isParagraphNode(node) || $isElementNode(node)) {
      const runs = $runs(node);
      if (hasText(runs)) blocks.push({ type: "paragraph", children: runs });
    }
  }
  return { blocks };
}
