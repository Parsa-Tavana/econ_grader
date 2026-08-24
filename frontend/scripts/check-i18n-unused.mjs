// Reverse i18n audit: lists locale keys that are NEVER referenced in code.
// Unused keys usually mean dead UI or a feature that was planned but not wired.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const srcDir = join(root, "src");

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    const s = statSync(p);
    if (s.isDirectory()) yield* walk(p);
    else if (/\.(ts|tsx)$/.test(entry) && !entry.includes(".test.") && !entry.startsWith("locales")) yield p;
  }
}

let codeText = "";
for (const f of walk(srcDir)) codeText += readFileSync(f, "utf8");

function flatten(obj, prefix = "") {
  const out = [];
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === "object" && v !== null) out.push(...flatten(v, key));
    else out.push(key);
  }
  return out;
}

function loadLocale(name) {
  let code = readFileSync(join(srcDir, "i18n/locales", `${name}.ts`), "utf8");
  code = code.replace(/export default \w+;?\s*$/, "");
  return new Function(`${code}; return ${name};`)();
}

for (const lang of ["en"]) {
  const dict = loadLocale(lang);
  const all = flatten(dict);
  // Strict: a key counts as used only when its FULL dotted path appears
  // inside a t("...") / t('...') call. Leaf-only matches are ignored.
  const usedKeys = new Set();
  const re = /\bt\(\s*["']([A-Za-z0-9_.]+)["']/g;
  let m;
  while ((m = re.exec(codeText))) usedKeys.add(m[1]);
  const unused = all.filter((k) => !usedKeys.has(k));
  console.log(`${lang}: ${all.length} defined, ${unused.length} unused`);
  for (const k of unused.sort()) console.log("  UNUSED", k);
}