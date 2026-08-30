// i18n completeness checker.
// Scans source files for t("dotted.key") usages and verifies each exists in
// BOTH locales (src/i18n/locales/en.ts and fa.ts).
//
//   node scripts/check-i18n.mjs     → exits 1 if any key is missing
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
    else if (/\.(ts|tsx)$/.test(entry) && !entry.endsWith(".test.ts")) yield p;
  }
}

// Extract used keys: t("a.b.c") and i18n.t('x.y') variants.
const used = new Map(); // key -> [file]
for (const file of walk(srcDir)) {
  const text = readFileSync(file, "utf8");
  const re = /\bt\(\s*["']([A-Za-z0-9_.]+)["']/g;
  let m;
  while ((m = re.exec(text))) {
    const key = m[1];
    if (!key.includes(".")) continue;
    if (!used.has(key)) used.set(key, []);
    used.get(key).push(file.replace(srcDir + "/", ""));
  }
}

// Load a locale module (plain object literal → eval-able JS).
function loadLocale(name) {
  let code = readFileSync(join(srcDir, "i18n/locales", `${name}.ts`), "utf8");
  code = code.replace(/export default \w+;?\s*$/, "");
  const obj = new Function(`${code}; return ${name};`)();
  return obj;
}

function hasKey(obj, dotted) {
  return dotted.split(".").every((part) => {
    if (obj == null || typeof obj !== "object" || !(part in obj)) return false;
    obj = obj[part];
    return true;
  });
}

let failures = 0;
for (const lang of ["en", "fa"]) {
  let dict;
  try {
    dict = loadLocale(lang);
  } catch (e) {
    console.error(`✖ ${lang}.ts could not be parsed: ${e.message}`);
    process.exit(2);
  }
  const missing = [...used.keys()].filter((k) => !hasKey(dict, k));
  console.log(`— ${lang}: ${used.size} keys used, ${missing.length} missing`);
  if (missing.length) {
    failures++;
    for (const k of missing.sort()) {
      console.log(`   MISSING  ${k}   (used in ${[...new Set(used.get(k))].join(", ")})`);
    }
  }
}

process.exit(failures ? 1 : 0);