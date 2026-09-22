# 자연어 추출·검토 개선 검증 (internal.13)

2026-09-22, 기준 `8cc4f1b`. 승인 범위: `NATURAL_DIAGRAM_EXTRACTION_PLAN.md`.

## 변경 동작

- 모델 추출 계약에서 origin/인용문을 제거하고 검증된 원문 근거로 서버가 저장 계약을 구성한다.
  설계 가정은 설계 단계에서 표시하며 원문 의미 검토를 유지한다.
- 검토 사유는 누락/조건 변경/근거 없는 주장/근거 불일치/엔터티 불일치/시나리오 불일치로
  구분한다. 모든 사유가 원문 근거를 지목하며 수정 지시는 진단에 노출하지 않는다.
- 추출 최대 3회, 후보별 검토 계약 최대 3회. 단위당 최대 12 논리 호출이며 기존 전체 예산을
  유지한다. 정상 항목을 보존하고 대상 항목을 교체한다. 무변화 후보 검토는 재사용한다.
- 교체/보존/거부/근거 통과 수치, 구체적 종료 사유와 사내에서 옮겨 적을 수 있는 요약을 제공한다.
- 버전 natural-v9/natural-design-v5. 공개 생성 API 및 기존 결과/편집을 보존한다.

## 검증 진행

- 기존 구현에서 회귀 2개 실패를 확인했다: 동일 ID 보정 무시, 검토 형식 보정 뒤 의미 보정 차단.
- 수정 후 관련 .NET 101개 통과. 횟수 상한 12회, 동일 후보 검토 재사용, 재개 횟수 보존,
  6종 오류별 보정, 고정 사내 입력 2종과 비민감 요약을 포함한다.
- 비동기화 사본 `%TEMP%/DiagramMaker-natural-internal13`에서 사전 반입 캐시만 사용한다.
  esbuild 상위 폴더 읽기 제한은 확장 권한 오프라인 빌드로 해결했다.
- 2026-09-22 재개 시 검증 사본에 빠진 마지막 수정 5개를 반영했다. 전체 검증에서
  .NET 399, 작업자 43, 웹 62, 정책 13, 시작 정책 11개 및 프런트엔드 빌드가 통과했다.
- 전체 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1` 최종 exit 0.
  API 두 모드, 공유 의미 기본/60,000자, SVG 6, Sequence 4, 코드·디자인·자연어 UI,
  Windows UI 실행기 14개 검사 통과 (`ui-test-launchers-or8xq5`).
- 자연어 API/Edge 회귀 `natural-reliability-4ehEld`: 20개 검사·144회 합성 HTTP 요청·20.578초.
  실패 사례별 요약 분리, 무변화 보정 종료, 390/1440px 요약·복사·다운로드를 확인했다.

## 배포 패키지 검증

- `build-offline-win-x64.ps1 -Version 0.1.0-internal.13 -SkipTests` exit 0.
  .NET 전체 회귀는 바로 앞의 verify에서 통과했고 패키징의 작업자·웹 테스트도 통과했다.
- 원본과 검증 사본의 소스·빌드 설정 파일 304개 SHA-256 일치. 검증 사본에는 Git 메타데이터가
  없으므로 해시 대조 후 manifest에 기준 커밋 `8cc4f1b`와 `sourceTreeDirty=true`를 반영했다.
- ZIP 96,508,426바이트, 1,832파일. ZIP의 모든 파일을 stage와 SHA-256으로 대조했다.
  SHA-256: `9778ffbeefec6f390fa56e62831a980ecc615a80edec153ca4cc8173e2790edd`.
- 검증 보조 스크립트의 PowerShell ZIP 어셈블리 로딩 오류는 수정 후 해시 검증을 통과했다.
  제품 빌드 실패와 구분한다.
- 패키지 자연어 회귀 `natural-reliability-SeeE7O`: 20개 검사·144회 합성 HTTP 요청·24.876초.
  보고서와 화면의 Build 값이 `0.1.0-internal.13`임을 확인했다. 이 시간은 합성 서버의 수치이며
  실제 모델의 품질이나 응답 시간 지표가 아니다.

| 패키지 검증 | 결과/증적 |
|---|---|
| API·4종 미리보기·4개 화면 너비 | 통과, `offline-preview-ZUfgL3` |
| 코드·디자인 UI | 통과, `code-block-ui-j6jcSs`, `design-ui-EJwrBZ` |
| 자연어 API/UI·관리자 검사 | 통과, `natural-reliability-SeeE7O` |
| 합성 LLM 제한/기본 모드 | 각 5개 통과, `packaged-llm-oETH0u`, `packaged-llm-qDPFwV` |
| 공유 의미 기본 | 통과, `shared-semantics-finuYE` |
| 공유 의미 60,000자 | 통과, `shared-semantics-0DCAl1` |
| Windows CMD | 8개 통과, `windows-launchers-WsLvVn` |

패키지 검사 9개 명령 모두 exit 0. 배포 파일은
[internal.13 ZIP](artifacts/release/DiagramMaker-0.1.0-internal.13-win-x64.zip)과
[SHA-256](artifacts/release/DiagramMaker-0.1.0-internal.13-win-x64.zip.sha256)이다.
원본 저장소로 복사한 ZIP의 해시도 확인했다. 검증 당시 보존했던 internal.11/internal.12 ZIP과
각 SHA-256 파일은 2026-09-22 사용자 요청으로 제거했다. 현재 배포 폴더에는 internal.13만 유지하며
이전 버전은 Git 이력에 남아 있다.

로그·명령별 종료 코드·소스 해시·ZIP 무결성 기록·대표 화면은
로컬 `artifacts/internal13-validation/`에 보존했다. 로컬 화면 증적:
[모바일 검사 요약](artifacts/internal13-validation/package-natural/natural-self-test-390.png),
[데스크톱 검사 요약](artifacts/internal13-validation/package-natural/natural-self-test-1440.png).
로컬 구현·전체 검증·패키징 완료, 미해결 로컬 실패 없음. 기존 변경과 checkpoint를 보존했고
사용자 요청에 따라 소스·배포·문서 및 이전 ZIP 정리를 커밋·푸시한다.

## 사내 확인

사용자가 internal.12 사내 내장 검사 실패를 확인했다. 실제 응답 본문은 반출하지 않는다.
internal.13 설치 후 ‘LLM 점검 → 자연어 생성 검사 → 검사 요약 복사’의 요약을 전달한다.
내장 두 사례와 기존 실패 입력의 캐시 없는 재생성 3회 및 주요 조건 보존을 확인한다.
로컬 합성 회귀는 사내 모델의 의미 품질을 증명하지 않는다. 실제 모델과 PostgreSQL 실연결은
이 환경에서 검증하지 않았으며, 사내 확인 전 상태는 ‘실제 모델 확인 대기’다.
호스트 egress 캡처와 외부 취약점 피드도 미수행이다. 이 항목들을 합성 검증 통과로 대체하지 않는다.
