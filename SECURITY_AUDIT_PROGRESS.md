# 사내 전용 보안·라이선스 정비 진행 기록

기준: 2026-09-09, HEAD e53efc2 및 기존 미커밋 코드 블록 기능 전체.
사용자 승인: 사내 사용, 개발/빌드/실행 외부 통신 차단, 외부 Git CLI 유지,
비동기화 경로 필수. 기존 checkpoint, 작업 변경, 과거 ZIP/시험 기록은 보존한다.

## 현재 단계

- [x] A0 읽기 감사와 기존 변경 백업. 과거 Codex 외부 추론 경로 및 배포 DLL 잔존 확인.
- [x] A1 Codex 실행 경로 제거, 사내 LLM/DB 연결 정책, 작업자 격리.
- [x] A2 비동기화 경로와 오프라인 개발/빌드/배포 정책 구현.
- [x] A3 라이선스 판정·고지 수집·SBOM·배포 검사 구현.
- [ ] A4 회귀 테스트·안전한 환경 검증·최종 감사 보고서.

## 검증 및 다음 작업

- 2026-09-10 전체 verify exit 0: .NET 163, worker 32, web 44, 정책 회귀 6,
  npm 268/NuGet 24 고지·해시 및 SBOM 292 구성요소 통과.
  시작 거부 5종 `internal-policy-KZyeEu`, API `api-smoke-5mUqKG`, Edge UI `code-block-ui-GJndSr` 통과.
  기본 샌드박스의 esbuild 상위 경로 접근 제한 후 동일 오프라인 검증을 확장 권한으로 통과했다.
  새 패키지 LLM 검사 스크립트는 기존 internal.2 EXE에서 연결·DiagramIR·Thinking·잘못된 JSON·
  리디렉션 거부 5종을 통과했다(`packaged-llm-hA3cLk`). 실제 사내 모델 호출은 아니다.
  다음: 검증한 소스 커밋 → 동일 소스의 internal.3 빌드 → 최종 ZIP/API/UI/LLM 검사 및 push.

- 재개(2026-09-10): 사용자가 미완료 정비 마무리, 사내 LLM 시험용 ZIP 생성과 Git push를 승인했다.
  이전 기록의 이번 작업 push 제외는 당시 범위이며 이번 요청으로 갱신한다.
  기존 임시 작업본에 internal.1/internal.2 ZIP이 남아 있어 보존하고 새 버전 internal.3을 사용한다.
  미기록 패키지 실패는 smoke-offline-preview가 구형 '만들기' 버튼을 찾은 것이었다.
  현재 '다이어그램 생성'으로 수정하고 실제 네트워크 정책 Git 제외 및 설치 안내를 보완했다.
  다음: 최신 소스 동기화 → 전체 오프라인 verify → 새 ZIP/실제 패키지 API·UI 검사 → 보고서/commit/push.

- A1 검증: 비동기화 임시 작업본에서 .NET 163/163 통과. 새 네트워크/경로 거부 테스트 포함.
- Node 보안·SPDX 회귀 5/5 통과. 설치 여부와 무관한 잠금 파일 268개 라이선스 통과.
- 최초 .NET 검증은 PowerShell 실행 정책으로 공통 초기화가 적용되지 않았고,
  샌드박스 계정의 빈 NuGet 캐시로 복원이 실패했다. 이후 명시적 텔레메트리 차단과
  기존 사용자 캐시 경로를 지정해 공개 소스 없이 locked restore 및 테스트를 통과했다.
- A2/A3 진행 중: 공개 다운로드·audit 제거, 완전 오프라인 복원, NuGet/런타임 증적·SBOM 추가.

- 기존 npm 검사기: 설치된 고유 패키지 241개 통과. 누락 대상도 exit 0인 결함 재현.
- 실제 사내 LLM/DB, 승인된 내부 미러와 호스트 방화벽 증적은 아직 제공되지 않음.
- 현재 작업 폴더는 OneDrive 아래이며 OneDrive 프로세스가 실행 중이다.
  강화된 실행/빌드 검증은 비동기화 임시 작업본에서 수행한다.
- 재개(2026-09-09): A1은 이미 구현되어 A2/A3/A4를 이어간다. 비동기화 작업본은
  `%TEMP%/DiagramMaker-security-4A9Jlf`이며 기존 checkpoint/변경/ZIP을 보존했다.
- 이번 회귀: .NET 163, worker 32, web 42 및 SPDX 3 통과. 기본 샌드박스의 esbuild
  상위 경로 접근 거부는 동일 오프라인 명령의 확장 권한 실행으로 해결했다.
- A2 보완: verify.cmd 공개 audit 경로 제거, 삭제 소스를 반영하는 로컬 재빌드,
  패키지 버전/경로 검증과 과거 ZIP 덮어쓰기 거부, 정적 자산·C++ 파서 경로 검사.
- A3 보완: NuGet JSON UTF-8 및 런타임 버전 범위 오류 수정, npm README/소스 고지와
  보충 표준 약관 구분, 중복 의존성 검증, 배포 DLL/JS 및 중첩 데이터 검사를 추가했다.
- 다음: 최신 파일을 임시 작업본에 반영하고 라이선스 수집 → 전체 verify → 새 Windows
  패키지/API/UI 검증을 수행한다. NuGet 수집 이후 단계는 아직 미검증이다.
- A2/A3 검증: 전체 verify exit 0 (.NET 163, worker 32, web 42, 정책 회귀 6),
  npm 268/NuGet 24 기록과 SBOM 292 구성요소 수집, API `api-smoke-kLH3hn`,
  Edge UI `code-block-ui-SsrJj3` 통과. 이후 고지 파일 해시 검증을 수집/SBOM/배포 검사에 연결했다.
- NuGet 서명 패키지의 일반 파일 해시와 lock contentHash 차이를 SDK의 NuGet.Packaging
  계산 도구로 해결했다. 보충 MIT 표준 약관의 다른 프로젝트 저작권 오표기를 제거했다.
- 시작 거부 5개 통과: 정책 누락, OneDrive 정책, 폐기된 외부 공급자, 미승인 LLM 주소,
  공개 IP 바인딩. `internal-policy-7KCtAi/result.json`에 기록했다.
- Windows 패키지 첫 시도는 x64 npm 스크립트가 PATH의 ARM64 Node로 빌드하여 실패했다.
  TypeScript/프런트엔드 테스트/번들러를 지정된 x64 Node로 직접 실행하도록 수정하고 재검증 중이다.
- 다음: 새 패키지 완성 후 실제 EXE/Node/UI 및 ZIP 검사, 최종 검증 기록과 감사 보고서 작성.
- 이번 작업은 외부 Git push, 실제 Codex 추론, 공개 패키지 다운로드를 수행하지 않는다.
