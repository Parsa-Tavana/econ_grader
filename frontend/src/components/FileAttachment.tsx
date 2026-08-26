import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { clsx } from "clsx";
import { FileText, Image as ImageIcon, Link2, Trash2, Upload, Download, Loader2 } from "lucide-react";
import { Button } from "./ui/Button";
import { friendlyError } from "./ui/Feedback";
import { useToast } from "../hooks/useToast";

export const ACCEPTED_TYPES = ".pdf,.png,.jpg,.jpeg,.docx,.xlsx,.xls";

interface FileAttachmentProps {
  /** Which role this attachment plays — shown to the user. */
  label: string;
  /** Current file name if one is stored (null = none). */
  fileName: string | null;
  contentType: string | null;
  /** URL that streams/downloads the stored file. */
  fileUrl: string | null;
  onUpload: (file: File) => Promise<void>;
  onDelete: () => Promise<void>;
  /**
   * When false, upload/replace/delete controls are hidden (read-only link
   * only) — mirrors the backend's Teacher-only file mutation endpoints.
   * Defaults to true.
   */
  canEdit?: boolean;
}

/** Inline icon for the stored file type. */
function TypeIcon({ contentType }: { contentType: string | null }) {
  if (!contentType) return <Link2 size={14} className="text-zinc-400" />;
  if (contentType.startsWith("image/")) return <ImageIcon size={14} className="text-sky-500" />;
  return <FileText size={14} className="text-red-400" />;
}

/**
 * Reusable single-file attachment control used for Question paper,
 * Rubric document and Student Answer uploads.
 * Handles upload (with progress state), replace, remove, preview/download,
 * client-side type validation and translated error toasts.
 */
export function FileAttachment({
  label,
  fileName,
  contentType,
  fileUrl,
  onUpload,
  onDelete,
  canEdit = true,
}: FileAttachmentProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const inputRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);

  function validate(file: File): boolean {
    // Browsers report varying MIME types for Office files (some use
    // application/octet-stream), so fall back to the extension.
    const okTypes = [
      "application/pdf",
      "image/png",
      "image/jpeg",
      "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      "application/vnd.ms-excel",
    ];
    const okExts = [".pdf", ".png", ".jpg", ".jpeg", ".docx", ".xlsx", ".xls"];
    const ext = file.name.slice(file.name.lastIndexOf(".")).toLowerCase();
    if (!okTypes.includes(file.type) && !okExts.includes(ext)) {
      toast.error(t("files.unsupportedType", { type: file.type || file.name }));
      return false;
    }
    if (file.size > 20 * 1024 * 1024) {
      toast.error(t("files.tooLarge"));
      return false;
    }
    return true;
  }

  async function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    if (!validate(file)) return;
    setBusy(true);
    try {
      await onUpload(file);
      toast.success(t("files.uploaded"));
    } catch (err) {
      toast.error(friendlyError(err, t));
    } finally {
      setBusy(false);
    }
  }

  async function handleDelete() {
    setBusy(true);
    try {
      await onDelete();
      toast.success(t("files.removed"));
    } catch (err) {
      toast.error(friendlyError(err, t));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div
      className={clsx(
        "rounded-xl border p-3 transition",
        busy ? "border-zinc-200 bg-zinc-50 opacity-70" : "border-zinc-200 bg-white"
      )}
    >
      <div className="flex items-center justify-between gap-2">
        <span className="text-xs font-semibold uppercase tracking-wide text-zinc-500">
          {label}
        </span>
        {busy ? <Loader2 size={14} className="animate-spin text-primary-500" /> : null}
      </div>

      {fileName ? (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <TypeIcon contentType={contentType} />
          <a
            href={fileUrl ?? "#"}
            target="_blank"
            rel="noreferrer"
            className="min-w-0 max-w-[220px] truncate text-sm font-medium text-primary-700 hover:underline"
            title={fileName}
          >
            {fileName}
          </a>
          <span className="flex-1" />
          <a href={fileUrl ?? "#"} download title={t("common.download")}>
            <Button size="sm" variant="ghost" aria-label={t("common.download")}>
              <Download size={13} />
            </Button>
          </a>
          {canEdit ? (
            <>
              <Button size="sm" variant="ghost" onClick={() => inputRef.current?.click()} disabled={busy}>
                <Upload size={13} /> {t("files.replace")}
              </Button>
              <Button size="sm" variant="ghost" className="text-red-600 hover:bg-red-50" onClick={handleDelete} disabled={busy}>
                <Trash2 size={13} />
              </Button>
            </>
          ) : null}
        </div>
      ) : canEdit ? (
        <label
          className={clsx(
            "mt-2 inline-flex cursor-pointer items-center gap-1.5 rounded-xl border border-dashed border-zinc-300 px-3 py-2 text-xs font-medium text-zinc-600 transition hover:border-primary-300 hover:text-primary-700",
            busy && "cursor-wait"
          )}
        >
          <Upload size={13} />
          {t("files.upload")}
          <input
            ref={inputRef}
            type="file"
            accept={ACCEPTED_TYPES}
            className="hidden"
            onChange={handleFile}
            disabled={busy}
          />
        </label>
      ) : null}

      {/* Hidden input kept mounted so Replace works without re-render tricks */}
      {fileName ? (
        <input
          ref={inputRef}
          type="file"
          accept={ACCEPTED_TYPES}
          className="hidden"
          onChange={handleFile}
          disabled={busy}
        />
      ) : null}
    </div>
  );
}