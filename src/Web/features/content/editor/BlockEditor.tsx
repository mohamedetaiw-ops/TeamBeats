"use client";

import { useCallback, useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { LexicalComposer } from "@lexical/react/LexicalComposer";
import { useLexicalComposerContext } from "@lexical/react/LexicalComposerContext";
import { ContentEditable } from "@lexical/react/LexicalContentEditable";
import { LexicalErrorBoundary } from "@lexical/react/LexicalErrorBoundary";
import { HistoryPlugin } from "@lexical/react/LexicalHistoryPlugin";
import { LinkPlugin } from "@lexical/react/LexicalLinkPlugin";
import { ListPlugin } from "@lexical/react/LexicalListPlugin";
import { OnChangePlugin } from "@lexical/react/LexicalOnChangePlugin";
import { RichTextPlugin } from "@lexical/react/LexicalRichTextPlugin";
import { $isLinkNode, LinkNode, TOGGLE_LINK_COMMAND } from "@lexical/link";
import { $isListNode, INSERT_ORDERED_LIST_COMMAND, INSERT_UNORDERED_LIST_COMMAND, ListItemNode, ListNode, REMOVE_LIST_COMMAND } from "@lexical/list";
import { $createHeadingNode, $createQuoteNode, $isHeadingNode, $isQuoteNode, HeadingNode, QuoteNode } from "@lexical/rich-text";
import { $setBlocksType } from "@lexical/selection";
import { $findMatchingParent, $insertNodeToNearestRoot, mergeRegister } from "@lexical/utils";
import {
  $createParagraphNode,
  $getSelection,
  $isRangeSelection,
  $isRootOrShadowRoot,
  CAN_REDO_COMMAND,
  CAN_UNDO_COMMAND,
  COMMAND_PRIORITY_LOW,
  FORMAT_TEXT_COMMAND,
  REDO_COMMAND,
  SELECTION_CHANGE_COMMAND,
  UNDO_COMMAND,
  type EditorState,
  type LexicalNode,
} from "lexical";
import type { BodyDocument } from "@/lib/content/types";
import { assetThumbnailUrl, assetsApi, type AssetPick } from "@/lib/assets/types";
import { nameIn } from "@/lib/documents/types";
import { $createAtomicBlockNode, AtomicBlockNode } from "./AtomicBlockNode";
import { $loadBody, $readBody } from "./convert";
import { Button } from "@/components/ui/Button";
import "../content.css";
import "./editor.css";

type BlockKind = "paragraph" | "h2" | "h3" | "quote" | "bullet" | "number";

/** Links the body document accepts; the API checks the same list. */
const linkable = (url: string) => /^(https?:\/\/|mailto:)/i.test(url);

/**
 * Picks a current image from the brand asset portal (spec 011 US5): the API offers only images the author reads whose
 * usage rights have not ended, and checks the choice again when the page is saved.
 */
function ImagePicker({ onPick, onCancel }: { onPick: (id: string, alt: string) => void; onCancel: () => void }) {
  const t = useTranslations("content.editor");
  const locale = useLocale();
  const searchId = useId();
  const altId = useId();
  const [query, setQuery] = useState("");
  const [picks, setPicks] = useState<AssetPick[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [chosen, setChosen] = useState<AssetPick | null>(null);
  const [alt, setAlt] = useState("");

  useEffect(() => {
    let live = true;
    const timer = setTimeout(async () => {
      try {
        const response = await fetch(`${assetsApi}/picker${query.trim() ? `?q=${encodeURIComponent(query.trim())}` : ""}`, { headers: { accept: "application/json" } });
        if (!live) return;
        if (!response.ok) {
          setFailed(true);
          setPicks([]);
          return;
        }
        setFailed(false);
        setPicks((await response.json()) as AssetPick[]);
      } catch {
        if (live) setFailed(true);
      }
    }, 250);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [query]);

  return (
    <div className="content-editor-ask" role="group" aria-label={t("imageTitle")}>
      <label htmlFor={searchId}>{t("imageSearch")}</label>
      <input id={searchId} type="search" dir="auto" value={query} autoFocus onChange={(e) => setQuery(e.target.value)} />
      {failed ? (
        <p className="field-error" role="alert">
          {t("imageUnavailable")}
        </p>
      ) : picks && picks.length === 0 ? (
        <p className="content-meta">{t("imageNone")}</p>
      ) : (
        <ul className="content-editor-picks" aria-label={t("imageChoices")}>
          {(picks ?? []).map((p) => {
            const name = nameIn(p.names, locale);
            return (
              <li key={p.id}>
                <button
                  type="button"
                  className="content-editor-pick"
                  aria-pressed={chosen?.id === p.id}
                  onClick={() => {
                    setChosen(p);
                    if (!alt) setAlt(name);
                  }}
                >
                  {/* eslint-disable-next-line @next/next/no-img-element -- thumbnails come from the authenticated API */}
                  <img src={assetThumbnailUrl(p.id)} alt="" />
                  <bdi>{name}</bdi>
                  <span className="content-meta">
                    <bdi>{nameIn(p.collectionNames, locale)}</bdi>
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
      <label htmlFor={altId}>{t("imageAlt")}</label>
      <input id={altId} type="text" dir="auto" value={alt} maxLength={300} onChange={(e) => setAlt(e.target.value)} />
      <p className="content-meta">{t("imageAltHint")}</p>
      <div className="content-actions">
        <Button variant="primary" disabled={!chosen || !alt.trim()} onClick={() => chosen && onPick(chosen.id, alt.trim())}>
          {t("insert")}
        </Button>
        <Button variant="quiet" onClick={onCancel}>
          {t("cancel")}
        </Button>
      </div>
    </div>
  );
}

function Toolbar({ label }: { label: string }) {
  const t = useTranslations("content.editor");
  const [editor] = useLexicalComposerContext();
  const [bold, setBold] = useState(false);
  const [italic, setItalic] = useState(false);
  const [link, setLink] = useState(false);
  const [kind, setKind] = useState<BlockKind>("paragraph");
  const [canUndo, setCanUndo] = useState(false);
  const [canRedo, setCanRedo] = useState(false);
  const [asking, setAsking] = useState<"link" | "embed" | "image" | null>(null);
  const [url, setUrl] = useState("");
  const [urlError, setUrlError] = useState(false);
  const urlId = useId();

  const refresh = useCallback(() => {
    const selection = $getSelection();
    if (!$isRangeSelection(selection)) return;
    setBold(selection.hasFormat("bold"));
    setItalic(selection.hasFormat("italic"));
    const anchor = selection.anchor.getNode();
    setLink($isLinkNode(anchor) || $isLinkNode(anchor.getParent()));
    const top = anchor.getKey() === "root" ? anchor : $findMatchingParent(anchor, (n: LexicalNode) => !!n.getParent() && $isRootOrShadowRoot(n.getParent()));
    if ($isListNode(top)) setKind(top.getListType() === "number" ? "number" : "bullet");
    else if ($isHeadingNode(top)) setKind(top.getTag() === "h3" ? "h3" : "h2");
    else if ($isQuoteNode(top)) setKind("quote");
    else setKind("paragraph");
  }, []);

  useEffect(
    () =>
      mergeRegister(
        editor.registerUpdateListener(({ editorState }) => editorState.read(refresh)),
        editor.registerCommand(
          SELECTION_CHANGE_COMMAND,
          () => {
            refresh();
            return false;
          },
          COMMAND_PRIORITY_LOW,
        ),
        editor.registerCommand(
          CAN_UNDO_COMMAND,
          (v) => {
            setCanUndo(v);
            return false;
          },
          COMMAND_PRIORITY_LOW,
        ),
        editor.registerCommand(
          CAN_REDO_COMMAND,
          (v) => {
            setCanRedo(v);
            return false;
          },
          COMMAND_PRIORITY_LOW,
        ),
      ),
    [editor, refresh],
  );

  const setBlock = (next: BlockKind) => {
    if (next === "bullet") return editor.dispatchCommand(kind === "bullet" ? REMOVE_LIST_COMMAND : INSERT_UNORDERED_LIST_COMMAND, undefined);
    if (next === "number") return editor.dispatchCommand(kind === "number" ? REMOVE_LIST_COMMAND : INSERT_ORDERED_LIST_COMMAND, undefined);
    if (kind === "bullet" || kind === "number") editor.dispatchCommand(REMOVE_LIST_COMMAND, undefined);
    editor.update(() => {
      const selection = $getSelection();
      if (!$isRangeSelection(selection)) return;
      $setBlocksType(selection, () => (next === "h2" || next === "h3" ? $createHeadingNode(next) : next === "quote" ? $createQuoteNode() : $createParagraphNode()));
    });
  };

  const confirmUrl = () => {
    const value = url.trim();
    if (!linkable(value)) {
      setUrlError(true);
      return;
    }
    if (asking === "link") {
      editor.dispatchCommand(TOGGLE_LINK_COMMAND, value);
    } else {
      editor.update(() => {
        $insertNodeToNearestRoot($createAtomicBlockNode({ type: "embed", url: value }));
      });
    }
    setAsking(null);
    setUrl("");
    setUrlError(false);
  };

  return (
    <>
      <div className="content-editor-toolbar" role="toolbar" aria-label={t("toolbar", { label })}>
        <label className="content-editor-select">
          <span className="visually-hidden">{t("blockType")}</span>
          <select value={kind === "bullet" || kind === "number" ? "paragraph" : kind} onChange={(e) => setBlock(e.target.value as BlockKind)}>
            <option value="paragraph">{t("paragraph")}</option>
            <option value="h2">{t("heading2")}</option>
            <option value="h3">{t("heading3")}</option>
            <option value="quote">{t("quote")}</option>
          </select>
        </label>
        <Button variant="quiet" aria-pressed={bold} onClick={() => editor.dispatchCommand(FORMAT_TEXT_COMMAND, "bold")}>
          {t("bold")}
        </Button>
        <Button variant="quiet" aria-pressed={italic} onClick={() => editor.dispatchCommand(FORMAT_TEXT_COMMAND, "italic")}>
          {t("italic")}
        </Button>
        <Button variant="quiet" aria-pressed={kind === "bullet"} onClick={() => setBlock("bullet")}>
          {t("bulleted")}
        </Button>
        <Button variant="quiet" aria-pressed={kind === "number"} onClick={() => setBlock("number")}>
          {t("numbered")}
        </Button>
        <Button variant="quiet"
          aria-pressed={link}
          onClick={() => (link ? editor.dispatchCommand(TOGGLE_LINK_COMMAND, null) : setAsking("link"))}
        >
          {link ? t("unlink") : t("link")}
        </Button>
        <Button variant="quiet" onClick={() => setAsking("embed")}>
          {t("embed")}
        </Button>
        <Button variant="quiet" onClick={() => setAsking("image")}>
          {t("image")}
        </Button>
        <Button variant="quiet" disabled={!canUndo} onClick={() => editor.dispatchCommand(UNDO_COMMAND, undefined)}>
          {t("undo")}
        </Button>
        <Button variant="quiet" disabled={!canRedo} onClick={() => editor.dispatchCommand(REDO_COMMAND, undefined)}>
          {t("redo")}
        </Button>
      </div>
      {asking === "image" ? (
        <ImagePicker
          onCancel={() => setAsking(null)}
          onPick={(assetId, alt) => {
            editor.update(() => {
              $insertNodeToNearestRoot($createAtomicBlockNode({ type: "image", assetId, alt }));
            });
            setAsking(null);
          }}
        />
      ) : asking ? (
        <div className="content-editor-ask">
          <label htmlFor={urlId}>{asking === "link" ? t("linkAddress") : t("embedAddress")}</label>
          <input
            id={urlId}
            type="url"
            dir="ltr"
            value={url}
            autoFocus
            aria-invalid={urlError || undefined}
            aria-describedby={urlError ? `${urlId}-error` : undefined}
            onChange={(e) => setUrl(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                confirmUrl();
              }
              if (e.key === "Escape") setAsking(null);
            }}
          />
          {urlError ? (
            <p id={`${urlId}-error`} className="field-error" role="alert">
              {t("badAddress")}
            </p>
          ) : null}
          <div className="content-actions">
            <Button variant="primary" onClick={confirmUrl}>
              {t("insert")}
            </Button>
            <Button variant="quiet" onClick={() => setAsking(null)}>
              {t("cancel")}
            </Button>
          </div>
          {asking === "embed" ? <p className="content-meta">{t("embedHint")}</p> : null}
        </div>
      ) : null}
    </>
  );
}

/**
 * The block editor (spec 009 T022): paragraphs, headings, quotes, lists, bold, italic and links, with embeds and other
 * blocks kept as cards. It reads and writes the body document, never HTML, so what is saved is what the API validates.
 * Lexical sets each block's direction from its first strong character, so Arabic and English mix within one body, and
 * the toolbar follows the locale of the variant being written.
 */
export function BlockEditor({
  label,
  value,
  onChange,
  locale,
  describedBy,
}: {
  label: string;
  value: BodyDocument;
  onChange: (body: BodyDocument) => void;
  locale: string;
  describedBy?: string;
}) {
  const t = useTranslations("content.editor");
  const dir = locale === "ar" ? "rtl" : "ltr";
  const config = {
    namespace: "teambeats-content",
    nodes: [HeadingNode, QuoteNode, ListNode, ListItemNode, LinkNode, AtomicBlockNode],
    editorState: () => $loadBody(value),
    onError: (error: Error) => {
      throw error;
    },
    theme: {
      paragraph: "content-editor-p",
      quote: "content-editor-quote",
      heading: { h2: "content-editor-h2", h3: "content-editor-h3", h4: "content-editor-h3" },
      list: { ul: "content-editor-ul", ol: "content-editor-ol", listitem: "content-editor-li" },
      text: { bold: "content-editor-bold", italic: "content-editor-italic" },
      link: "content-editor-link",
    },
  };
  return (
    <div className="content-editor" dir={dir} lang={locale}>
      <LexicalComposer initialConfig={config}>
        <Toolbar label={label} />
        <div className="content-editor-surface">
          <RichTextPlugin
            contentEditable={
              <ContentEditable
                className="content-editor-input"
                aria-label={label}
                aria-multiline
                aria-describedby={describedBy}
                aria-placeholder={t("placeholder")}
                placeholder={<div className="content-editor-placeholder">{t("placeholder")}</div>}
              />
            }
            ErrorBoundary={LexicalErrorBoundary}
          />
        </div>
        <HistoryPlugin />
        <ListPlugin />
        <LinkPlugin validateUrl={linkable} />
        <OnChangePlugin ignoreSelectionChange onChange={(state: EditorState) => state.read(() => onChange($readBody()))} />
      </LexicalComposer>
    </div>
  );
}
