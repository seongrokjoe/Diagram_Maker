import assert from 'node:assert/strict';
import path from 'node:path';

export async function checkZoomWheel(page, editor) {
  const canvas = editor.locator('.diagram-canvas');
  await canvas.locator('svg').waitFor();
  const originalViewport = await page.evaluate(() => innerWidth);
  await editor.getByLabel('원본 기준 확대', { exact: true }).selectOption('1');
  await canvas.hover();
  await page.mouse.wheel(0, 120);
  await page.waitForTimeout(120);
  assert.equal(await editor.locator('.zoom-status').innerText(), '100%', 'ordinary wheel must scroll without zooming');
  await canvas.hover();
  await page.keyboard.down('Control');
  try {
    await page.mouse.wheel(0, -120);
    await editor.locator('.zoom-status').filter({ hasText: /^120%$/ }).waitFor();
  } finally { await page.keyboard.up('Control'); }
  assert.equal(await page.evaluate(() => innerWidth), originalViewport, 'Ctrl+wheel in canvas must not zoom the browser');
  await editor.getByRole('button', { name: '맞춤 보기', exact: true }).click();
}

export async function checkResultPresentation(page, workspace, output, prefix) {
  for (const width of [1440, 1366, 800, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    const checkbox = workspace.getByLabel('AI/Code 함께 보기', { exact: true });
    await checkbox.scrollIntoViewIfNeeded();
    const aligned = await checkbox.evaluate(input => {
      const label = input.closest('label'), text = label.querySelector('span');
      const a = input.getBoundingClientRect(), b = text.getBoundingClientRect();
      return { gap: b.left - a.right, offset: Math.abs(a.top + a.height / 2 - b.top - b.height / 2), labelWidth: label.getBoundingClientRect().width };
    });
    assert.ok(aligned.gap >= 6 && aligned.gap <= 10 && aligned.offset <= 2, prefix + ': checkbox and text alignment at ' + width);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), prefix + ': page overflow at ' + width);
    if (prefix === 'git') {
      const areas = await workspace.locator('.resizable-results').evaluate(root => {
        const a = root.querySelector('.result-sidebar').getBoundingClientRect(), b = root.querySelector('.result-content').getBoundingClientRect();
        return { sideBySide: b.left >= a.right, stacked: b.top >= a.bottom };
      });
      assert.ok(width > 720 ? areas.sideBySide : areas.stacked, 'Git result layout at ' + width);
    }
    await workspace.locator('.resizable-results').screenshot({ path: path.join(output, prefix + '-results-' + width + '.png') });
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
}
