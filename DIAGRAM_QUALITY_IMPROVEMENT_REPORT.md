# 다이어그램 품질·사용성 개선 검증 기록

기준: `741b2a4` / internal.8. 승인 범위: `DIAGRAM_QUALITY_IMPROVEMENT_PLAN.md`.

## 구현

- 오류 목록·상세 높이를 287px로 제한하고 행 간격을 줄였다. 접기와 선택 상태를 유지한다.
- 배율을 1–1600%로 제공하고 맞춤 배율부터 연속 조절한다. 보기 방향을 도구 모음 첫 묶음으로 배치했다.
- 결과 왼쪽 영역의 글꼴을 줄이고 220–560px 너비 조절, 키보드 조작, 브라우저 설정 저장을 연결했다.
- Git 변경점의 마지막 항목을 해제하거나 이동하면 빈 그룹을 제거한다. 새로 만든 빈 초안은 유지한다.
- 의미 요청에 항목별 함수·위치·제어 문맥·정의와 Git 전후 원문을 연결하고 중복 원문을 줄였다.
- 비교식·제네릭·인라인 강조를 처리하면서 위험한 표시 문법을 차단한다. 실패 항목의 보정과 전체 의미 검토를 유지한다.
- 공통 문자·토큰 예산, 서버 토큰 계산과 캐시, 순차 요청, 영향 페이지 갱신 및 단계별 시간을 연결했다.
- 자연어 요구사항을 한 번 추출한 뒤 형식별 구조 설계·독립 검토·보정 1회를 수행한다. 요구사항·가정·검토 상태를 저장하고 재생성 실패 시 마지막 정상 결과를 보존한다.
- 자연어 클래스 포함/집합 관계와 상태 시작/종료 표시를 보완했다. 프리셋 적용 시 노드·관계를 임의로 잘라내지 않는다.

## 합성 통합 비교

동일한 1,212행/40함수 입력에서 모든 결과 페이지와 의미 검토를 유지한다.

| 생성 대상 | internal.8 요청 수 | 개선 후 요청 수 | 결과 페이지 |
| --- | ---: | ---: | ---: |
| 코드 Flow | 8 | 8 | 41 |
| 코드 3종 | 14 | 10 | 43 |
| Git 3종 | 28 | 20 | 51 |

코드 3종·Git 3종 요청 수는 약 29% 감소했다. 이 값은 합성 응답 서버의 계약·요청 수 검사 결과다.
실제 동일 사내 모델 3회 중앙값의 처리 시간 30% 개선 목표는 사내 측정 전까지 미검증이다.

## 검증 상태

최종 전체 `verify.ps1`이 **실제 종료 코드 0**으로 완료됐다. 실행 시간은
2026-09-15 12:31:39–12:39:22 KST이며 중단된 세션 이후 남은 종료 기록을 재개 세션에서 확인했다.
현재 소스 278개가 검증 작업본과 SHA-256으로 일치한다. 미해결 로컬 빌드·회귀 실패는 없다.

| 검사 | 결과 |
| --- | --- |
| .NET / C++ 작업자 / 웹 단위 | 320 / 43 / 53개 통과 |
| 정책·폰트·실행기 단위 | 13개 통과 |
| 프런트엔드 빌드·잠금 의존성·라이선스 | 통과, npm 268개 / NuGet 24개 / SBOM 293개 |
| 시작 정책 / API | 11개 / 일반·기본 2모드 통과 |
| 공유 의미·AI/Code 원본·편집·화면 | 기본·60,000자 한도 모두 통과 |
| SVG 보안·색상·PNG | 6개 통과 |
| 코드 블럭·자연어·Git UI | 통과, 너비·배율 조절과 새로고침 복원 포함 |
| 실제 Windows CMD 실행기 | 14개 통과 |

