"use client";

import Link from "next/link";
import { Icon, type IconName } from "./Icon";
import type { TileTone } from "./TileCard";

export type LaunchTile = {
  id: string;
  title: string;
  href: string;
  icon: IconName;
  tone?: TileTone;
  /** Opens another site in a new tab; the label says so under the title. */
  external?: { label: string };
  /** Called when the tile is opened, such as to record that a service was used. */
  onOpen?: () => void;
};

/**
 * Launch tiles (spec 036 DLS-039): a compact strip of things to open, each a tinted icon and a name. Two side by side
 * on a phone, one row on a desktop. A tile is the link; the strip holds no rules.
 */
export function LaunchTiles({ tiles, label }: { tiles: readonly LaunchTile[]; label?: string }) {
  return (
    <ul className="ui-launch-tiles" aria-label={label}>
      {tiles.map((tile) => {
        const body = (
          <>
            <span className="ui-icon-tile" data-tone={tile.tone ?? "accent"} aria-hidden="true">
              <Icon name={tile.external ? "external" : tile.icon} size={20} />
            </span>
            <bdi>{tile.title}</bdi>
            {tile.external ? <span className="ui-launch-tile-meta">{tile.external.label}</span> : null}
          </>
        );
        return (
          <li key={tile.id}>
            {tile.external ? (
              <a href={tile.href} target="_blank" rel="noopener noreferrer" onClick={tile.onOpen}>
                {body}
              </a>
            ) : (
              <Link href={tile.href} onClick={tile.onOpen}>
                {body}
              </Link>
            )}
          </li>
        );
      })}
    </ul>
  );
}
