import assert from 'node:assert/strict';
import test from 'node:test';
import { assignChange, sidebarWidth } from '../src/analysisSelection.ts';
import { steppedZoom } from '../src/diagramInteraction.ts';

test('unchecking and moving the last Git change removes only the emptied group', () => {
  const groups = [{ id: 'a', changeIds: ['one'] }, { id: 'b', changeIds: ['two'] }, { id: 'draft', changeIds: [] }];
  assert.deepEqual(assignChange(groups, 'one').map(g => g.id), ['b', 'draft']);
  assert.deepEqual(assignChange(groups, 'one', 'b'), [{ id: 'b', changeIds: ['two', 'one'] }, groups[2]]);
  assert.deepEqual(assignChange(groups, 'one', 'missing'), groups);
  assert.deepEqual(groups[0].changeIds, ['one']);
  assert.equal(assignChange(assignChange(groups, 'one'), 'two').length, 1);
});
test('zoom crosses 100% and continues smoothly from a small fitted diagram', () => {
  assert.ok(steppedZoom(1, -1) < 1);
  assert.ok(steppedZoom(.04, 1) < .05);
  let zoom = 2;
  for (let i = 0; i < 50; i++) zoom = steppedZoom(zoom, -1);
  assert.equal(zoom, .01);
  assert.equal(steppedZoom(16, 1), 16);
});
test('sidebar preserves usable diagram space and bounds invalid preferences', () => {
  assert.equal(sidebarWidth(900, 1200), 560);
  assert.equal(sidebarWidth(500, 752), 400);
  assert.equal(sidebarWidth(NaN, 1200), 280);
  assert.equal(sidebarWidth(-1, 1200), 220);
});
