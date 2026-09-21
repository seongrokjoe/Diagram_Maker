# 자연어 생성 복구·진단 검증 (internal.12)

2026-09-21, 기준 커밋 `516a579`. 승인 범위는
[구현 계획](NATURAL_DIAGRAM_RECOVERY_PLAN.md)에 기록했다.
기존 internal.11 배포와 기능 구현 전 checkpoint는 보존한다.

## 변경 동작

- 요구사항 추출과 원문 검토는 각각 최대 3회다. 검토 계약 오류는 검토만 재시도하며,
  저장된 추출 결과와 재개 전 보정 횟수를 보존한다.
- 원문 ID를 지정한 수정 요청은 해당 요구사항만 수정 가능하게 한다. 정상 항목과 ID는
  보존하며 시나리오 연결 오류와 잘못 바뀐 ID를 별도로 진단한다.
- 호환 스키마를 전송하더라도 원래 스키마의 필수 필드·자료형·열거값·길이 제한을
  로컬에서 검사한다. 원문/응답은 진단에 포함하지 않는다.
- HTTP 요청, 응답 내용 검증, 단계 종료를 구분하고 마지막 실패 원인과 복구 상태를
  마감한다. 정상 null은 해당 없음, 구형 누락은 미기록으로 표시한다.
- 자연어 화면은 전체 오류 목록과 JSON/텍스트 진단을 제공한다. 보정 소진은 이어하기를
  차단하고 새 실행을 안내한다. 이전 정상 결과는 유지한다.
- 관리자 전용 자연어 합성 검사 API·화면·`test-natural-diagram.cmd`를 추가했다.
  짧은 승인 흐름과 표·조건·오류·공통 인터락의 네 형식을 실제 생성 경로로 검사한다.
  Thinking OFF, 전체 예산 최대 900초, 임시 저장소 사용으로 사용자 작업을 남기지 않는다.
- 생성 정책은 `natural-v8`, 계약은 `natural-design-v4`다. 기존 저장 결과 조회는 유지한다.

## 검증 결과

비동기화 사본 `%TEMP%/DiagramMaker-natural-internal12`에서 사전 반입된 의존성과
loopback 합성 LLM만 사용했다. 외부 의존성 다운로드는 하지 않았다.

- 관련 .NET 97개 통과. 새 회귀 14개는 근거 실패, 검토 계약 오류 7종, 재개 횟수 보존,
  원문 ID 수정·정상 항목 보존, 시나리오 오류, ID 변경, 호환 스키마 길이 검증을 포함한다.
- 자연어 API/Edge 회귀 `natural-reliability-3eMvo3`: 16개 검사, HTTP 117회, 17.442초.
  관리자 ACL, 검토 독립 보정·소진·중단 재개, 네 형식, 분할·선택 상세 재생성,
  질문 저장·재시작·stale 답변 거부, 390/1440px 오류 목록·검사 화면·다운로드를 확인했다.
- 새 검사 API가 비관리자 요청을 500으로 반환하던 문제를 명시적 403 응답으로 수정했다.
- esbuild 상위 디렉터리 읽기 제한은 비동기화 사본에서 확장 권한 검증으로 해결했다.
- 전체 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1` exit 0.
  .NET 384, 작업자 43, 웹 62, 정책 13, 시작 정책 11개 통과. API 두 모드,
  공유 의미 기본/60,000자, SVG 6, Sequence 4, 코드·디자인·자연어 UI, CMD 14개 통과.
  최종 자연어 회귀는 `natural-reliability-Ha1PTW`, 실행기는 `ui-test-launchers-00x4dU`다.
- 최종 소스 자연어 회귀는 16개 검사·117회 요청·18.808초, 패키지 회귀
  `natural-reliability-qipoCe`는 같은 16개·117회·21.947초다. 합성 서버의 수치이며
  실제 모델의 품질이나 처리 시간 지표로 해석하지 않는다. 패키지 보고서 버전도 검증했다.
- 처음 전체 실행은 PowerShell `*>` 로그 수집이 일반 빌드 stderr를 오류로 처리해 중단됐다.
  CMD 리디렉션으로 전체 검증을 재실행해 실제 exit 0을 확인했다.

## 배포 및 한계

배포 빌드 exit 0. ZIP은 96,495,442바이트, 1,832개 파일이며 모든 파일을 stage와
SHA-256으로 대조했다. 검증 사본에는 Git 메타데이터가 없으므로 원본과 소스 해시를
대조한 후 manifest에 기준 커밋 `516a579`와 `sourceTreeDirty=true`를 기록했다.
보고서의 Build 값은 publish에 지정한 패키지 버전을 사용한다.

ZIP SHA-256: `ce2b04aa4b2d7a7c73ae73a72a840c4233708f6b7f9e63ce46ec691b7ddb1a5f`.
패키지 검사 9개 명령 모두 exit 0으로 완료했다.

| 검증 | 결과/증적 |
|---|---|
| API·4종 미리보기·4개 화면 너비 | 통과, `offline-preview-QRPMur` |
| 코드·디자인 UI | 통과, `code-block-ui-T2rXcQ`, `design-ui-10yLGP` |
| 자연어 API/UI·관리자 검사 | 통과, `natural-reliability-qipoCe` |
| 합성 LLM 제한/기본 모드 | 각 5개 통과, `packaged-llm-SbPD3F`, `packaged-llm-9ErnJE` |
| 공유 의미 기본/60,000자 | 통과, `shared-semantics-FdTZvB`, `shared-semantics-VZ9uVC` |
| Windows CMD | 8개 통과, `windows-launchers-JVoVhy` |

배포 파일: [internal.12 ZIP](artifacts/release/DiagramMaker-0.1.0-internal.12-win-x64.zip),
[SHA-256](artifacts/release/DiagramMaker-0.1.0-internal.12-win-x64.zip.sha256).
최종 소스 301개를 검증 사본과 해시로 대조했다. `git diff --check` 통과.
미해결 로컬 검증 실패 없음. 검증 로그·대표 화면은 로컬 `artifacts/internal12-validation/`에 보존했다.
기존 서버 종료, data·설정 백업 후 새 비동기화 폴더에 설치한다. 세부 절차는 동봉
`OFFLINE_INSTALL_KO.txt`, `INTERNAL_TEST_KO.txt`의 자연어 생성 검사 항목을 따른다.

사내 실제 모델의 기존 실패 입력과 의미 품질·응답 시간은 미검증이다. PostgreSQL 실연결·동시성,
호스트 egress 캡처와 외부 취약점 피드 역시 미수행이다. 합성 회귀로 이 항목을 대체하지 않는다.
