import assert from 'node:assert/strict';
import test from 'node:test';
import { assignChange, sidebarWidth, emptyGroupMemory, rememberGroups, restoreChange, forgetGroups } from '../src/analysisSelection.ts';
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

test('reselecting restores the removed group and its settings without selecting its unchecked siblings', () => {
  const original = [
    { id: 'a', title: '앞 그룹', changeIds: ['one'] },
    { id: 'b', title: '복원할 그룹', changeIds: ['two', 'three'], views: [{ id: 'v', presetId: 'detailed', diagramType: 'sequence' }] },
    { id: 'c', title: '뒤 그룹', changeIds: ['four'] },
  ];
  let memory = rememberGroups(emptyGroupMemory(), original);
  let groups = assignChange(assignChange(original, 'two'), 'three');
  memory = rememberGroups(memory, groups);
  groups = restoreChange(groups, 'three', JSON.parse(JSON.stringify(memory)), { id: 'unused', changeIds: [] });
  assert.deepEqual(groups.map(g => g.id), ['a', 'b', 'c']);
  assert.deepEqual(groups[1], { ...original[1], changeIds: ['three'] });
  groups = restoreChange(groups, 'two', memory, { id: 'unused', changeIds: [] });
  assert.deepEqual(groups[1].changeIds, ['three', 'two']);
});

test('explicit moves, merges and deletes determine the next restore target', () => {
  let groups = [{ id: 'a', changeIds: ['one', 'two'] }, { id: 'b', changeIds: ['three'] }];
  let memory = rememberGroups(emptyGroupMemory(), groups);
  groups = assignChange(groups, 'one', 'b');
  memory = rememberGroups(memory, groups);
  groups = assignChange(groups, 'one');
  assert.deepEqual(restoreChange(groups, 'one', memory, { id: 'unused', changeIds: [] })[1].changeIds, ['three', 'one']);
  memory = forgetGroups(memory, ['b'], 'a');
  assert.equal(memory.assignments.one, 'a');
  assert.equal(memory.groups.b, undefined);
  memory = forgetGroups(memory, ['a']);
  assert.equal(memory.assignments.one, undefined);
  assert.equal(restoreChange([], 'one', memory, { id: 'new', changeIds: [] })[0].id, 'new');
});
