import type { AppLang } from "../i18n";

/** Reads the active app language reactively via i18n events is overkill here;
 *  components can call `i18n.language` through useTranslation instead.
 *  This helper keeps a stable import path for formatting utilities. */
export function currentLang(): AppLang {
  const stored = localStorage.getItem("econgrader.lang");
  return stored === "en" ? "en" : "fa";
}
