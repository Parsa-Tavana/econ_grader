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
});