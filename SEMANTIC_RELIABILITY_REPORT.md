# 의미 분석·렌더링 개선 검증 결과

2026-09-10. 승인 계획의 구현과 로컬 검증을 완료하고 사내 시험 배포 `0.1.0-internal.5`를 만들었다.
사용자 지시에 따라 이전 internal.4 및 semantic.1 ZIP/SHA를 삭제하고 새 배포본만 남겼다.
사용자가 이 PC에서는 사내 LLM 검증이 불가능하다고 명시했으므로 실제 모델 품질은 미검증이다.

## 적용한 동작

- 코드 입력은 블럭당 100,000자, 전체 1,000,000자, 최대 20블럭이다. 서버 한도를 화면에도 적용하고,
  초과하여 붙여넣은 원문은 자르지 않으며 한도 초과 상태에서 저장·생성을 차단한다.
- Mermaid의 안전한 CSS 규칙과 SVG 정의를 보존하여 색상·선·텍스트가 사라지는 문제를 수정했다.
  표시와 SVG/PNG 내보내기에 같은 정제 경로를 사용하고 외부 URL·스크립트·이벤트·삽입용 CSS는 제거한다.
- 입력 토큰과 출력 예약을 합산하여 문맥 한도를 검사한다. 기본 입력/전체 문맥 상한은 200,000토큰,
  출력 상한은 60,000토큰이다. 서버 토큰 계산을 사용할 수 없으면 보수적인 추정치임을 표시한다.
- 구조화 출력 미지원 응답이 명확할 때만 `structured_outputs` → `response_format` → JSON 지시문으로
  협상한다. 모든 모드에 동일한 계약·코드 근거 검증을 적용하고, 잘린 응답이나 일반 오류를 성공으로 처리하지 않는다.
- 수정 요청에 거부된 응답과 검증 피드백을 포함한다. 짧은 근거 ID를 복원하고, 코드에서 확인되는
  관련 심벌에 한해 추가 문맥을 한 번 제공한다. 큰 함수·페이지·Git 변경 문맥을 완전한 단위로 분할하며
  근거·원문 위치·실제 관계를 보존한다. 분할할 수 없는 단위는 명시적인 미완료 결과로 남긴다.
- 코드 블럭과 Git 분석에서 검증한 요약 페이지부터 저장·공개한다. 기본 900초 예산, 진행 표시,
  취소·이어하기, 완료 단위 체크포인트를 연결했다. 변경된 입력·설정은 해당 체크포인트를 재사용하지 않는다.
- 작업 리비전과 lease를 확인하여 중복 재개 및 오래된 작업자의 쓰기를 거부한다. 코드 블럭의 이어하기는
  기존 생성 이력을 보존하는 새 실행이며, Git 분석은 같은 작업을 원자적으로 다시 대기 상태로 전환한다.
- 작업 진단 다운로드에는 단계·시간·토큰·전송 상태·오류 코드만 포함한다.
  원문, 프롬프트, 모델 응답, 체크포인트 값, 실제 서버 주소는 포함하지 않는다.

## 검증 결과

OneDrive 실행 차단 정책을 유지하고 `%TEMP%/DiagramMaker-internal5-20260910`의 비동기화 worktree에서 실행했다.
패키지 소스 커밋은 `7bb42f689f6fc169b7fa5b2516bc2af144171cd8`이다.
깨끗한 체크아웃에서 시작 정책 검사가 이전 웹 빌드에 의존하던 문제를 보완한 뒤 전체 검증을 통과했다.

| 검사 | 결과 |
| --- | --- |
| `scripts/verify.ps1 -UseInstalledDependencies` | exit 0, .NET 180/180, worker 32/32, web 46/46 |
| 정책·시작·라이선스 | 정책 회귀 7종, 시작 검사 11종, 검토된 의존성 및 SBOM 통과 |
| 전체 API·화면 | 기본/선택 정책 API, SVG 색상·보안 6종, Edge UI 통과 |
| 추가 HTTP·다운로드 | 취소/재개, 중복 재개 409, 삭제 후 진단 404, UI 진단 다운로드 통과 |
| 실제 VllmClient + 합성 HTTP 응답 | 예산 중단, LocalFile 재시작, 완료 요청 재호출 없이 나머지 생성, 소유자/동시성 검증 통과 |
| 큰 입력 | 610개 호출 함수 분할, 한글/CRLF/이모지, 원문 해시·호출 위치, 추가 문맥 제한 통과 |
| 실제 Windows 패키지 | 내장 x64 Node의 C/C++ 처리, 코드 블럭 5종 API, 프리셋 4종 × 화면 폭 4종, 편집/SVG/PNG/진단 UI 통과 |
| 패키지 LLM 전송 | loopback 합성 서버로 기본·선택 정책 각 5종 통과 |
| CMD 실행기 | 기본 실행·선택 정책·누락/오류 정책 처리 6종 통과 |
| ZIP 감사 | 1,818개 파일의 빌드 결과 일치, 필수 파일·작업자·실행기·사내 시험 안내·SHA-256 확인 |

