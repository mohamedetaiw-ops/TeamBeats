/**
 * Image with a fixed aspect ratio (docs/design-system/imagery.md), so the page never shifts while it loads. `alt` is
 * required: describe what the image shows for the reader, or pass `decorative` when the text around it already says
 * everything, which sets an empty alternative. Images below the fold load lazily.
 */
export type MediaRatio = "16:9" | "4:3" | "1:1" | "3:1";

type Props = { src: string; ratio?: MediaRatio; priority?: boolean; caption?: string } & (
  | { alt: string; decorative?: false }
  | { alt?: never; decorative: true }
);

export function Media({ src, ratio = "16:9", priority = false, caption, ...rest }: Props) {
  const alt = rest.decorative ? "" : rest.alt;
  const image = (
    <span className="ui-media" data-ratio={ratio}>
      {/* Content images are session-protected API files or tenant assets; the Next image optimizer cannot fetch them. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img src={src} alt={alt} loading={priority ? "eager" : "lazy"} decoding="async" fetchPriority={priority ? "high" : undefined} />
    </span>
  );
  return caption ? (
    <figure className="ui-figure">
      {image}
      <figcaption>{caption}</figcaption>
    </figure>
  ) : (
    image
  );
}
