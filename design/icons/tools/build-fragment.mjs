// Generates the self-contained visualization fragment for WMonitor icon proposals.
// Reads the 6 standalone SVG sources and inlines each instance fully (no <use>/<symbol>),
// prefixing every id per instance so gradients/clips/filters never collide.
// Usage: node build-fragment.mjs <svgDir> <outFile>
import fs from "node:fs";
import path from "node:path";

const [, , svgDir, outFile] = process.argv;
if (!svgDir || !outFile) {
  console.error("usage: node build-fragment.mjs <svgDir> <outFile>");
  process.exit(1);
}

const sources = {
  aD: fs.readFileSync(path.join(svgDir, "A-pulse.dark.svg"), "utf8"),
  aL: fs.readFileSync(path.join(svgDir, "A-pulse.light.svg"), "utf8"),
  bD: fs.readFileSync(path.join(svgDir, "B-gauge.dark.svg"), "utf8"),
  bL: fs.readFileSync(path.join(svgDir, "B-gauge.light.svg"), "utf8"),
  cD: fs.readFileSync(path.join(svgDir, "C-radar.dark.svg"), "utf8"),
  cL: fs.readFileSync(path.join(svgDir, "C-radar.light.svg"), "utf8"),
};

let counter = 0;
function icon(key, size, attrs = "") {
  const prefix = `${key}i${counter++}`;
  let svg = sources[key];
  // namespace ids and references
  svg = svg
    .replace(/\bid="([^"]+)"/g, (_, id) => `id="${prefix}-${id}"`)
    .replace(/url\(#([^)]+)\)/g, (_, id) => `url(#${prefix}-${id})`)
    .replace(/href="#([^"]+)"/g, (_, id) => `href="#${prefix}-${id}"`);
  // drop width/height from root, apply requested size
  svg = svg.replace(/width="\d+" height="\d+"/, `width="${size}" height="${size}"${attrs}`);
  return svg;
}

const dot = (c) => `<i style="background:${c}"></i>`;
const pillDark = `<span class="wm-pill">${dot("#4CC2FF")}${dot("#B39DFF")}${dot("#3DDC97")}${dot("#FFB340")}</span>`;
const pillLight = `<span class="wm-pill">${dot("#0A84FF")}${dot("#7C5CE0")}${dot("#11A36B")}${dot("#D18400")}</span>`;

function board(theme, key) {
  const label = theme === "dark" ? "深色主题 · WindowBg #0E1014" : "浅色主题 · WindowBg #EDEFF3";
  const pill = theme === "dark" ? pillDark : pillLight;
  const sizes = [48, 32, 24, 16]
    .map((s) => `<div class="wm-size-cell">${icon(key, s)}<span class="wm-size-label">${s}</span></div>`)
    .join("");
  return `<div class="wm-board ${theme}">
      <span class="wm-caption">${label}</span>
      ${icon(key, 120)}
      <div class="wm-sizes">${sizes}</div>
      <div class="wm-taskbar">${icon(key, 16)}${pill}<span class="wm-clock">14:32</span></div>
    </div>`;
}

function scheme(name, tag, keyD, keyL) {
  return `<section class="wm-scheme">
  <div class="wm-scheme-head"><span class="wm-scheme-name">${name}</span><span class="wm-scheme-tag">${tag}</span></div>
  <div class="wm-board-row">
    ${board("dark", keyD)}
    ${board("light", keyL)}
  </div>
</section>`;
}

const overview = ["aD", "aL", "bD", "bL", "cD", "cL"]
  .map((k) => icon(k, 56))
  .join("\n  ");

