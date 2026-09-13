// Contract-only responses for the loopback fixtures. These do not assess model quality.
export function executionMeaningFixture(context) {
  const steps = context.steps;
  const referenced = new Set(steps.flatMap(s => [...s.childIds, ...s.alternativeIds, ...s.evaluationIds]));
  const regions = [steps.filter(s => !referenced.has(s.id)).map(s => s.id),
    ...steps.flatMap(s => [s.childIds, s.alternativeIds, s.evaluationIds])];
  const units = [], owned = new Set();
  for (const region of regions) {
    let chain = [];
    const flush = () => { if (chain.length) { units.push({ summary: '지역 값을 준비합니다',
      description: '원문에 명시된 순서로 값을 설정합니다', eventIds: chain }); chain = []; } };
    for (const id of region) {
      if (owned.has(id)) continue;
      owned.add(id);
      const step = steps.find(s => s.id === id);
      if (['declare', 'assign'].includes(step.kind)) chain.push(id);
      else { flush(); units.push({ summary: '원본 동작을 처리합니다', description: '원문 실행 사실을 보존하며 외부 구현은 미확인입니다', eventIds: [id] }); }
    }
    flush();
  }
  return { summary: '입력 조건을 확인하고 명시된 처리 결과를 반환합니다', basis: 'implementation', steps, units };
}
