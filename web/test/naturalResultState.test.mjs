import assert from 'node:assert/strict';
import test from 'node:test';
import { naturalResultState, naturalResultCounts } from '../src/naturalResultState.ts';

test('three healthy pages do not inherit the two failed pages or parent warning', () => {
  const pages = Array.from({ length: 5 }, (_, i) => ({ id: String(i), state: i < 3 ? 'Completed' : 'Failed',
    diagram: i < 3 ? { id: String(i) } : undefined, errorMessage: i >= 3 ? 'own failure' : undefined }));
  const view = { state: 'Partial', diagram: pages[0].diagram, errorMessage: 'first failure', pages };
  for (const page of pages.slice(0, 3)) assert.equal(naturalResultState(view, page).error, undefined);
  for (const page of pages.slice(3)) {
    const state = naturalResultState(view, page);
    assert.equal(state.diagram, undefined);
    assert.equal(state.label, '생성된 결과 없음');
    assert.equal(state.error, 'own failure');
  }
  assert.deepEqual(naturalResultCounts([view]), { completed: 3, partial: 0, failed: 2 });
});

test('retained artifact belongs to selected page; legacy view remains readable', () => {
  const view = { diagram: { id: 'other-page' }, designQuality: { status: 'Reviewed' } };
  const page = { state: 'Failed', lastSuccessfulDiagram: { id: 'own-previous' } };
  assert.equal(naturalResultState(view, page).diagram.id, 'own-previous');
  assert.equal(naturalResultState(view, page).quality, undefined);
  assert.equal(naturalResultState(view).diagram.id, 'other-page');
});

test('partially reviewed current output is distinguished from a retained previous result', () => {
  assert.equal(naturalResultState(undefined, { state: 'Partial', diagram: { id: 'current' } }).label,
    '생성 부분 완료 · 검토 필요');
  assert.equal(naturalResultState(undefined, { state: 'Failed', lastSuccessfulDiagram: { id: 'previous' } }).label,
    '생성 미완료 · 이전 정상 결과 표시');
});