const html = `<div id="wmonitor-icon-proposals">
<style>
#wmonitor-icon-proposals{font-family:"Segoe UI Variable","Segoe UI",system-ui,-apple-system,"PingFang SC","Microsoft YaHei",sans-serif;color:var(--foreground);}
#wmonitor-icon-proposals h2{font-size:var(--font-size-base,14px);font-weight:500;margin:0 0 2px;}
#wmonitor-icon-proposals .wm-sub{color:var(--muted-foreground);font-size:12px;margin:0 0 18px;}
#wmonitor-icon-proposals .wm-scheme{margin:0 0 26px;}
#wmonitor-icon-proposals .wm-scheme-head{display:flex;align-items:baseline;gap:10px;margin:0 0 10px;flex-wrap:wrap;}
#wmonitor-icon-proposals .wm-scheme-name{font-weight:500;}
#wmonitor-icon-proposals .wm-scheme-tag{font-size:11px;color:var(--muted-foreground);}
#wmonitor-icon-proposals .wm-board-row{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px;}
#wmonitor-icon-proposals .wm-board{border-radius:16px;padding:26px 22px 20px;display:flex;flex-direction:column;align-items:center;gap:18px;}
#wmonitor-icon-proposals .wm-board.dark{background:#0E1014;border:1px solid rgba(255,255,255,.07);}
#wmonitor-icon-proposals .wm-board.light{background:#EDEFF3;border:1px solid rgba(0,0,0,.06);}
#wmonitor-icon-proposals .wm-caption{font-size:11px;letter-spacing:.08em;}
#wmonitor-icon-proposals .wm-board.dark .wm-caption{color:#9AA3B2;}
#wmonitor-icon-proposals .wm-board.light .wm-caption{color:#5C636E;}
#wmonitor-icon-proposals .wm-sizes{display:flex;align-items:flex-end;gap:22px;}
#wmonitor-icon-proposals .wm-size-cell{display:flex;flex-direction:column;align-items:center;gap:6px;}
#wmonitor-icon-proposals .wm-size-label{font-size:11px;tabular-nums;}
#wmonitor-icon-proposals .wm-board.dark .wm-size-label{color:#9AA3B2;}
#wmonitor-icon-proposals .wm-board.light .wm-size-label{color:#5C636E;}
#wmonitor-icon-proposals .wm-taskbar{width:100%;max-width:340px;border-radius:10px;display:flex;align-items:center;gap:8px;padding:5px 10px;box-sizing:border-box;}
#wmonitor-icon-proposals .wm-board.dark .wm-taskbar{background:rgba(34,36,40,.965);border:1px solid rgba(255,255,255,.15);}
#wmonitor-icon-proposals .wm-board.light .wm-taskbar{background:rgba(244,244,244,.97);border:1px solid rgba(0,0,0,.10);}
#wmonitor-icon-proposals .wm-pill{display:flex;align-items:center;gap:5px;border-radius:7px;padding:3px 8px;}
#wmonitor-icon-proposals .wm-board.dark .wm-pill{background:rgba(255,255,255,.07);}
#wmonitor-icon-proposals .wm-board.light .wm-pill{background:rgba(0,0,0,.05);}
#wmonitor-icon-proposals .wm-pill i{display:block;width:6px;height:6px;border-radius:3px;}
#wmonitor-icon-proposals .wm-clock{font-size:11px;tabular-nums;margin-left:auto;}
#wmonitor-icon-proposals .wm-board.dark .wm-clock{color:#9AA3B2;}
#wmonitor-icon-proposals .wm-board.light .wm-clock{color:#5C636E;}
#wmonitor-icon-proposals .wm-overview{display:flex;gap:16px;flex-wrap:wrap;align-items:center;margin:0 0 26px;padding:14px 16px;border-radius:12px;background:var(--muted,#F1F3F6);}
#wmonitor-icon-proposals .wm-note{font-size:11px;color:var(--muted-foreground,#666);margin:4px 0 0;}
</style>

<h2>WMonitor 应用图标提案 · 三方案 × 明暗主题</h2>
<p class="wm-sub">无字母抽象图形 · 统一 #0A84FF + 圆角方块语言 · 深浅两版颜色直接映射 Theme.cs token（深 #4CC2FF/#3DDC97，浅 #0A84FF/#11A36B）</p>

<div class="wm-overview" aria-label="六版图标总览">
  ${overview}
</div>

${scheme("方案 A · 脉搏", "实时采样活性：灰色采样轨 + ECG 波形 + 绿色 live 点", "aD", "aL")}
${scheme("方案 B · 仪表", "资源水位与阈值：270° 轨道 + 68% 弧 + 指针", "bD", "bL")}
${scheme("方案 C · 雷达", "看护巡视：双环 + 扫描扇面 + 目标点", "cD", "cL")}

<p class="wm-note">小尺寸行（48/32/24/16）为等比缩略示意；落地 .ico 时 32px 以下会按规范简化细节。</p>
</div>
`;

fs.writeFileSync(outFile, html);
console.log(`written ${outFile} (${html.length} bytes, ${counter} instances, use refs: ${(html.match(/<use/g) || []).length})`);
