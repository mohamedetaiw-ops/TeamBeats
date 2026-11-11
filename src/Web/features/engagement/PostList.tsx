"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { StateView } from "@/components/ui/StateView";
import type { PostView } from "@/lib/engagement";
import { PostCard } from "./PostCard";
import "./engagement.css";

/**
 * Posts from more than one community, such as a hashtag page (spec 023 US1 scenario 3), or one post opened from a
 * notification or search. The API sends only posts this viewer can read, with what they may do on each.
 */
export function PostList({ initial, communityNames, openComments = false }: { initial: PostView[]; communityNames: Record<string, string>; openComments?: boolean }) {
  const t = useTranslations("engagement");
  const [posts, setPosts] = useState(initial);
  if (posts.length === 0) return <StateView kind="empty" title={t("hashtag.emptyTitle")} message={t("hashtag.empty")} icon="search" />;
  return (
    <ul className="engagement-feed" aria-label={t("feed.label")}>
      {posts.map((p) => (
        <li key={p.id}>
          <PostCard
            initial={p}
            canComment={p.canComment}
            showCommunity
            communityName={communityNames[p.communityId]}
            openComments={openComments}
            onRemoved={(id) => setPosts((all) => all.filter((x) => x.id !== id))}
          />
        </li>
      ))}
    </ul>
  );
}
