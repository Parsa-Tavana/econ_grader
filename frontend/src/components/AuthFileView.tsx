import { useEffect, useState } from "react";
import { api } from "../api/client";

interface AuthFileViewProps {
  /** Authenticated endpoint that streams the file, e.g. /answers/<id>/image */
  path: string;
  /** MIME type of the stored file — picks <img> vs <iframe> rendering. */
  contentType: string | null;
  alt: string;
  className?: string;
}

/**
 * Renders a stored answer/question/rubric file by fetching it WITH the JWT
 * (axios interceptor) and displaying the blob. A bare <img src> or
 * <iframe src> cannot attach the Authorization header, so those requests
 * hit the API unauthenticated and get 401 — the file pane then shows nothing.
 * This component is the fix: same endpoint, authenticated fetch.
 */
export function AuthFileView({ path, contentType, alt, className }: AuthFileViewProps) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let objectUrl: string | null = null;
    let cancelled = false;
    setUrl(null);
    setFailed(false);
    api
      .get(path, { responseType: "blob" })
      .then((resp) => {
        if (cancelled) return;
        objectUrl = URL.createObjectURL(resp.data as Blob);
        setUrl(objectUrl);
      })
      .catch(() => {
        if (!cancelled) setFailed(true);
      });
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [path]);

  if (failed)
    return (
      <div className="flex h-40 items-center justify-center text-sm text-zinc-500">
        {alt} — ⚠
      </div>
    );
  if (!url) return <div className="flex h-40 items-center justify-center text-sm text-zinc-400">…</div>;

  const isPdf = (contentType ?? "").includes("pdf");
  return isPdf ? (
    <iframe src={url} title={alt} className={className} />
  ) : (
    <img src={url} alt={alt} className={className} loading="lazy" />
  );
}