예산 중단 회귀는 합성 응답을 대기시킨 상태에서 테스트 예산을 1초로 줄여 수행했다.
실제 모델을 900초 동안 실행한 시험으로 해석하지 않는다. 화면은 390/800/1440px 캡처를 포함한다.

검증 로그와 결과·화면은 [artifacts/internal5-validation](artifacts/internal5-validation/)에 보존했다.
주요 증적은 [전체 검증 로그](artifacts/internal5-validation/internal5-verify-final.log),
[패키지 API 결과](artifacts/internal5-validation/offline-preview-OAusA8/result.json),
[패키지 UI 결과](artifacts/internal5-validation/code-block-ui-b9fn8d/result.json),
[CMD 실행기 결과](artifacts/internal5-validation/windows-launchers-Qw0HUY/result.json),
[ZIP 감사](artifacts/internal5-validation/package-audit.json)이다.
HTTP 취소·재개·중복 거부·진단 다운로드를 최종 전체 검증과 실제 패키지에서 확인했다.

## 사내 시험 패키지

- [DiagramMaker-0.1.0-internal.5-win-x64.zip](artifacts/release/DiagramMaker-0.1.0-internal.5-win-x64.zip)
- [SHA-256 파일](artifacts/release/DiagramMaker-0.1.0-internal.5-win-x64.zip.sha256)
- 크기: 94,167,458바이트
- SHA-256: `c71bfe6b35cdd71ce047766426039f5b2cd5e5482408fac74ceba310616ef2ed`

내부 manifest의 sourceCommit은 위 `7bb42f6` 커밋과 일치한다. sourceTreeDirty는 빌드가 기록한 `true`를 보존했다.
별도 감사에서 `git diff HEAD`가 비어 있고 미추적 파일은 생성한 ZIP/SHA 두 개뿐이며,
패키지의 작업자·실행기·시험 안내가 해당 커밋 작업본과 일치함을 확인했다.
이전 배포 ZIP/SHA 4개 삭제 내역은 [삭제 기록](artifacts/internal5-validation/removed-packages.json)에 있다.
기존 소스 체크포인트와 커밋 이력은 보존한다.

배포 커밋 `bdfe294ae29434f1785f6e4bae7ea04d4a430c5d`를 기존 `origin/main`에 일반 푸시했다.
원격 SHA 일치와 해당 커밋의 배포 폴더에 internal.5 ZIP/SHA만 있음을 확인했다.

ZIP을 OneDrive 등 동기화 경로 밖에 풀고 `configure-llm.cmd` → `start.cmd` 순서로 실행한다.
패키지에 포함된 `OFFLINE_INSTALL_KO.txt`와 [INTERNAL_TEST_KO.txt](packaging/windows/INTERNAL_TEST_KO.txt)는
설치, 고정 C#/C++ 코드, Thinking OFF 의미 품질, 큰 입력, 요약 공개, 이어하기와 진단 수집 절차를 제공한다.

## 남은 외부 검증

- 실제 사내 LLM 연결·의미 품질·실제 토큰 사용·처리 시간: 이 PC에서는 미실시.
- PostgreSQL 실연결과 동시성: 서버 환경이 없어 미실시. 구현 빌드 및 다른 저장소의 회귀 통과와 구분한다.
- 외부 취약점 피드와 호스트 전체 송신 캡처: 미실시. 오프라인 검증은 공개 audit 서비스를 호출하지 않았다.

다음 환경 의존 작업은 사내 비동기화 경로에서 Thinking OFF로 고정 합성 사례를 3회 실행하고,
문제 코드·작은 Git·큰 Git 사례의 의미, 원본 근거, 요약 공개 시점, 중단 후 재사용을 확인하는 것이다.
민감정보 없는 작업 진단으로 결과를 기록한다. 현재 해결되지 않은 로컬 검증 실패는 없다.
