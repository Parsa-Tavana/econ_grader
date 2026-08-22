"""Evaluation metrics — industry-standard scoring agreement measures.

Pure numpy implementation (no scipy) for maximum portability.
All functions operate on arrays of (teacher_score, ai_score) pairs.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Optional
import numpy as np


@dataclass
class EvaluationMetrics:
    count: int
    mae: float
    rmse: float
    exact_agreement_pct: float
    within_half_pct: float
    within_one_pct: float
    bias: float  # mean(AI - teacher)
    pearson_r: Optional[float]
    quadratic_weighted_kappa: Optional[float]
    score_distribution: dict  # {teacher_score: {ai_score: count}}


def _pearson_r(x: np.ndarray, y: np.ndarray) -> Optional[float]:
    """Pearson correlation coefficient (pure numpy)."""
    sx, sy = x.std(), y.std()
    if sx == 0 or sy == 0:
        return None
    r = float(np.corrcoef(x, y)[0, 1])
    return r if not math.isnan(r) else None


def compute_metrics(
    teacher_scores: list[float],
    ai_scores: list[float],
) -> EvaluationMetrics:
    """Compute full evaluation suite. Returns None for metrics that require
    specific conditions (e.g. Pearson needs variance)."""
    if len(teacher_scores) != len(ai_scores) or not teacher_scores:
        raise ValueError("Empty or mismatched score arrays")
    
    n = len(teacher_scores)
    ts = np.array(teacher_scores, dtype=float)
    ai = np.array(ai_scores, dtype=float)
    diff = ai - ts
    
    mae = float(np.mean(np.abs(diff)))
    rmse = float(np.sqrt(np.mean(diff ** 2)))
    exact = float(np.mean(np.abs(diff) < 1e-9) * 100)
    within_half = float(np.mean(np.abs(diff) <= 0.5) * 100)
    within_one = float(np.mean(np.abs(diff) <= 1.0) * 100)
    bias = float(np.mean(diff))
    
    # Pearson correlation
    pearson_r = _pearson_r(ts, ai)
    
    # Quadratic Weighted Kappa
    qwk = _quadratic_weighted_kappa(ts, ai)
    
    # Score distribution table
    dist: dict = {}
    for t, a in zip(ts, ai):
        t_key = round(t, 1)  # bin to 0.1
        a_key = round(a, 1)
        dist.setdefault(t_key, {})
        dist[t_key][a_key] = dist[t_key].get(a_key, 0) + 1
    
    return EvaluationMetrics(
        count=n,
        mae=round(mae, 4),
        rmse=round(rmse, 4),
        exact_agreement_pct=round(exact, 2),
        within_half_pct=round(within_half, 2),
        within_one_pct=round(within_one, 2),
        bias=round(bias, 4),
        pearson_r=pearson_r,
        quadratic_weighted_kappa=qwk,
        score_distribution=dist,
    )


def _quadratic_weighted_kappa(y_true: np.ndarray, y_pred: np.ndarray) -> Optional[float]:
    """Quadratic Weighted Kappa — industry standard for ordinal scoring tasks.
    Handles scores on arbitrary scales by treating them as ordered categories.
    """
    try:
        # Flatten to 1D integer bins (score * 10 -> 0..max*10)
        max_val = max(y_true.max(), y_pred.max())
        min_val = min(y_true.min(), y_pred.min())
        n_bins = int((max_val - min_val) * 10) + 1
        
        if n_bins <= 1:
            return 1.0  # perfect agreement if only one score value
        
        # Map to bin indices
        true_idx = np.round((y_true - min_val) * 10).astype(int)
        pred_idx = np.round((y_pred - min_val) * 10).astype(int)
        
        # Confusion matrix
        O = np.zeros((n_bins, n_bins), dtype=int)
        for t, p in zip(true_idx, pred_idx):
            O[t, p] += 1
        
        # Expected matrix (outer product of marginals)
        row_sums = O.sum(axis=1)
        col_sums = O.sum(axis=0)
        total = O.sum()
        E = np.outer(row_sums, col_sums) / total
        
        # Weight matrix: quadratic distance
        W = np.zeros((n_bins, n_bins), dtype=float)
        for i in range(n_bins):
            for j in range(n_bins):
                W[i, j] = ((i - j) / (n_bins - 1)) ** 2
        
        num = (W * O).sum()
        den = (W * E).sum()
        
        if den == 0:
            return 1.0
        return float(1.0 - num / den)
    except Exception:
        return None


def aggregate_by_provider(runs: list[dict]) -> dict[str, EvaluationMetrics]:
    """Group runs by (provider, model_name, prompt_version) and compute metrics."""
    from collections import defaultdict
    groups = defaultdict(list)
    for r in runs:
        key = f"{r.get('provider', '?')}/{r.get('model_name', '?')}/{r.get('prompt_version', '?')}"
        groups[key].append(r)
    
    out = {}
    for key, items in groups.items():
        t = [float(i["teacher_score"]) for i in items if i.get("teacher_score") is not None]
        a = [float(i["ai_score"]) for i in items if i.get("ai_score") is not None]
        if t and a:
            out[key] = compute_metrics(t, a)
    return out


def compute_human_human_metrics(
    teacher1_scores: list[float],
    teacher2_scores: list[float],
) -> EvaluationMetrics:
    """Compute the same metrics for two independent human graders.
    This is the 'ceiling' against which AI-human agreement should be judged.
    """
    return compute_metrics(teacher1_scores, teacher2_scores)