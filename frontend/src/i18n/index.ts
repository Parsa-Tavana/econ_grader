import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import fa from "./locales/fa.ts";
import en from "./locales/en.ts";

export const LANG_KEY = "econgrader.lang";
export type AppLang = "fa" | "en";

function detectInitial(): AppLang {
  const saved = localStorage.getItem(LANG_KEY);
  if (saved === "fa" || saved === "en") return saved;
  return "fa"; // Persian is the primary production language
}

export function applyDirection(lang: AppLang) {
  const dir = lang === "fa" ? "rtl" : "ltr";
  document.documentElement.setAttribute("dir", dir);
  document.documentElement.setAttribute("lang", lang === "fa" ? "fa-IR" : "en-US");
}

i18n.use(initReactI18next).init({
  resources: {
    fa: { translation: fa },
    en: { translation: en },
  },
  lng: detectInitial(),
  fallbackLng: "en",
  interpolation: { escapeValue: false },
});

applyDirection(detectInitial() as AppLang);

export default i18n;

export function changeLanguage(lang: AppLang) {
  i18n.changeLanguage(lang);
  applyDirection(lang);
  localStorage.setItem(LANG_KEY, lang);
}