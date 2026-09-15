import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(path.join(root, 'web/package.json'));
const ts = require('typescript');
const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
const layout = ts.transpileModule(await readFile(path.join(root, 'web/src/sequenceLayout.ts'), 'utf8'),
  { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText.replace('export function', 'function');
const output = path.join(root, 'artifacts/sequence-layout'); await mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  await page.route('**/*', route => route.abort());
  await page.setContent('<html><body><main></main></body></html>');
  const font = (await readFile(path.join(root, 'web/src/assets/fonts/PretendardVariable.woff2'))).toString('base64');
  await page.addStyleTag({ content: '@font-face{font-family:"Pretendard Variable";src:url(data:font/woff2;base64,' + font + ')} body{margin:24px;background:white} main{overflow:auto} svg{max-width:none!important}' });
  await page.evaluate(async () => { await document.fonts.load('16px "Pretendard Variable"'); await document.fonts.ready; });
  await page.addScriptTag({ content: await readFile(path.join(root, 'web/node_modules/mermaid/dist/mermaid.min.js'), 'utf8') });
  await page.addScriptTag({ content: layout + '\nglobalThis.prepareSequenceLayout = prepareSequenceLayout;' });
  const cases = {
    nested: 'alt 초기화 상태가 요청한 상태와 같고 연결이 준비되었을 때: initializationStatus == expectedInitializationStatus && connectionStatus == expectedConnectedStatus\nalt 내부 처리 결과가 정상일 때: nestedStatus != expectedNestedStatus\nA->>A: localValidationWithLongFunctionName(result, timeout)\nelse 내부 처리 실패\nNote over A: 실패 상태 기록\nend\nelse 초기화 또는 연결 실패\nA->>B: reportFailure()\nend',
    note: 'alt 이 조건을 만족하면 지역 상태를 갱신하고 다음 단계를 준비합니다\nNote over A: 상태 갱신\nelse 조건 불충족\nNote over A: 상태 유지\nend',
    loop: 'loop 반복 조건 확인: requestIndex ‹ maximumRequestCount\nopt 연결 준비 완료\nA->>B: 상태 확인 및 데이터 요청\nB-->>A: result ← 반환값\nbreak 결과가 실패이면 함수 종료\nA-->>C: 반환 false\nend\nend\nA->>A: requestIndex 값 증가\nend\nA-->>C: 반환 true',
    token: 'opt ' + 'LongIdentifierWithoutWhitespace'.repeat(5) + ' != Ready\nA->>A: Save()\nend'
  };
  const results = [];
  for (const [name, body] of Object.entries(cases)) {
    const result = await page.evaluate(async ({ name, body }) => {
      const source = 'sequenceDiagram\nparticipant A as 요청 처리 함수\nparticipant B as 저장 서비스\nparticipant C as 호출자\n' + body;
      const context = document.createElement('canvas').getContext('2d'); context.font = '16px "Pretendard Variable", "Malgun Gothic", sans-serif';
      const display = prepareSequenceLayout(source, text => context.measureText(text).width);
      mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', htmlLabels: false, theme: 'base', sequence: display.sequence });
      const { svg } = await mermaid.render('layout_' + name, display.source);
      const host = document.querySelector('main'); host.innerHTML = svg;
      const text = [...host.querySelectorAll('text.loopText,text.sectionTitle,text.messageText,text.noteText')].map(e => {
        const b = e.getBBox(); return { text: e.textContent, x: b.x, y: b.y, width: b.width, height: b.height };
      }).filter(b => b.width > 0 && b.height > 0);
      const overlap = [];
      for (let i = 0; i < text.length; i++) for (let j = i + 1; j < text.length; j++) {
        const a = text[i], b = text[j];
        if (Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x) > 1 &&
            Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y) > 1) overlap.push([a.text, b.text]);
      }
      const cuts = [...host.querySelectorAll('line.loopLine')].filter(e => e.getAttribute('y1') === e.getAttribute('y2')).flatMap(e => {
        const x1 = Number(e.getAttribute('x1')), x2 = Number(e.getAttribute('x2')), y = Number(e.getAttribute('y1'));
        return text.filter(t => y > t.y + 2 && y < t.y + t.height - 2 && Math.min(x1, x2) < t.x + t.width && Math.max(x1, x2) > t.x).map(t => t.text);
      });
      const bounds = host.querySelector('svg').viewBox.baseVal;
      const clipped = text.filter(t => t.x < bounds.x - 1 || t.y < bounds.y - 1 || t.x + t.width > bounds.x + bounds.width + 1 || t.y + t.height > bounds.y + bounds.height + 1);
      const image = new Image(); const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }));
      await new Promise((resolve, reject) => { image.onload = resolve; image.onerror = reject; image.src = url; });
      const canvas = document.createElement('canvas'); canvas.width = Math.ceil(bounds.width); canvas.height = Math.ceil(bounds.height);
      canvas.getContext('2d').drawImage(image, 0, 0); const png = canvas.toDataURL(); URL.revokeObjectURL(url);
      return { name, overlap, cuts, clipped, verticalCharacters: text.filter(t => /^.?-$/.test(t.text)).length, svg, png };
    }, { name, body });
    await writeFile(path.join(output, name + '.svg'), result.svg);
    await writeFile(path.join(output, name + '.png'), Buffer.from(result.png.split(',')[1], 'base64'));
    await page.locator('main').screenshot({ path: path.join(output, name + '-preview.png') });
    const { svg, png, ...metrics } = result; results.push(metrics);
    await writeFile(path.join(output, 'result.json'), JSON.stringify(results, null, 2));
    assert.deepEqual(result.overlap, [], name + ': text overlaps');
    assert.deepEqual(result.cuts, [], name + ': fragment line intersects text');
    assert.deepEqual(result.clipped, [], name + ': clipped text');
    assert.equal(result.verticalCharacters, 0, name + ': character-by-character wrapping');
  }
  console.log('Sequence layout: 4 cases passed (text, fragment lines, SVG and PNG).');
} finally { await browser.close(); }
