// Builds wwwroot/icons/lucide.svg: one sprite with only the Lucide icons the app uses.
// Usage (from the repository root):
//   npm install --no-save --prefix scripts/.icons lucide-static
//   node scripts/build-icons.mjs
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const icons = [
  "plus", "log-in", "copy", "check", "share-2", "phone", "phone-off", "phone-incoming", "video", "video-off",
  "mic", "mic-off", "settings", "shield", "shield-check", "user", "user-round", "users", "sun", "moon",
  "clock", "lock", "lock-open", "timer", "hourglass", "wifi", "wifi-off", "x", "eraser", "link", "log-out",
  "menu", "chevron-down", "chevron-right", "circle-help", "door-open", "key-round", "mail", "eye", "eye-off",
  "trash-2", "history", "bookmark", "user-plus", "external-link", "triangle-alert", "circle-check",
  "circle-alert", "refresh-cw", "info", "square-pen", "user-x", "radio", "qr-code", "download", "keyboard",
  "cloud", "cloud-off", "loader-circle", "git-merge", "file-text", "monitor-smartphone", "calendar-clock", "panel-right",
];

const dir = join(import.meta.dirname, ".icons", "node_modules", "lucide-static", "icons");
const version = JSON.parse(readFileSync(join(dir, "..", "package.json"), "utf8")).version;
const symbols = icons.map((name) => {
  const svg = readFileSync(join(dir, `${name}.svg`), "utf8");
  const body = svg
    .replace(/<!--[\s\S]*?-->/g, "")
    .replace(/^[\s\S]*?<svg[^>]*>/, "")
    .replace(/<\/svg>\s*$/, "")
    .replace(/\s+/g, " ")
    .trim();
  return `<symbol id="${name}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${body}</symbol>`;
});
const out = `<svg xmlns="http://www.w3.org/2000/svg">\n<!-- Lucide ${version} icons (ISC License, https://lucide.dev/license). Generated sprite; regenerate with scripts/build-icons.mjs -->\n${symbols.join("\n")}\n</svg>\n`;
writeFileSync(join(import.meta.dirname, "..", "src", "NeuralBridge.Web", "wwwroot", "icons", "lucide.svg"), out);
console.log(`${icons.length} icons written (lucide-static ${version})`);
