"use client";

import type { JSX } from "react";
import { useTranslations } from "next-intl";
import { useLexicalComposerContext } from "@lexical/react/LexicalComposerContext";
import { $getNodeByKey, DecoratorNode, type LexicalNode, type NodeKey, type SerializedLexicalNode, type Spread } from "lexical";
import type { Block } from "@/lib/content/types";
import { Button } from "@/components/ui/Button";

type SerializedAtomicBlock = Spread<{ block: Block }, SerializedLexicalNode>;

/** What a reader would see of the block, in one line. */
function describe(block: Block): string {
  if (block.type === "embed" || block.type === "link") return block.url ?? "";
  if (block.type === "image") return block.alt ?? "";
  if (block.type === "table") return String(block.rows?.length ?? 0);
  return "";
}

function AtomicCard({ nodeKey, block }: { nodeKey: NodeKey; block: Block }) {
  const t = useTranslations("content.editor.atomic");
  const [editor] = useLexicalComposerContext();
  const kind = block.type === "embed" || block.type === "image" || block.type === "table" || block.type === "link" ? block.type : "other";
  return (
    <div className="content-atomic" data-block={block.type} contentEditable={false}>
      <span className="ui-badge content-badge" data-tone="accent">{t(kind)}</span>
      <bdi className="content-atomic-text">{block.type === "link" && block.text ? `${block.text} (${describe(block)})` : describe(block)}</bdi>
      <Button variant="quiet"
        onClick={() =>
          editor.update(() => {
            $getNodeByKey(nodeKey)?.remove();
          })
        }
      >
        {t("remove")}
      </Button>
    </div>
  );
}

/**
 * A block the text editor does not edit inline (embed, image, table, link card): kept as it is and shown as a card the
 * author can remove. The block document stays the source of truth, so these round trip unchanged (spec 009 T022).
 */
export class AtomicBlockNode extends DecoratorNode<JSX.Element> {
  __block: Block;

  static getType(): string {
    return "tb-atomic-block";
  }

  static clone(node: AtomicBlockNode): AtomicBlockNode {
    return new AtomicBlockNode(node.__block, node.__key);
  }

  static importJSON(serialized: SerializedAtomicBlock): AtomicBlockNode {
    return new AtomicBlockNode(serialized.block);
  }

  constructor(block: Block, key?: NodeKey) {
    super(key);
    this.__block = block;
  }

  exportJSON(): SerializedAtomicBlock {
    return { ...super.exportJSON(), type: AtomicBlockNode.getType(), version: 1, block: this.__block };
  }

  getBlock(): Block {
    return this.getLatest().__block;
  }

  createDOM(): HTMLElement {
    return document.createElement("div");
  }

  updateDOM(): false {
    return false;
  }

  isInline(): false {
    return false;
  }

  decorate(): JSX.Element {
    return <AtomicCard nodeKey={this.__key} block={this.__block} />;
  }
}

export function $createAtomicBlockNode(block: Block): AtomicBlockNode {
  return new AtomicBlockNode(block);
}

export function $isAtomicBlockNode(node: LexicalNode | null | undefined): node is AtomicBlockNode {
  return node instanceof AtomicBlockNode;
}
