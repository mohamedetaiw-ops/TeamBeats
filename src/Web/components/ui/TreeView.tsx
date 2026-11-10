"use client";

import { useState, type KeyboardEvent } from "react";
import { Icon, type IconName } from "./Icon";
import { nextByCharacter } from "./overlay";

export type TreeNode = { key: string; label: string; icon?: IconName; children?: readonly TreeNode[] };

/**
 * Tree view (spec 036 DLS-036): a hierarchy such as folders, an organization or a page tree. It follows the tree
 * pattern: one item is in the tab order; Up and Down move through the visible items; the forward arrow opens a branch
 * or goes to its first child and the back arrow closes it or goes to its parent, following the reading direction;
 * Home and End jump; a letter jumps to the next item that starts with it; Enter or Space selects.
 */
export function TreeView({
  label,
  nodes,
  selected,
  onSelect,
  defaultExpanded = [],
}: {
  label: string;
  nodes: readonly TreeNode[];
  selected: string | null;
  onSelect: (key: string) => void;
  defaultExpanded?: readonly string[];
}) {
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set(defaultExpanded));
  const [cursor, setCursor] = useState<string | null>(null);
  const tabbable = cursor ?? selected ?? nodes[0]?.key ?? null;

  const toggle = (key: string, open: boolean) =>
    setExpanded((before) => {
      const next = new Set(before);
      if (open) next.add(key);
      else next.delete(key);
      return next;
    });

  function onKeyDown(event: KeyboardEvent<HTMLUListElement>) {
    const tree = event.currentTarget;
    const item = (event.target as HTMLElement).closest<HTMLElement>('[role="treeitem"]');
    if (!item) return;
    const key = item.dataset.key!;
    const visible = [...tree.querySelectorAll<HTMLElement>('[role="treeitem"]')].filter((el) => !el.closest('[role="group"][hidden]'));
    const index = visible.indexOf(item);
    const rtl = getComputedStyle(tree).direction === "rtl";
    const forward = rtl ? "ArrowLeft" : "ArrowRight";
    const back = rtl ? "ArrowRight" : "ArrowLeft";
    const branch = item.hasAttribute("aria-expanded");
    const open = item.getAttribute("aria-expanded") === "true";
    let target: HTMLElement | null | undefined;

    if (event.key === "ArrowDown") target = visible[index + 1];
    else if (event.key === "ArrowUp") target = visible[index - 1];
    else if (event.key === "Home") target = visible[0];
    else if (event.key === "End") target = visible[visible.length - 1];
    else if (event.key === forward && branch) {
      if (open) target = visible[index + 1];
      else toggle(key, true);
    } else if (event.key === back) {
      if (branch && open) toggle(key, false);
      else target = item.parentElement?.closest<HTMLElement>('[role="treeitem"]');
    } else if (event.key === "Enter" || event.key === " ") {
      onSelect(key);
    } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
      const labels = visible.map((el) => el.querySelector<HTMLElement>(".ui-tree-label")!);
      target = nextByCharacter(labels, index, event.key)?.closest<HTMLElement>('[role="treeitem"]');
    } else {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    if (target) {
      setCursor(target.dataset.key!);
      target.focus();
    }
  }

  const render = (list: readonly TreeNode[], level: number) =>
    list.map((node) => {
      const branch = !!node.children?.length;
      const open = expanded.has(node.key);
      return (
        <li
          key={node.key}
          role="treeitem"
          data-key={node.key}
          aria-level={level}
          aria-expanded={branch ? open : undefined}
          aria-selected={node.key === selected}
          tabIndex={node.key === tabbable ? 0 : -1}
          className="ui-tree-item"
        >
          {/* The row is the pointer target; the keys are handled on the tree, where the item itself has focus. */}
          <span
            className="ui-tree-row"
            onClick={() => {
              setCursor(node.key);
              if (branch) toggle(node.key, !open);
              onSelect(node.key);
            }}
          >
            <span className="ui-tree-twist" aria-hidden="true">
              {branch ? <Icon name={open ? "chevron-down" : "chevron-forward"} size={16} /> : null}
            </span>
            {node.icon ? <Icon name={node.icon} size={16} /> : null}
            <span className="ui-tree-label">{node.label}</span>
          </span>
          {branch ? (
            <ul role="group" hidden={!open}>
              {render(node.children!, level + 1)}
            </ul>
          ) : null}
        </li>
      );
    });

  return (
    <ul role="tree" aria-label={label} className="ui-tree" onKeyDown={onKeyDown}>
      {render(nodes, 1)}
    </ul>
  );
}
