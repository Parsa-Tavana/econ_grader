import { describe, expect, it } from "vitest";
import {
  formatNumber,
  formatScore,
  formatPercent,
  formatCost,
  formatLatency,
  toFaDigits,
} from "./format";

describe("toFaDigits", () => {
  it("converts ASCII digits to Persian digits", () => {
    expect(toFaDigits("123")).toBe("۱۲۳");
    expect(toFaDigits("3.5s")).toBe("۳.۵s");
  });
});

describe("formatNumber", () => {
  it("returns em-dash for null/undefined/NaN", () => {
    expect(formatNumber(null, "en")).toBe("—");
    expect(formatNumber(undefined, "en")).toBe("—");
    expect(formatNumber(NaN, "en")).toBe("—");
  });

  it("formats with at most 2 fraction digits", () => {
    expect(formatNumber(12.3456, "en")).toBe("12.35");
    expect(formatNumber(12, "en")).toBe("12");
  });

  it("localizes digits for fa", () => {
    expect(formatNumber(12.5, "fa")).toBe("۱۲.۵");
  });
});

describe("formatScore", () => {
  it("delegates to formatNumber", () => {
    expect(formatScore(7.25, "en")).toBe("7.25");
    expect(formatScore(null, "en")).toBe("—");
  });
});

describe("formatPercent", () => {
  it("appends Persian percent sign", () => {
    expect(formatPercent(87.25, "en")).toBe("87.3٪");
    expect(formatPercent(null, "en")).toBe("—");
  });
});

describe("formatCost", () => {
  it("formats USD with 4 decimals", () => {
    expect(formatCost(0.01234, "en")).toBe("$0.0123");
    expect(formatCost(null, "en")).toBe("—");
  });
});

describe("formatLatency", () => {
  it("uses ms under a second and seconds above", () => {
    expect(formatLatency(850, "en")).toBe("850ms");
    expect(formatLatency(1500, "en")).toBe("1.5s");
  });
});