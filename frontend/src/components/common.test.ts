import { describe, expect, it } from "vitest";
import { answerStatus } from "./common";
import type { AnswerDto, GradingRunSummaryDto } from "../types/models";

function run(partial: Partial<GradingRunSummaryDto>): GradingRunSummaryDto {
  return {
    id: "r1",
    provider: "claude",
    modelName: "claude-3",
    promptVersion: "default",
    temperature: 0,
    aiScore: 7,
    isValid: true,
    createdAt: new Date().toISOString(),
    ...partial,
  };
}

function answer(runs: GradingRunSummaryDto[]): AnswerDto {
  return {
    id: "a1",
    studentId: "s1",
    studentExternalId: "STU-1",
    questionId: "q1",
    imageStorageKey: "k",
    teacherScore: null,
    teacher2Score: null,
    uploadedAt: new Date().toISOString(),
    gradingRuns: runs,
  };
}

describe("answerStatus", () => {
  it("noAi when there are no runs", () => {
    expect(answerStatus(answer([]))).toBe("noAi");
  });

  it("reviewed when any run has a teacher snapshot", () => {
    const a = answer([run({ teacherScoreSnapshot: 6.5 })]);
    expect(answerStatus(a)).toBe("reviewed");
  });

  it("aiGraded when runs exist but none reviewed", () => {
    const a = answer([run({}), run({ id: "r2" })]);
    expect(answerStatus(a)).toBe("aiGraded");
  });

  it("error when a run failed validation", () => {
    const a = answer([run({ isValid: false, error: "timeout" })]);
    expect(answerStatus(a)).toBe("error");
  });
});