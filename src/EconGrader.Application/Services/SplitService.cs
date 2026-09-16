using System.Data;
using EconGrader.Application.Data;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EconGrader.Application.Services;

/// <summary>Result of one bulk-split job execution.</summary>
public sealed record SplitResult(bool Completed, string? Error);

/// <summary>Config for the M6 bulk answer-sheet split (bound from Split:* in
/// Program.cs; HeaderBandPct mirrors the Python side's SPLIT_HEADER_BAND_PCT).</summary>
public sealed class SplitOptions
{
    /// <summary>Lease duration for claimed split jobs (seconds) — renewed as
    /// progress is made (per ingest window / per OCR chunk).</summary>
    public int LeaseSeconds { get; set; } = 900;

    /// <summary>Top % of each page treated as the header band.</summary>
    public decimal HeaderBandPct { get; set; } = 12m;

    /// <summary>OCR reads at/above this confidence may be auto-assigned;
    /// below it the page is flagged needs_review. NEVER auto-assign below
    /// the threshold (locked safety rule from the plan).</summary>
    public decimal OcrConfidenceThreshold { get; set; } = 0.8m;

    /// <summary>Max pages per merged upload (Split:MaxPages, plan default 400).</summary>
    public int MaxPages { get; set; } = 400;

    /// <summary>Pages ingested per Python call — slices a big merged PDF so
    /// no single response carries ~1 GB of base64.</summary>
    public int IngestWindowSize { get; set; } = 20;

    /// <summary>Pages OCR'd per /split-header call.</summary>
    public int OcrChunkSize { get; set; } = 100;

    /// <summary>Optional vision fallback for residual unmatched pages. OFF —
    /// M6 ships OCR-only (no AI tokens); a vision prompt is future work.</summary>
    public bool VisionFallbackEnabled { get; set; } = false;
}

/// <summary>
/// M6 bulk answer-sheet split (.NET side): ingest the merged upload in
/// windows, OCR every page's header band via Python /split-header (pure
/// tesseract, zero AI tokens), group consecutive same-ID pages into
/// per-student stacks, and (on confirm) apply the mapping as real Answer
/// rows with the EXACT single-upload semantics.
///
/// The batch row IS its own queue job: Status="splitting" + lease = claimable
/// (same pattern as IngestJob). Idempotency: unique (QuestionId,
/// SourceArtifactId) anchors re-upload → same batch (no re-OCR); unique
/// (BatchId, PageNumber) anchors mapping rows against lease-expiry re-runs.
///
/// SAFETY RULES (locked in the plan): reads below the confidence threshold
/// are NEVER auto-assigned; unknown OCR ids are NEVER auto-turned into
/// Students; an id appearing in two non-consecutive runs is flagged
/// needs_review — the teacher resolves everything in the review UI.
/// </summary>
public interface ISplitService
{
    /// <summary>Find or create the batch for (questionId, sourceArtifactId).
    /// Idempotent: an existing batch for the same pair is returned as-is.</summary>
    Task<BulkAnswerBatch> EnqueueAsync(Guid questionId, Guid sourceArtifactId,
        string sourceFileName, string sourceContentType, Guid userId, CancellationToken ct = default);

    /// <summary>Claim the next splitting batch (lease-based).</summary>
    Task<BulkAnswerBatch?> ClaimNextAsync(CancellationToken ct = default);

    /// <summary>Execute a claimed batch: ingest windows → OCR → grouping →
    /// ready_for_review. Called by the worker loop.</summary>
    Task<SplitResult> ExecuteAsync(BulkAnswerBatch batch, string leaseToken, CancellationToken ct = default);

    /// <summary>Run the worker loop until cancelled (AppRole=worker).</summary>
    Task RunWorkerLoopAsync(CancellationToken ct);

    /// <summary>Apply the reviewed mapping: create/replace Answer rows per
    /// student with single-upload semantics. Only auto/confirmed pages apply.</summary>
    Task<BulkApplySummary> ApplyAsync(Guid batchId, Guid userId, CancellationToken ct = default);
}

public sealed record BulkApplySummary(
    int Students, int PagesApplied, int PagesSkipped, int PagesUnassigned);

