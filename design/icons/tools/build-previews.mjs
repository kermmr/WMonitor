// Generates pure-SVG preview sheets for the WMonitor icon proposals.
// These sheets are rasterized in a browser via img->canvas, bypassing the
// unreliable IAB screenshot compositor.
// Usage: node build-previews.mjs <svgDir> <outDir>
import fs from "node:fs";
import path from "node:path";

const [, , svgDir, outDir] = process.argv;
if (!svgDir || !outDir) {
  console.error("usage: node build-previews.mjs <svgDir> <outDir>");
  process.exit(1);
}
fs.mkdirSync(outDir, { recursive: true });

const sources = {
  aD: fs.readFileSync(path.join(svgDir, "A-pulse.dark.svg"), "utf8"),
  aL: fs.readFileSync(path.join(svgDir, "A-pulse.light.svg"), "utf8"),
  bD: fs.readFileSync(path.join(svgDir, "B-gauge.dark.svg"), "utf8"),
  bL: fs.readFileSync(path.join(svgDir, "B-gauge.light.svg"), "utf8"),
  cD: fs.readFileSync(path.join(svgDir, "C-radar.dark.svg"), "utf8"),
  cL: fs.readFileSync(path.join(svgDir, "C-radar.light.svg"), "utf8"),
};

let counter = 0;
function iconG(key, tx, ty, size) {
  const prefix = `${key}p${counter++}`;
  const scale = size / 512;
  const inner = sources[key]
    .replace(/^<svg[^>]*>/, "")
    .replace(/<\/svg>\s*$/, "")
    .replace(/\bid="([^"]+)"/g, (_, id) => `id="${prefix}-${id}"`)
    .replace(/url\(#([^)]+)\)/g, (_, id) => `url(#${prefix}-${id})`);
  return `<g transform="translate(${tx} ${ty}) scale(${scale})">${inner}</g>`;
}

const FONT = 'Segoe UI, Microsoft YaHei, system-ui, sans-serif';
const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;");

function caption(x, y, text, dark) {
  const fill = dark ? "#9AA3B2" : "#5C636E";
  return `<text x="${x}" y="${y}" font-family="${FONT}" font-size="13" letter-spacing="1.5" fill="${fill}" text-anchor="middle">${esc(text)}</text>`;
}

function sizeRow(cx, topY, key, dark) {
  const sizes = [48, 32, 24, 16];
  const gap = 22;
  const total = sizes.reduce((a, b) => a + b, 0) + gap * (sizes.length - 1);
  let x = cx - total / 2;
  const labelFill = dark ? "#9AA3B2" : "#5C636E";
  return sizes
    .map((s) => {
      const g = iconG(key, x, topY, s);
      const t = `<text x="${x + s / 2}" y="${topY + s + 18}" font-family="${FONT}" font-size="11" fill="${labelFill}" text-anchor="middle">${s}</text>`;
      x += s + gap;
      return g + t;
    })
    .join("");
}

function taskbar(boardX, boardW, y, key, theme) {
  const w = 340;
  const x = boardX + (boardW - w) / 2;
  const h = 46;
  const bg = theme === "dark" ? "rgba(34,36,40,.965)" : "rgba(244,244,244,.97)";
  const border = theme === "dark" ? "rgba(255,255,255,.15)" : "rgba(0,0,0,.10)";
  const pillBg = theme === "dark" ? "rgba(255,255,255,.07)" : "rgba(0,0,0,.05)";
  const clockFill = theme === "dark" ? "#9AA3B2" : "#5C636E";
  const dots =
    theme === "dark"
      ? ["#4CC2FF", "#B39DFF", "#3DDC97", "#FFB340"]
      : ["#0A84FF", "#7C5CE0", "#11A36B", "#D18400"];
  const dotEls = dots
    .map((c, i) => `<circle cx="${x + 66 + i * 16}" cy="${y + 23}" r="3.2" fill="${c}"/>`)
    .join("");
  return `<g>
    <rect x="${x}" y="${y}" width="${w}" height="${h}" rx="10" fill="${bg}" stroke="${border}" stroke-width="1"/>
    ${iconG(key, x + 13, y + 15, 16)}
    <rect x="${x + 42}" y="${y + 11}" width="92" height="24" rx="7" fill="${pillBg}"/>
    ${dotEls}
    <text x="${x + w - 14}" y="${y + 27}" font-family="${FONT}" font-size="11" fill="${clockFill}" text-anchor="end">14:32</text>
  </g>`;
}

function board(x, y, w, h, theme, key, captionText) {
  const bg = theme === "dark" ? "#0E1014" : "#EDEFF3";
  const border = theme === "dark" ? "rgba(255,255,255,.07)" : "rgba(0,0,0,.06)";
  const cx = x + w / 2;
  const heroSize = 130;
  const heroY = y + 62;
  return `<g>
    <rect x="${x}" y="${y}" width="${w}" height="${h}" rx="20" fill="${bg}" stroke="${border}" stroke-width="1"/>
    ${caption(cx, y + 40, captionText, theme === "dark")}
    ${iconG(key, cx - heroSize / 2, heroY, heroSize)}
    ${sizeRow(cx, heroY + heroSize + 34, key, theme === "dark")}
    ${taskbar(x, w, y + h - 66, key, theme)}
  </g>`;
}

function schemeSheet(name, tag, keyD, keyL) {
  const W = 1240;
  const H = 470;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">
  <rect width="${W}" height="${H}" fill="#FFFFFF"/>
  <text x="8" y="24" font-family="${FONT}" font-size="15" font-weight="600" fill="#141414">${esc(name)}</text>
  <text x="${8 + name.length * 15 + 14}" y="24" font-family="${FONT}" font-size="12" fill="#5C636E">${esc(tag)}</text>
  ${board(8, 40, 604, 422, "dark", keyD, "深色主题 · WindowBg #0E1014")}
  ${board(628, 40, 604, 422, "light", keyL, "浅色主题 · WindowBg #EDEFF3")}
</svg>`;
}

function stripSheet() {
  const keys = ["aD", "aL", "bD", "bL", "cD", "cL"];
  const W = 580;
  const H = 96;
  const icons = keys.map((k, i) => iconG(k, 24 + i * 92, 20, 56)).join("");
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">
  <rect width="${W}" height="${H}" rx="12" fill="#F1F3F6"/>
  ${icons}
</svg>`;
}

function overviewSheet() {
  const schemeDefs = [
    ["方案 A · 脉搏", "实时采样活性：灰色采样轨 + ECG 波形 + 绿色 live 点", "aD", "aL"],
    ["方案 B · 仪表", "资源水位与阈值：270° 轨道 + 68% 弧 + 指针", "bD", "bL"],
    ["方案 C · 雷达", "看护巡视：双环 + 扫描扇面 + 目标点", "cD", "cL"],
  ];
  const W = 1240;
  const headH = 46;
  const stripH = 116;
  const secH = 470;
  const H = headH + stripH + schemeDefs.length * (secH + 8) + 34;
  const parts = [];
  parts.push(`<rect width="${W}" height="${H}" fill="#FFFFFF"/>`);
  parts.push(
    `<text x="8" y="26" font-family="${FONT}" font-size="16" font-weight="600" fill="#141414">WMonitor 应用图标提案 · 三方案 × 明暗主题</text>`,
  );
  parts.push(
    `<text x="8" y="44" font-family="${FONT}" font-size="11" fill="#5C636E">无字母抽象图形 · 统一 #0A84FF + 圆角方块语言 · 深浅两版颜色直接映射 Theme.cs token（深 #4CC2FF/#3DDC97，浅 #0A84FF/#11A36B）</text>`,
  );
  parts.push(iconStripAt(8, headH + 8));
  let y = headH + stripH + 10;
  for (const [name, tag, kD, kL] of schemeDefs) {
    parts.push(schemeGroupAt(8, y, name, tag, kD, kL));
    y += secH + 8;
  }
  parts.push(
    `<text x="8" y="${H - 12}" font-family="${FONT}" font-size="11" fill="#5C636E">小尺寸行（48/32/24/16）为等比缩略示意；落地 .ico 时 32px 以下会按规范简化细节。</text>`,
  );
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">${parts.join("")}</svg>`;

  function iconStripAt(x, yy) {
    const keys = ["aD", "aL", "bD", "bL", "cD", "cL"];
    const icons = keys.map((k, i) => iconG(k, x + 16 + i * 92, yy + 16, 56)).join("");
    return `<g><rect x="${x}" y="${yy}" width="580" height="88" rx="12" fill="#F1F3F6"/>${icons}</g>`;
  }
  function schemeGroupAt(x, yy, name, tag, kD, kL) {
    return `
    <text x="${x}" y="${yy + 18}" font-family="${FONT}" font-size="15" font-weight="600" fill="#141414">${esc(name)}</text>
    <text x="${x + name.length * 15 + 14}" y="${yy + 18}" font-family="${FONT}" font-size="12" fill="#5C636E">${esc(tag)}</text>
    ${board(x + 0, yy + 30, 604, 422, "dark", kD, "深色主题 · WindowBg #0E1014")}
    ${board(x + 620, yy + 30, 604, 422, "light", kL, "浅色主题 · WindowBg #EDEFF3")}`;
  }
}

fs.writeFileSync(path.join(outDir, "sheet-A-pulse.svg"), schemeSheet("方案 A · 脉搏", "实时采样活性：灰色采样轨 + ECG 波形 + 绿色 live 点", "aD", "aL"));
fs.writeFileSync(path.join(outDir, "sheet-B-gauge.svg"), schemeSheet("方案 B · 仪表", "资源水位与阈值：270° 轨道 + 68% 弧 + 指针", "bD", "bL"));
fs.writeFileSync(path.join(outDir, "sheet-C-radar.svg"), schemeSheet("方案 C · 雷达", "看护巡视：双环 + 扫描扇面 + 目标点", "cD", "cL"));
fs.writeFileSync(path.join(outDir, "sheet-strip.svg"), stripSheet());
fs.writeFileSync(path.join(outDir, "sheet-overview.svg"), overviewSheet());
console.log(`sheets written to ${outDir}`);
