import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { AlertTriangle, Loader2, Plus, Sparkles, Trash2 } from "lucide-react";
import type {
  ApplyExtractionQuestion,
  ExtractedQuestion,
  ExtractionPreview,
} from "../types/models";
import {
  Badge,
  Button,
  ConfirmDialog,
  Dialog,
  Input,
  Textarea,
} from "./ui";
import { formatScore } from "../utils/format";
import { currentLang } from "../hooks/useLang";

/**
 * Editable preview of the AI rubric extraction (POST /exams/{id}/extraction/
 * preview). Nothing reaches the database until the user confirms — Apply only
 * hands the (edited) rows back to the caller, which posts them to /apply.
 */
export function ExtractionPreviewDialog({
  open,
  onClose,
  preview,
  running,
  error,
  existingNumbers,
  applying,
  onApply,
}: {
  open: boolean;
  onClose: () => void;
  preview: ExtractionPreview | null;
  /** True while the AI call is in flight and no result has arrived yet. */
  running: boolean;
  error: string | null;
  /** Question numbers that already exist — matching rows will be UPDATED on apply. */
  existingNumbers: number[];
  applying: boolean;
  onApply: (questions: ApplyExtractionQuestion[]) => void;
}) {
  const { t } = useTranslation();
  const lang = currentLang();
  const [rows, setRows] = useState<ExtractedQuestion[]>([]);
  const [confirmOpen, setConfirmOpen] = useState(false);

  // Re-seed the editable copy whenever a new preview arrives (extraction is
  // re-runnable, so the dialog may be filled more than once per mount).
  useEffect(() => {
    setRows(
      (preview?.questions ?? []).map((q) => ({
        ...q,
        criteria: q.criteria.map((c) => ({ ...c })),
      }))
    );
  }, [preview]);

  const update = (i: number, patch: Partial<ExtractedQuestion>) =>
    setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));

  const updateCriterion = (qi: number, ci: number, patch: Partial<ExtractedQuestion["criteria"][number]>) =>
    setRows(
      rows.map((r, j) =>
        j === qi
          ? { ...r, criteria: r.criteria.map((c, k) => (k === ci ? { ...c, ...patch } : c)) }
          : r
      )
    );

  // Client-side mirror of the server's apply validation — block obviously bad
  // rows before they reach EXTRACTION_CONFLICT / validation errors.
  const issues = useMemo(() => {
    const counts = new Map<number, number>();
    for (const r of rows) counts.set(r.number, (counts.get(r.number) ?? 0) + 1);
    return rows.map((r) => {
      const sum = r.criteria.reduce((s, c) => s + (Number(c.maxScore) || 0), 0);
      return {
        badNumber: !Number.isInteger(r.number) || r.number < 1,
        duplicateNumber: (counts.get(r.number) ?? 0) > 1,
        emptyText: !r.text.trim(),
        emptyCriteria: !r.criteria.some((c) => c.description.trim()),
        sumExceeds: sum > r.maxScore + 1e-9,
        sum,
      };
    });
  }, [rows]);

  const hasIssues = issues.some(
    (x) => x.badNumber || x.duplicateNumber || x.emptyText || x.emptyCriteria || x.sumExceeds
  );

  const updateCount = rows.filter((r) => existingNumbers.includes(r.number)).length;

  function payload(): ApplyExtractionQuestion[] {
    return rows.map((r) => ({
      number: r.number,
      text: r.text.trim(),
      maxScore: r.maxScore,
      criteria: r.criteria
        .filter((c) => c.description.trim())
        .map((c) => ({
          // Empty id (manually added rows) gets a generated one server-side-safe.
          criterionId: c.criterionId.trim() || crypto.randomUUID(),
          description: c.description.trim(),
          maxScore: c.maxScore,
        })),
    }));
  }

  function requestApply() {
    if (hasIssues || applying) return;
    if (updateCount > 0) setConfirmOpen(true);
    else onApply(payload());
  }

  if (!open) return null;

  return (
    <>
      <Dialog
        open
        onClose={onClose}
        title={t("extraction.title")}
        description={t("extraction.subtitle")}
        wide
      >
        {running && !preview ? (
          <div className="flex flex-col items-center gap-3 py-10 text-sm text-zinc-500">
            <Loader2 size={26} className="animate-spin text-primary-500" />
            <p>{t("extraction.running")}</p>
          </div>
        ) : error && !preview ? (
          <div className="flex items-start gap-2 rounded-xl bg-red-50 p-3 text-sm text-red-700">
            <AlertTriangle size={16} className="mt-0.5 shrink-0" />
            <p>{error}</p>
          </div>
        ) : preview && preview.questions.length === 0 ? (
          <p className="py-8 text-center text-sm text-zinc-500">{t("extraction.emptyResult")}</p>
        ) : (
          <>
            {preview && preview.warnings.length > 0 ? (
              <div className="mb-3 rounded-xl bg-amber-50 p-3 text-xs text-amber-800">
                <p className="mb-1 flex items-center gap-1.5 font-semibold">
                  <AlertTriangle size={13} /> {t("extraction.warnings")}
                </p>
                <ul className="list-disc space-y-0.5 pe-4 ltr-token">
                  {preview.warnings.map((w, i) => (
                    <li key={i}>{w}</li>
                  ))}
                </ul>
              </div>
            ) : null}

            {hasIssues ? (
              <p className="mb-3 rounded-xl bg-red-50 p-2.5 text-xs font-medium text-red-700">
                {t("extraction.invalidRow")}
              </p>
            ) : null}

            <div className="max-h-[55vh] space-y-3 overflow-y-auto pe-1">
              {rows.map((r, i) => {
                const iss = issues[i];
                return (
                  <div
                    key={i}
                    className={
                      "rounded-xl border p-3 " +
                      (iss.badNumber || iss.duplicateNumber || iss.emptyText || iss.emptyCriteria || iss.sumExceeds
                        ? "border-red-300 bg-red-50/40"
                        : "border-zinc-200")
                    }
                  >
                    <div className="flex items-start gap-2">
                      <div className="w-16 shrink-0">
                        <Input
                          type="number"
                          min={1}
                          value={r.number}
                          aria-label={t("questions.number")}
                          error={iss.badNumber || iss.duplicateNumber}
                          title={iss.duplicateNumber ? t("extraction.duplicateNumber") : undefined}
                          onChange={(e) => update(i, { number: Number(e.target.value) })}
                        />
                      </div>
                      <div className="flex-1">
                        <Textarea
                          rows={2}
                          value={r.text}
                          placeholder={t("questions.text")}
                          error={iss.emptyText}
                          onChange={(e) => update(i, { text: e.target.value })}
                        />
                      </div>
                      <div className="w-20 shrink-0">
                        <Input
                          type="number"
                          min={0.5}
                          step={0.5}
                          value={r.maxScore}
                          aria-label={t("questions.maxScore")}
                          onChange={(e) => update(i, { maxScore: Number(e.target.value) })}
                        />
                      </div>
                      <button
                        type="button"
                        onClick={() => setRows(rows.filter((_, j) => j !== i))}
                        className="mt-2 rounded-lg p-1.5 text-zinc-400 transition hover:bg-red-50 hover:text-red-600"
                        aria-label={t("extraction.removeQuestion")}
                        title={t("extraction.removeQuestion")}
                      >
                        <Trash2 size={15} />
                      </button>
                    </div>

                    <div className="mt-2 space-y-1.5 border-t border-zinc-100 pt-2">
                      {r.criteria.map((c, ci) => (
                        <div key={ci} className="flex items-center gap-1.5">
                          <div className="w-20 shrink-0">
                            <Input
                              value={c.criterionId}
                              placeholder={t("rubric.idPlaceholder")}
                              aria-label={t("rubric.criterionId")}
                              className="ltr-token"
                              onChange={(e) => updateCriterion(i, ci, { criterionId: e.target.value })}
                            />
                          </div>
                          <div className="flex-1">
                            <Input
                              value={c.description}
                              placeholder={t("rubric.criterionDescription")}
                              onChange={(e) => updateCriterion(i, ci, { description: e.target.value })}
                            />
                          </div>
                          <div className="w-20 shrink-0">
                            <Input
                              type="number"
                              min={0}
                              step={0.5}
                              value={c.maxScore}
                              aria-label={t("questions.maxScore")}
                              onChange={(e) => updateCriterion(i, ci, { maxScore: Number(e.target.value) })}
                            />
                          </div>
                          <button
                            type="button"
                            onClick={() =>
                              update(i, { criteria: r.criteria.filter((_, k) => k !== ci) })
                            }
                            className="rounded-lg p-1.5 text-zinc-400 transition hover:bg-red-50 hover:text-red-600"
                            aria-label={t("rubric.removeCriterion")}
                          >
                            <Trash2 size={13} />
                          </button>
                        </div>
                      ))}
                      <div className="flex items-center justify-between gap-2">
                        <Button
                          variant="secondary"
                          size="sm"
                          onClick={() =>
                            update(i, {
                              criteria: [
                                ...r.criteria,
                                { criterionId: "", description: "", maxScore: 1 },
                              ],
                            })
                          }
                        >
                          <Plus size={13} /> {t("rubric.addCriterion")}
                        </Button>
                        <span
                          className={
                            "text-xs tabular-nums " +
                            (iss.sumExceeds ? "font-semibold text-red-600" : "text-zinc-400")
                          }
                          title={iss.sumExceeds ? t("extraction.sumExceedsMax", { sum: iss.sum, max: r.maxScore }) : undefined}
                        >
                          {t("rubric.totalMaxScore")}: {formatScore(iss.sum, lang)} /{" "}
                          {formatScore(r.maxScore, lang)}
                        </span>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>

            {preview ? (
              <p className="mt-3 flex flex-wrap items-center gap-2 text-[11px] text-zinc-400 ltr-token">
                <Badge tone="zinc">
                  <Sparkles size={10} /> {preview.modelName}
                </Badge>
                <span>
                  in {preview.inputTokens} · out {preview.outputTokens} tokens
                </span>
                <span>· {preview.latencyMs} ms</span>
              </p>
            ) : null}
          </>
        )}

        <div className="mt-5 flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose} disabled={applying}>
            {t("common.cancel")}
          </Button>
          <Button onClick={requestApply} loading={applying} disabled={running || hasIssues}>
            {t("extraction.apply")}
          </Button>
        </div>
      </Dialog>

      {/* Apply overwrites existing questions matched by number — confirm first. */}
      <ConfirmDialog
        open={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        onConfirm={() => {
          setConfirmOpen(false);
          onApply(payload());
        }}
        title={t("extraction.confirmOverwriteTitle")}
        message={t("extraction.confirmOverwrite", { updates: updateCount, creates: rows.length - updateCount })}
        loading={applying}
      />
    </>
  );
}
