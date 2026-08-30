import { describe, expect, it } from "vitest";
import { parseCriteriaScores } from "./models";

describe("parseCriteriaScores", () => {
  it("parses a valid criteria JSON array", () => {
    const json = JSON.stringify([
      { criterionId: "c1", score: 4, maxScore: 5, comment: "good" },
      { criterionId: "c2", score: 2, maxScore: 5 },
    ]);
    const result = parseCriteriaScores(json);
    expect(result).toHaveLength(2);
    expect(result[0]).toEqual({ criterionId: "c1", score: 4, maxScore: 5, comment: "good" });
  });

  it("returns empty for null/undefined/empty", () => {
    expect(parseCriteriaScores(null)).toEqual([]);
    expect(parseCriteriaScores(undefined)).toEqual([]);
    expect(parseCriteriaScores("")).toEqual([]);
  });

  it("returns empty for invalid JSON or non-arrays", () => {
    expect(parseCriteriaScores("{not json")).toEqual([]);
    expect(parseCriteriaScores(JSON.stringify({ a: 1 }))).toEqual([]);
  });

  it("tolerates legacy PascalCase rows (older API builds)", () => {
    const json = JSON.stringify([
      { CriterionId: "ترسیم کلی", Score: 1.0, MaxScore: 1.0, Comment: "ok" },
    ]);
    expect(parseCriteriaScores(json)).toEqual([
      { criterionId: "ترسیم کلی", score: 1, maxScore: 1, comment: "ok" },
    ]);
  });

  it("tolerates snake_case rows (python service shape)", () => {
    const json = JSON.stringify([{ criterion_id: "c1", score: 2, max_score: 4 }]);
    expect(parseCriteriaScores(json)).toEqual([
      { criterionId: "c1", score: 2, maxScore: 4, comment: null },
    ]);
  });

  it("never throws on rows with missing or non-object entries", () => {
    const json = JSON.stringify([{}, null, 42]);
    expect(parseCriteriaScores(json)).toEqual([
      { criterionId: "unknown", score: 0, maxScore: 0, comment: null },
    ]);
  });
});