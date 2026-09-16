import assert from 'node:assert/strict';
import test from 'node:test';
import { withViewDirection, fitZoom } from '../src/diagramViewSettings.ts';
import { diagramName, matchesDiagramName, originOfPage, resultCountsText } from '../src/diagramOrigin.ts';
import { mermaidSafetyError, maximumMermaidCharacters } from '../src/mermaidSafety.ts';

test('view directions preserve diagram labels and sequence time order', () => {
  const source = 'flowchart LR\n  n["direction LR · 한글 레이블"]\n  n --> m\n';
  assert.equal(withViewDirection(source, 'TB', 'flowchart'), source.replace('flowchart LR', 'flowchart TB'));
  assert.equal(withViewDirection(source, 'original', 'flowchart'), source);
  const sequence = 'sequenceDiagram\n  A->>B: direction LR';
  assert.equal(withViewDirection(sequence, 'TB', 'sequence'), sequence);
  assert.equal(fitZoom(2000, 1000, 800, 600), 0.4);
  assert.equal(fitZoom(100, 50, 800, 600), 1);
});

test('corresponding diagrams share a searchable base name', () => {
  assert.equal(diagramName('AI_연결 확인', 'code'), 'Code_연결 확인');
  assert.equal(matchesDiagramName('Code_연결 확인', ' 연결 '), true);
  assert.equal(matchesDiagramName('Code_연결 확인', '없는 함수'), false);
});

test('large source diagrams have a bounded render budget', () => {
  assert.equal(mermaidSafetyError('flowchart LR\n' + ' '.repeat(60_000)), null);
  assert.match(mermaidSafetyError(' '.repeat(maximumMermaidCharacters + 1)), /크기 한도/);
});

test('compact page metadata preserves AI origin when explanation bodies are omitted', () => {
  const diagram = { ir: { provenance: [] } };
  assert.equal(originOfPage({ diagram, resultKind: 'semantic' }), 'ai');
  assert.equal(originOfPage({ diagram, resultKind: 'static' }), 'code');
  assert.equal(originOfPage({ diagram: { ...diagram, explanation: { status: 'Semantic' } } }), 'ai');
  assert.equal(originOfPage({ diagram }), 'code');
});

test('partially reviewed AI remains an AI result and is counted separately', () => {
  const diagram = { ir: { provenance: [] }, explanation: { status: 'Incomplete', coverage: { verifiedUnits: 2 } } };
  assert.equal(originOfPage({ diagram }), 'ai');
  assert.match(resultCountsText({ aiCompleted: 1, aiPartial: 2, aiFailed: 3, aiPending: 4, codeCompleted: 5 }),
    /AI 완료 1개 · AI 부분 2개 · AI 실패 3개/);
});
