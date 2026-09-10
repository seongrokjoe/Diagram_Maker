import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(path.join(root, 'web/package.json'));
const ts = require('typescript');
const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
const safety = ts.transpileModule(await readFile(path.join(root, 'web/src/svgSafety.ts'), 'utf8'),
  { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText.replace('export function sanitizeSvg', 'function sanitizeSvg');
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const output = path.join(root, 'artifacts/svg-safety'); await mkdir(output, { recursive: true });
try {
  const page = await browser.newPage();
  await page.route('**/*', route => route.abort());
  await page.setContent('<html><body></body></html>');
  await page.addScriptTag({ content: await readFile(path.join(root, 'artifacts/verify-web-dist/vendor/mermaid.min.js'), 'utf8') });
  await page.addScriptTag({ content: safety + '\nglobalThis.sanitizeSvg = sanitizeSvg;' });
  const result = await page.evaluate(async () => {
    mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', htmlLabels: false, theme: 'base' });
    const cases = [
      'flowchart LR\n a["입력 확인"] -->|"검사 성공"| b["저장"]',
      'sequenceDiagram\n participant a as 요청\n participant b as 저장소\n a->>b: 저장 요청',
      'classDiagram\n class A\n class B\n A --> B : 사용',
      'stateDiagram-v2\n 대기 --> 완료: 저장 성공',
      'flowchart TB\n a["실행"] -.->|"호출"| b["저장"]'
    ];
    const checks = [];
    for (let i = 0; i < cases.length; i++) {
      const { svg } = await mermaid.render(`diagram_check_${i}`, cases[i]);
      const original = document.createElement('div'); original.innerHTML = svg; document.body.append(original);
      const paint = host => [...host.querySelectorAll('rect, polygon, path, text')].map(e => {
        const s = getComputedStyle(e); return [s.fill, s.stroke, s.fillOpacity, s.strokeWidth, s.fontSize];
      });
      const expected = paint(original); original.remove();
      const clean = sanitizeSvg(svg); const host = document.createElement('div'); host.innerHTML = clean; document.body.append(host);
      const actual = paint(host); host.remove();
      if (JSON.stringify(expected) !== JSON.stringify(actual)) throw new Error(`Paint changed in format ${i}: ${JSON.stringify({
        originalTags: [...new Set([...original.querySelectorAll('*')].map(e => e.localName + ':' + e.namespaceURI))],
        safeTags: [...new Set([...host.querySelectorAll('*')].map(e => e.localName + ':' + e.namespaceURI))] })}`);
      if (!clean.includes('<style')) throw new Error('Styles lost');
      const img = new Image(); const url = URL.createObjectURL(new Blob([clean], { type: 'image/svg+xml' }));
      await new Promise((resolve, reject) => { img.onload = resolve; img.onerror = reject; img.src = url; });
      const canvas = document.createElement('canvas'); canvas.width = 800; canvas.height = 500;
      canvas.getContext('2d').drawImage(img, 0, 0); canvas.toDataURL(); URL.revokeObjectURL(url);
      checks.push(`format ${i}: SVG paint and PNG export`);
    }
    const hostile = sanitizeSvg('<svg xmlns="http://www.w3.org/2000/svg" id="diagram_hostile"><style>@import url(https://example.invalid/a); @keyframes dash{to{opacity:0}} #diagram_hostile rect{fill:#e7f0ff;stroke:#123456;background:url(https://example.invalid/a)} body{background:red}</style><script>alert(1)</script><rect onclick="alert(1)" style="fill:#e7f0ff;stroke:url(https://example.invalid/a)"/><a href="https://example.invalid"><text>표시</text></a></svg>');
    const safeHost = document.createElement('div'); safeHost.innerHTML = hostile; document.body.append(safeHost);
    const fill = getComputedStyle(safeHost.querySelector('rect')).fill; safeHost.remove();
    if (/example.invalid|onclick|<script|<a |@import|@keyframes|body\s*\{/.test(hostile) || fill !== 'rgb(231, 240, 255)') throw new Error('Unsafe SVG or lost safe color: ' + hostile);
    const escaped = sanitizeSvg('<svg xmlns="http://www.w3.org/2000/svg" id="diagram_scope"><style>#diagram_scope + body{background:red} #diagram_scope ~ div{display:none} #diagram_scope rect{fill:#ffffff;background:image-set(url(https://example.invalid/a) 1x)}</style><audio xmlns="http://www.w3.org/1999/xhtml" src="https://example.invalid/a"/><rect/></svg>');
    if (/example.invalid|<audio|\+ body|~ div|image-set/.test(escaped)) throw new Error('SVG namespace or selector escaped its scope');
    return { status: 'passed', checks: [...checks, 'malicious rules removed without erasing safe declarations'] };
  });
  assert.equal(result.status, 'passed'); await writeFile(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
  console.log('SVG safety and paint preservation: 6 checks passed.');
} finally { await browser.close(); }