- .NET 검사에는 332항목의 누락·중복 없는 생성/검토 회귀가 포함된다.
- 자연어 4종은 공유 요구사항 추출 1회와 형식별 설계·검토로 총 9회 요청한다. 선택한 형식의 검토 실패/보정은 4회 추가하며 다른 형식과 마지막 정상 결과를 보존한다.
- 명령: 비동기화 검증 작업본에서 `powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1`.
  실행 도우미는 `artifacts/quality/finish-verification.ps1` → `artifacts/ai-code/run-checks.ps1 -Mode verify`다.
- [최종 종료 결과](artifacts/quality/verify-quality-result.json), [전체 로그](artifacts/quality/verify-quality.log),
  [표준 오류 로그](artifacts/quality/verify-quality.stderr.log)를 보존했다.
- [소스 해시 278개](artifacts/quality/verified-source-hashes.json)와
  [증적 목록](artifacts/quality/evidence-manifest.json)에 9개 픽스처, 캡처 66개, 결과 JSON 7개,
  합성 자체 점검 텍스트 7개, CMD 로그 20개의 해시를 기록했다. API 2모드의 성공은 전체 로그에 기록되어 있다.
- 기본 공유 검사 `shared-semantics-TDYwZu`, 60,000자 검사 `shared-semantics-i8cjM0`에서
  위 요청 수와 결과 페이지 수를 동일하게 확인했다.
- `git diff --check`도 통과했다. 이 재개 세션에서는 검증 증적과 문서를 마감했으며 새 배포 ZIP·커밋·푸시는 수행하지 않았다.

직접 확인한 최종 화면:

- [오류 진단 1440px](artifacts/quality/evidence/code-block-ui-KJZ8l1/semantic-progress-1440.png)
  · [390px](artifacts/quality/evidence/code-block-ui-KJZ8l1/semantic-progress-390.png): 목록·상세 높이와 모바일 내부 스크롤.
- [왼쪽 영역 300px 조절](artifacts/quality/evidence/code-block-ui-KJZ8l1/resized-results.png): 결과 트리와 보기 도구 배치.
- 자연어 [Class](artifacts/quality/evidence/shared-semantics-TDYwZu/natural-class.png)
  · [Flow](artifacts/quality/evidence/shared-semantics-TDYwZu/natural-flowchart.png)
  · [Sequence](artifacts/quality/evidence/shared-semantics-TDYwZu/natural-sequence.png)
  · [State](artifacts/quality/evidence/shared-semantics-TDYwZu/natural-state.png): 멤버·메서드, 조건 분기, alt, 시작·종료·가드.
- [Git 결과 390px](artifacts/quality/evidence/design-ui-7YHSkL/git-results-390.png): 결과 트리와 보기 도구의 모바일 배치.

## 재개 중 해결한 검사 실패

- 이전 실행의 공유 배치 상한 검사가 새 26개 상한을 반영하지 못했다. 최신 상한으로 모든 항목과 검토 수를 다시 확인했다.
- 문자 예산 호환성 테스트에 공통 메시지 오버헤드 64자가 빠져 있었다. 공통 계산식을 사용하고, 호환 모드 2회 전송 뒤 과대 JSON 프롬프트 전송을 차단하는 검사를 유지했다.
- 너비 설정의 새로고침 검사를 추가하면서 생성 HTTP 응답을 탐색 이후에 읽게 됐다. 응답 본문을 즉시 보관하도록 고쳤으며 전체 코드 UI 재검증을 통과했다.

## 호환성·검증 범위

새 요구사항·검토·단계 시간 필드는 선택 필드이며 기존 결과·저장·편집 경로를 유지한다. 생성 정책 버전을 갱신해 이전 의미 결과의 잘못된 재사용을 방지한다.
기존 체크포인트·사용자 변경·배포 ZIP을 보존한다. 실제 사내 LLM 품질·처리 시간, PostgreSQL 실연결, 취약점 피드와 호스트 외부 통신 캡처는 이 로컬 합성 검증에 포함되지 않는다.
다음 검증은 승인된 사내 환경에서 동일 모델 3회 중앙값과 의미 품질·PostgreSQL 실연결을 측정하는 것이다.
현재 소스 UI는 `start-ui-test.cmd`로 확인할 수 있다(이 실행기는 LLM 비활성).
