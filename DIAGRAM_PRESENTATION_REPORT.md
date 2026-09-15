# 다이어그램 표시 개선 · internal.9

## 반영 범위

1. 흐름/영향도 마름모에 의미 설명과 실제 조건식을 함께 표시한다. C++/C# 정적 구조와 AI 설명 모두 원식·코드 근거를 유지한다.
2. AI/Code 함께 보기의 체크박스와 텍스트를 한 행에 정렬한다.
3. Git 결과는 왼쪽 검색·형식/그룹/AI·Code/페이지 트리와 오른쪽 선택한 그림으로 표시한다. 중복 선택 행을 제거하고 옵션·요약·근거를 접는다. 경계 너비를 조절하며 좁은 화면은 세로로 배치한다.
4. Sequence 반환값 대입은 괄호와 C++/C# 형 변환을 포함해 해당 응답에 병합한다. 같은 분기의 추가 내부 처리는 요약 메모로 표시하며 원문과 각 근거는 보존한다. 다른 분기의 같은 이름 호출·서로 다른 대입은 유지한다.
5. 실행 내용 없는 평가 분기를 렌더링에서 생략하고 한쪽 경로만 있으면 조건 방향을 유지한 opt로 표현한다. 원본 실행 구조는 보존한다.
6. 자연어·코드·Git의 캔버스에서 Ctrl+휠은 확대/축소, 일반 휠은 스크롤한다. 배율 선택과 확대·축소 버튼도 유지한다.
7. Sequence 글꼴을 실측해 참가자 폭과 중첩 간격을 확보한다. 긴 문장은 공백에서 줄바꿈하고 식별자를 한 글자씩 쪼개지 않는다. 표시용 줄바꿈은 저장한 Mermaid 원문에 쓰지 않는다.

## 호환 및 배포 범위

- `DiagramNode.originalExpression`은 선택 필드다. 기존 JSON 저장소를 읽을 수 있으며 마이그레이션은 필요하지 않다.
- 새 생성은 `diagram-presentation-v2`, `shared-semantic-v6`으로 캐시를 구분한다. 기존 원본·편집본을 일괄 덮어쓰지 않는다. 새 내용은 새 생성 또는 선택 재생성에 적용된다.
- 이전 세션의 품질·사용성 변경도 보존하고 함께 배포한다. 상세는 [품질 개선 보고서](DIAGRAM_QUALITY_IMPROVEMENT_REPORT.md)를 참조한다.
- loopback 기본값, 저장소 ACL, 출처 검증, 비밀 마스킹, 외부 추론 금지 정책을 유지한다.

## 검증 기록

- .NET 334개, Git 작업자 43개, 웹 56개 통과. 조건 원식 보존, C++/C# 대입 병합, 분기 간 분리, 근거 유지, 빈 제어 블록 회귀를 포함한다.
- Sequence 긴 조건·중첩 alt·메모 분기·loop/opt/break·긴 식별자 4개 사례의 텍스트 겹침, 구분선 침범, SVG 잘림 검사를 통과했다. SVG·PNG 및 화면 캡처는 `artifacts/sequence-layout/`에 있다.
- 전체 `verify.ps1`이 **실제 종료 코드 0**으로 완료됐다(2026-09-15 17:11:39–17:20:38 KST).
  정책 단위 13개·시작 정책 11개, API 두 모드, 공유 의미 기본·60,000자, SVG 6개,
  코드·디자인 UI와 Windows CMD 14개 검사를 포함한다. 미해결 로컬 실패는 없다.
- 현재 소스 284개가 검증 작업본과 SHA-256으로 일치하며 10개 픽스처의 증적 133개를 보존했다.
  [전체 로그](artifacts/display-improvements/verify.log), [실제 종료 결과](artifacts/display-improvements/verify-result.json),
  [소스 해시](artifacts/display-improvements/verified-source-hashes.json), [증적 목록](artifacts/display-improvements/evidence-manifest.json).
- 직접 확인한 화면: [Git 좌우 결과](artifacts/display-improvements/evidence/shared-semantics-oeGmxU/git-results-1440.png),
  [Git 모바일](artifacts/display-improvements/evidence/shared-semantics-oeGmxU/git-results-390.png),
  [코드 조건식·모바일](artifacts/display-improvements/evidence/code-block-ui-ujr201/code-results-390.png),
  [Sequence 중첩 배치](artifacts/display-improvements/evidence/sequence-layout/nested-preview.png).
- 배포 패키지 검증과 해시, Git push 결과는 완료 후 아래에 기록한다.

합성 loopback LLM은 계약·검토·복구·UI 검증에 사용한다. 실제 사내 LLM 의미 품질·처리 시간과 PostgreSQL 연결은 이번 로컬 검증에 포함하지 않는다.