public sealed class SplitService : ISplitService
{
    // Concrete AppDbContext (not IAppDbContext): lease ExecuteUpdate with
    // token guards + the retrying execution strategy need Database (same
    // tradeoff as GradingJobService/ArtifactStore).
    private readonly AppDbContext _db;
    private readonly IGradingClient _gradingClient;
    private readonly IArtifactStore _artifacts;
    private readonly IAuditLogger _audit;
    private readonly IOptions<SplitOptions> _options;
    private readonly IOptions<IngestOptions> _ingestOptions;
    private readonly ILogger<SplitService> _logger;

    public SplitService(
        AppDbContext db,
        IGradingClient gradingClient,
        IArtifactStore artifacts,
        IAuditLogger audit,
        IOptions<SplitOptions> options,
        IOptions<IngestOptions> ingestOptions,
        ILogger<SplitService> logger)
    {
        _db = db;
        _gradingClient = gradingClient;
        _artifacts = artifacts;
        _audit = audit;
        _options = options;
        _ingestOptions = ingestOptions;
        _logger = logger;
    }

    // ── Enqueue ─────────────────────────────────────────────────────────────

    public async Task<BulkAnswerBatch> EnqueueAsync(Guid questionId, Guid sourceArtifactId,
        string sourceFileName, string sourceContentType, Guid userId, CancellationToken ct = default)
    {
        var existing = await _db.BulkAnswerBatches
            .FirstOrDefaultAsync(b => b.QuestionId == questionId && b.SourceArtifactId == sourceArtifactId, ct);
        if (existing is not null)
        {
            // applied batches stay applied — re-uploading the same merged
            // file returns the SAME batch and never re-OCRs (plan rule).
            _logger.LogInformation(
                "Bulk batch {BatchId} already exists for artifact {ArtifactId} (status {Status})",
                existing.Id, sourceArtifactId, existing.Status);
            return existing;
        }

        var batch = new BulkAnswerBatch
        {
            QuestionId = questionId,
            SourceArtifactId = sourceArtifactId,
            SourceFileName = Truncate(sourceFileName, 260),
            SourceContentType = Truncate(sourceContentType, 128),
            CreatedByUserId = userId,
        };
        _db.BulkAnswerBatches.Add(batch);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent upload of the same file raced the unique
            // (QuestionId, SourceArtifactId) index — return the winner.
            _db.Entry(batch).State = EntityState.Detached;
            existing = await _db.BulkAnswerBatches
                .FirstOrDefaultAsync(b => b.QuestionId == questionId && b.SourceArtifactId == sourceArtifactId, ct);
            if (existing is null) throw;
            return existing;
        }
        _logger.LogInformation("Enqueued bulk split batch {BatchId} (question {QuestionId}, artifact {ArtifactId})",
            batch.Id, questionId, sourceArtifactId);
        return batch;
    }

    // ── Lease claim (IngestService pattern) ─────────────────────────────────

    public async Task<BulkAnswerBatch?> ClaimNextAsync(CancellationToken ct = default)
    {
        var candidates = await _db.BulkAnswerBatches
            .Where(b => b.Status == "splitting" && (b.LeaseUntil == null || b.LeaseUntil < DateTime.UtcNow))
            .OrderBy(b => b.CreatedAt)
            .Take(5)
            .ToListAsync(ct);
        if (candidates.Count == 0) return null;

        foreach (var batch in candidates)
        {
            var token = Guid.NewGuid().ToString("N");
            var claim = await _db.BulkAnswerBatches
                .Where(b => b.Id == batch.Id && b.Status == "splitting" &&
                            (b.LeaseUntil == null || b.LeaseUntil < DateTime.UtcNow))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.LeaseToken, token)
                    .SetProperty(b => b.LeaseUntil, DateTime.UtcNow.AddSeconds(_options.Value.LeaseSeconds))
                    .SetProperty(b => b.StartedAt, b => b.StartedAt ?? DateTime.UtcNow)
                    .SetProperty(b => b.Attempts, b => b.Attempts + 1), ct);
            if (claim == 1)
            {
                batch.LeaseToken = token; // local knowledge of the new token
                _logger.LogInformation("Claimed bulk split batch {BatchId} (attempt {Attempts})",
                    batch.Id, batch.Attempts + 1);
                return batch;
            }
            // claim == 0: raced another worker — try the next candidate.
        }
        return null;
    }

    // ── Execute: windows → OCR → grouping ───────────────────────────────────

    public async Task<SplitResult> ExecuteAsync(BulkAnswerBatch batch, string leaseToken, CancellationToken ct = default)
    {
        try
        {
            var source = await _db.Artifacts
                .FirstOrDefaultAsync(a => a.Id == batch.SourceArtifactId, ct)
                ?? throw new FileNotFoundException($"Source artifact {batch.SourceArtifactId} missing");
            var absolutePath = _artifacts.AbsolutePath(source.StorageKey);
            if (!_artifacts.Exists(source.StorageKey))
                throw new FileNotFoundException($"Merged upload file missing: {source.StorageKey}");

            var opts = _options.Value;
            var preferredFormat = _ingestOptions.Value.PageFormat;

            // ── Windowed ingest ─────────────────────────────────────────────
            // Each window renders+registers page artifacts (content-addressed,
            // deduped); the page's preferred-format artifact is the mapping's
            // target. Mapping rows are built fresh — unique (BatchId,
            // PageNumber) backs the delete-then-insert below.
            var mappings = new List<BulkPageMapping>();
            var artifactsByPage = new Dictionary<int, Artifact>();
            var totalPages = 0;
            var offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var response = await _gradingClient.IngestAsync(
                    absolutePath, formats: ["png200", "jpeg150"],
                    maxPages: opts.IngestWindowSize, pageOffset: offset, ct);

                if (offset == 0)
                {
                    totalPages = response.TotalPages;
                    if (totalPages > opts.MaxPages)
                        throw new BusinessRuleException(
                            $"Merged document has {totalPages} pages — the limit is {opts.MaxPages}. Split the scan into several uploads.",
                            "BULK_TOO_MANY_PAGES");
                    if (totalPages == 0)
                        throw new BusinessRuleException(
                            "The uploaded document has no pages to split.", "BULK_NO_PAGES");
                }

                foreach (var page in response.Pages)
                {
                    Artifact? chosen = null;
                    Artifact? first = null;
                    foreach (var format in page.Formats)
                    {
                        var bytes = Convert.FromBase64String(format.DataB64);
                        var artifact = await _artifacts.RegisterPageAsync(
                            bytes, ExtensionForFormat(format.Format, format.MediaType),
                            page.Width, page.Height, source.Id, format.Format, ct);
                        first ??= artifact;
                        if (format.Format == preferredFormat) chosen = artifact;
                    }
                    chosen ??= first
                        ?? throw new InvalidOperationException($"Page {page.PageNumber} produced no artifacts");

                    artifactsByPage[page.PageNumber] = chosen;
                    mappings.Add(new BulkPageMapping
                    {
                        BatchId = batch.Id,
                        PageArtifactId = chosen.Id,
                        PageNumber = page.PageNumber,
                        ReviewStatus = "unmatched",
                    });
                }

                offset += response.Pages.Count;
                if (response.Pages.Count == 0 || offset >= totalPages)
                    break;
                await RenewLeaseAsync(batch.Id, leaseToken, ct); // progress heartbeat
            }

            batch.TotalPages = mappings.Count;

            // ── OCR the header band (Python tesseract — no AI tokens) ───────
            // /split-header returns page_number 1..N per REQUEST; zip by
            // position onto the real page numbers sent.
            var ocrByPage = new Dictionary<int, (string? RawId, decimal Confidence)>();
            foreach (var chunk in mappings.OrderBy(m => m.PageNumber).Select(m => m.PageNumber).Chunk(opts.OcrChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                var paths = chunk.Select(pn => _artifacts.AbsolutePath(
                    artifactsByPage[pn].StorageKey)).ToList();
                var ocr = await _gradingClient.SplitHeaderAsync(paths, opts.HeaderBandPct, ct);
                for (var i = 0; i < chunk.Length && i < ocr.Pages.Count; i++)
                    ocrByPage[chunk[i]] = (ocr.Pages[i].RawId, ocr.Pages[i].Confidence);
                await RenewLeaseAsync(batch.Id, leaseToken, ct); // progress heartbeat
            }

            // ── Grouping: consecutive same-ID pages → per-student stacks ────
            var grouping = GroupIntoStacks(mappings
                .OrderBy(m => m.PageNumber)
                .Select(m => (
                    m.PageNumber,
                    RawId: ocrByPage.TryGetValue(m.PageNumber, out var v) ? v.RawId : null,
                    Confidence: ocrByPage.TryGetValue(m.PageNumber, out var v2) ? v2.Confidence : 0m)));
            var byPage = grouping.ToDictionary(g => g.PageNumber);

            var threshold = opts.OcrConfidenceThreshold;
            var studentsByExternalId = (await _db.Students.ToListAsync(ct))
                .GroupBy(s => NormalizeExternalId(s.ExternalId))
                .Where(g => g.Key is not null)
                .ToDictionary(g => g.Key!, g => g.First());

            foreach (var mapping in mappings)
            {
                var g = byPage[mapping.PageNumber];
                mapping.RawOcrId = g.RawId;
                mapping.OcrConfidence = Math.Round(g.Confidence, 4);
                mapping.SortInStack = g.SortInStack;

                if (g.RawId is null)
                {
                    mapping.ReviewStatus = "unmatched"; // blank/unreadable bucket
                    continue;
                }

                // Unknown id → review; NEVER auto-create a Student (locked).
                if (!studentsByExternalId.TryGetValue(NormalizeExternalId(g.RawId)!, out var student))
                {
                    mapping.ReviewStatus = "needs_review";
                    continue;
                }

                // Below threshold → review; NEVER auto-assign (locked).
                if (g.Confidence < threshold)
                {
                    mapping.ReviewStatus = "needs_review";
                    continue;
                }

                // Known student, confident read. Non-consecutive re-appearance
                // (g.Flagged) still prefills the assignment but forces review.
                mapping.MatchedStudentId = student.Id;
                mapping.ReviewStatus = g.Flagged ? "needs_review" : "auto";
            }

            // Split-run idempotency: this execute OWNS the batch's mappings —
            // clear any rows from an interrupted earlier attempt, then insert.
            await _db.BulkPageMappings.Where(m => m.BatchId == batch.Id).ExecuteDeleteAsync(ct);
            _db.BulkPageMappings.AddRange(mappings);
            // Persist the mappings BEFORE flipping the status — ExecuteUpdate
            // below bypasses the change tracker, so the tracked inserts would
            // otherwise never be saved, and a ready_for_review batch with no
            // pages could never be re-claimed or re-run.
            await _db.SaveChangesAsync(ct);

            await _db.BulkAnswerBatches
                .Where(b => b.Id == batch.Id && b.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, "ready_for_review")
                    .SetProperty(b => b.TotalPages, batch.TotalPages)
                    .SetProperty(b => b.FinishedAt, DateTime.UtcNow)
                    .SetProperty(b => b.LeaseToken, (string?)null)
                    .SetProperty(b => b.LeaseUntil, (DateTime?)null)
                    .SetProperty(b => b.Error, (string?)null)
                    .SetProperty(b => b.ErrorKind, (string?)null), ct);

            _logger.LogInformation(
                "Bulk split batch {BatchId} ready for review: {Pages} pages, {Auto} auto, {Review} review, {Unmatched} unmatched",
                batch.Id, mappings.Count,
                mappings.Count(m => m.ReviewStatus == "auto"),
                mappings.Count(m => m.ReviewStatus == "needs_review"),
                mappings.Count(m => m.ReviewStatus == "unmatched"));
            return new SplitResult(true, null);
        }
        catch (OperationCanceledException)
        {
            await ReleaseLeaseAsync(batch.Id, leaseToken, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var kind = ex is HttpRequestException ? "dependency" :
                       ex is BusinessRuleException ? "business_rule" :
                       ex is FileNotFoundException ? "not_found" : "processing";
            var permanent = ex is BusinessRuleException || batch.Attempts >= 3;
            var message = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            _logger.LogError(ex, "Bulk split batch {BatchId} failed (attempt {Attempts}, {Outcome})",
                batch.Id, batch.Attempts, permanent ? "permanent" : "requeued");
            await _db.BulkAnswerBatches
                .Where(b => b.Id == batch.Id && b.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, permanent ? "failed" : "splitting")
                    .SetProperty(b => b.ErrorKind, kind)
                    .SetProperty(b => b.Error, message)
                    .SetProperty(b => b.FinishedAt, permanent ? DateTime.UtcNow : (DateTime?)null)
                    .SetProperty(b => b.LeaseToken, (string?)null)
                    .SetProperty(b => b.LeaseUntil, (DateTime?)null), ct);
            return new SplitResult(false, ex.Message);
        }
    }

    // ── Grouping ────────────────────────────────────────────────────────────

    internal sealed record PageGrouping(int PageNumber, string? RawId, decimal Confidence, bool Flagged, int SortInStack);

    /// <summary>Group ordered pages into per-student stacks. Consecutive pages
    /// sharing one normalized id form a run (SortInStack 0..n within the run).
    /// An id that REAPPEARS after a different page ran between — another id or
    /// an unreadable page — is non-consecutive: BOTH runs get flagged so the
    /// teacher resolves them; never silently merged (locked rule).</summary>
    internal static List<PageGrouping> GroupIntoStacks(
        IEnumerable<(int PageNumber, string? RawId, decimal Confidence)> pages)
    {
        var ordered = pages.OrderBy(p => p.PageNumber).ToList();

        // Pass 1: runs + non-consecutive re-appearance detection. A run is a
        // maximal stretch of adjacent pages with the same id; ANY page between
        // two runs of one id (different id or unreadable) makes it
        // non-consecutive.
        var flagged = new HashSet<int>();
        var runIndexByPage = new Dictionary<int, int>();
        var runs = new List<(string Id, List<int> Pages)>();
        string? currentId = null;
        foreach (var (pageNumber, rawId, _) in ordered)
        {
            var key = NormalizeExternalId(rawId);
            if (key is not null && key == currentId && runs.Count > 0)
            {
                runs[^1].Pages.Add(pageNumber); // same run continues
                runIndexByPage[pageNumber] = runs.Count - 1;
            }
            else if (key is not null)
            {
                runs.Add((key, [pageNumber]));
                runIndexByPage[pageNumber] = runs.Count - 1;
                currentId = key;
            }
            else
            {
                currentId = null; // unreadable page breaks the run
            }
        }

        // Same id in more than one run → non-consecutive: flag every page of
        // every run carrying that id.
        foreach (var g in runs.GroupBy(r => r.Id).Where(g => g.Count() > 1))
            foreach (var pn in g.SelectMany(r => r.Pages))
                flagged.Add(pn);

        // Pass 2: sort positions within each run.
        var result = new List<PageGrouping>(ordered.Count);
        var sortCounter = new Dictionary<int, int>(); // run index → next SortInStack
        foreach (var (pageNumber, rawId, confidence) in ordered)
        {
            var sortInStack = 0;
            if (runIndexByPage.TryGetValue(pageNumber, out var runIdx))
            {
                sortInStack = sortCounter.GetValueOrDefault(runIdx);
                sortCounter[runIdx] = sortInStack + 1;
            }
            result.Add(new PageGrouping(pageNumber, rawId, confidence, flagged.Contains(pageNumber), sortInStack));
        }
        return result;
    }

    /// <summary>ExternalId comparison: keep ASCII alphanumerics, lowercase —
    /// "402-1131" on paper must match "4021131" in the DB.</summary>
    internal static string? NormalizeExternalId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var norm = new string(raw.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return norm.Length == 0 ? null : norm;
    }

    // ── Apply (confirm → real Answers) ──────────────────────────────────────

    public async Task<BulkApplySummary> ApplyAsync(Guid batchId, Guid userId, CancellationToken ct = default)
    {
        var batch = await _db.BulkAnswerBatches
            .Include(b => b.SourceArtifact)
            .FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new NotFoundException("Bulk batch", batchId);
        if (batch.Status != "ready_for_review")
            throw new BusinessRuleException(
                $"Batch is '{batch.Status}' — only ready_for_review batches can be applied", "BULK_BAD_STATE");

        var mappings = await _db.BulkPageMappings
            .Include(m => m.PageArtifact)
            .Where(m => m.BatchId == batchId && m.ReviewStatus != "skipped")
            .ToListAsync(ct);

        // SAFETY: only auto (confident, consecutive) and confirmed (teacher
        // acted in the review UI) pages flow through. Left-over needs_review /
        // unmatched pages are reported, never silently applied.
        var applicable = mappings
            .Where(m => m.ReviewStatus is "auto" or "confirmed" && m.MatchedStudentId is not null)
            .GroupBy(m => m.MatchedStudentId!.Value)
            .ToList();

        foreach (var group in applicable)
        {
            var pages = group
                .OrderBy(m => m.SortInStack)
                .ThenBy(m => m.PageNumber)
                .ToList();
            var studentId = group.Key;

            var answer = await _db.Answers
                .FirstOrDefaultAsync(a => a.StudentId == studentId && a.QuestionId == batch.QuestionId, ct);

            // Identical semantics to the single-upload path: one answer per
            // (student, question) — replace, and never delete shared
            // content-addressed blobs/pages (every student in this batch
            // shares the SAME merged source key).
            if (answer is not null &&
                !string.IsNullOrEmpty(answer.ImageStorageKey) &&
                !IsContentAddressed(answer.ImageStorageKey) &&
                answer.ImageStorageKey != batch.SourceArtifact.StorageKey)
            {
                try { await _artifacts.DeleteAsync(answer.ImageStorageKey, ct); }
                catch (IOException ex) { _logger.LogWarning(ex, "Could not delete previous answer file {Key}", answer.ImageStorageKey); }
            }

            if (answer is null)
            {
                answer = new Answer
                {
                    StudentId = studentId,
                    QuestionId = batch.QuestionId,
                };
                _db.Answers.Add(answer);
            }

            answer.ImageStorageKey = batch.SourceArtifact.StorageKey;
            answer.OriginalArtifactId = batch.SourceArtifactId;
            answer.FileName = batch.SourceFileName ?? $"bulk-{batch.Id:N}.pdf";
            answer.ContentType = batch.SourceContentType ?? "application/pdf";
            answer.UploadedAt = DateTime.UtcNow;

            // Stale page references from a previous upload must not leak into
            // grading — pages are rebuilt from the confirmed mapping.
            await _db.AnswerPages.Where(p => p.AnswerId == answer.Id).ExecuteDeleteAsync(ct);
            for (var i = 0; i < pages.Count; i++)
            {
                _db.AnswerPages.Add(new AnswerPage
                {
                    AnswerId = answer.Id,
                    ArtifactId = pages[i].PageArtifactId,
                    Format = pages[i].PageArtifact.Format ?? "png200",
                    SortOrder = i, // fresh 0-based ordering across the confirmed stack
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        // Guarded status flip: another concurrent apply already did the same
        // idempotent work — losing the race is harmless.
        var flipped = await _db.BulkAnswerBatches
            .Where(b => b.Id == batchId && b.Status == "ready_for_review")
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, "applied")
                .SetProperty(b => b.FinishedAt, DateTime.UtcNow), ct);
        if (flipped == 0)
            _logger.LogInformation("Bulk batch {BatchId} was applied concurrently — work was idempotent", batchId);

        var summary = new BulkApplySummary(
            applicable.Count,
            applicable.Sum(g => g.Count()),
            mappings.Count(m => m.ReviewStatus == "skipped"),
            mappings.Count(m => m.ReviewStatus is "needs_review" or "unmatched"));
        await _audit.WriteAsync("BulkBatchApplied", "BulkAnswerBatch", batchId, userId,
            new { summary.Students, summary.PagesApplied, summary.PagesSkipped, summary.PagesUnassigned });
        _logger.LogInformation(
            "Bulk batch {BatchId} applied: {Students} students, {Pages} pages, {Unassigned} left unassigned",
            batchId, summary.Students, summary.PagesApplied, summary.PagesUnassigned);
        return summary;
    }

    // ── Worker loop (AppRole=worker) ────────────────────────────────────────

    public async Task RunWorkerLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Bulk split worker loop started (lease={Lease}s)", _options.Value.LeaseSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = await ClaimNextAsync(ct);
                if (batch is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    continue;
                }
                await ExecuteAsync(batch, batch.LeaseToken!, ct);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk split worker loop iteration failed — retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        _logger.LogInformation("Bulk split worker loop stopped");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task RenewLeaseAsync(Guid batchId, string leaseToken, CancellationToken ct)
    {
        // Progress heartbeat: a long windowed ingest/OCR keeps its lease as
        // long as work continues; a dead worker stops renewing and the lease
        // expires normally.
        await _db.BulkAnswerBatches
            .Where(b => b.Id == batchId && b.LeaseToken == leaseToken && b.Status == "splitting")
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.LeaseUntil, DateTime.UtcNow.AddSeconds(_options.Value.LeaseSeconds)), ct);
    }

    private async Task ReleaseLeaseAsync(Guid batchId, string leaseToken, CancellationToken ct)
    {
        try
        {
            await _db.BulkAnswerBatches
                .Where(b => b.Id == batchId && b.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.LeaseToken, (string?)null)
                    .SetProperty(b => b.LeaseUntil, (DateTime?)null), ct);
        }
        catch { /* best effort during cancellation */ }
    }

    private static bool IsContentAddressed(string key) =>
        key.StartsWith("blobs/", StringComparison.Ordinal) || key.StartsWith("pages/", StringComparison.Ordinal);

    private static string ExtensionForFormat(string format, string mediaType) => format switch
    {
        "png200" => ".png",
        "jpeg150" => ".jpg",
        _ => mediaType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            _ => ".bin",
        },
    };

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];
}
