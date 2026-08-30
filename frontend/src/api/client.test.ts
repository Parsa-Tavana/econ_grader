// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { isValidGuid, apiErrorMessage } from "./client";

describe("isValidGuid", () => {
  it("accepts canonical lowercase/uppercase GUIDs", () => {
    expect(isValidGuid("d3b07384-d9a0-4b71-8f5c-2e6a1c9b0f11")).toBe(true);
    expect(isValidGuid("D3B07384-D9A0-4B71-8F5C-2E6A1C9B0F11")).toBe(true);
  });

  it("rejects malformed values", () => {
    expect(isValidGuid(null)).toBe(false);
    expect(isValidGuid(undefined)).toBe(false);
    expect(isValidGuid("")).toBe(false);
    expect(isValidGuid("teacher-1")).toBe(false);
    expect(isValidGuid("d3b07384d9a04b718f5c2e6a1c9b0f11")).toBe(false); // no dashes
    expect(isValidGuid("zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz")).toBe(false);
  });
});

describe("apiErrorMessage", () => {
  it("returns NETWORK_ERROR sentinel for axios network failures", () => {
    const err = Object.assign(new Error("Network Error"), {
      isAxiosError: true,
      code: "ERR_NETWORK",
    });
    expect(apiErrorMessage(err)).toBe("NETWORK_ERROR");
  });

  it("extracts detail/title from problem responses", () => {
    const detail = Object.assign(new Error("x"), {
      isAxiosError: true,
      response: { data: { title: "Validation failed", detail: "Score required" } },
    });
    expect(apiErrorMessage(detail)).toBe("Score required");

    const titleOnly = Object.assign(new Error("x"), {
      isAxiosError: true,
      response: { data: { title: "Not found" } },
    });
    expect(apiErrorMessage(titleOnly)).toBe("Not found");
  });

  it("stringifies non-axios errors", () => {
    expect(apiErrorMessage(new Error("boom"))).toBe("boom");
    expect(apiErrorMessage("plain")).toBe("plain");
  });
});