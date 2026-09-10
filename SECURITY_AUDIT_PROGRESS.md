# 사내 전용 보안·라이선스 정비 진행 기록

기준: 2026-09-09, HEAD e53efc2 및 기존 미커밋 코드 블록 기능 전체.
사용자 승인: 사내 사용, 개발/빌드/실행 외부 통신 차단, 외부 Git CLI 유지,
비동기화 경로 필수. 기존 checkpoint, 작업 변경, 과거 ZIP/시험 기록은 보존한다.

## 현재 단계

- [x] A0 읽기 감사와 기존 변경 백업. 과거 Codex 외부 추론 경로 및 배포 DLL 잔존 확인.
- [x] A1 Codex 실행 경로 제거, 사내 LLM/DB 연결 정책, 작업자 격리.
- [x] A2 비동기화 경로와 오프라인 개발/빌드/배포 정책 구현.
- [x] A3 라이선스 판정·고지 수집·SBOM·배포 검사 구현.
- [x] A4 회귀 테스트·격리 환경 검증·최종 감사 보고서. 외부 실연결/호스트 증적은 별도 범위로 기록.

## 검증 및 다음 작업

- 2026-09-10 internal.4 전달 완료: 릴리스 `7ae2a10`의 기존 origin/main 일반 push exit 0,
  `23c2409..7ae2a10` 갱신 확인. GitHub의 89.76 MB 파일 크기 권고 외 실패 없음.
  소스·ZIP/SHA·보고서와 구버전 배포 파일 정리를 원격에 반영했다. 기존 Git 이력은 보존했다.
  다음은 사내 승인 LLM 연결/해석 품질과 필요한 DB/호스트 증적 확인이다. 미해결 로컬 실패 없음.

- 2026-09-10 internal.4 최종 verify exit 0: .NET 168/worker 32/web 45/정책 7/시작 11,
  기본·선택 정책 API(`api-smoke-4cRlkv`, `api-smoke-V8wrzY`), UI(`code-block-ui-6412sZ`) 통과.
  실제 CMD 실행기 6종도 통과했다(`windows-launchers-PXf7KG`). Windows 포트 해제 지연을
  검사 스크립트에서 최대 5초 기다리도록 보완했으며 배포 파일은 변경하지 않았다.
  `artifacts/internal4-validation/`에 증적을 모으고 감사 보고서를 internal.4 기본/선택 실행에 맞췄다.
  새 ZIP/SHA 복사 후 해시를 검증하고 승인된 구버전 배포 ZIP/SHA 10개를 제거했다.
  최초 자동 승인 거부는 기존 승인 기록과 대상 모두 Git HEAD 내용에 일치함을 제시해 해소했다.
  다음: 릴리스 커밋과 기존 origin/main push. 실제 사내 LLM/DB/호스트 검증은 별도 환경에서 수행한다.

- 2026-09-10 재개: internal.4는 `a7b6ee3`, sourceTreeDirty=false로 이미 빌드됐다.
  ZIP 1,817개/94,119,889바이트와 API/UI/기본·선택 정책 합성 LLM 검사가 통과했다.
  실행기 검사만 첫 사례 후 EADDRINUSE로 실패했다. 현재 잔존 테스트 프로세스/5080 listener는 없다.
  다음: 실행기 종료 및 포트 해제 검증 → 감사 보고서 갱신 → 승인된 구버전 ZIP/SHA 교체와 기존 origin push.

- 2026-09-10 첫 clean worktree 빌드는 x64 worker 32/web 45/번들 통과 후 khroma 고지 비교에서
  실패했다. Git autocrlf가 검토 고지의 LF를 CRLF로 바꾼 것을 확인해 `packaging/licenses/** -text`
  속성을 추가한다. 라이선스 검사 기준은 완화하지 않는다. 다음: 보완 커밋/checkout 검증 후 재빌드.

- 2026-09-10 소스 `f027762` 커밋 완료. `%TEMP%/DiagramMaker-internal4-20260910`에
  동일 소스의 깨끗한 detached worktree를 만들고 `0.1.0-internal.4 -SkipTests` 빌드를 시작했다.
  기존 승인 캐시만 복사했으며, 다음은 실제 패키지/API/UI/LLM 두 모드/실행기/ZIP 검사다.

- 2026-09-10 internal.4 전체 verify exit 0. .NET 168, worker 32, web 45, 정책 회귀 7,
  시작 11종(`internal-policy-XAatmd`), 선택 정책 API(`api-smoke-BahTIx`),
  기본 API(`api-smoke-cGJBxo`), Edge UI(`code-block-ui-1k7OGL`) 통과.
  npm 268/NuGet 24/SBOM 292 검증. 호출 위치에 따른 NuGet 출력 경로 오류는
  비동기화 작업본에서 명령을 직접 실행해 해결했다. 공개 다운로드/실제 LLM·DB 호출 없음.
  다음: 소스 커밋과 동일 커밋의 internal.4 빌드, 패키지/실행기 검사 후 ZIP 교체·push.

- 2026-09-10 internal.4 재개: 기존 변경과 승인 범위를 확인하고 기본/선택 정책 안내,
  두 모드의 API·패키지 LLM 검사, 명시 정책 누락/오류/빈 목록과 기본 경로/origin 거부 검사를 추가했다.
  .NET 168, worker 32, web 45 통과. PowerShell 회귀의 모든 조건은 통과했으나 Windows Node가
  동기 임시 폴더 삭제에서 0xC0000409로 종료했다(확장 권한/x64도 재현).
  정리 코드를 기존 테스트와 같은 비동기 rm으로 바꿔 회귀 통과를 확인했다.
  다음: 전체 verify → 소스 커밋 → internal.4 패키지/두 실행 모드 검증 → 구버전 ZIP 정리와 push.

- 2026-09-10 후속 사용자 승인: 별도 network JSON 없는 기본 실행, 관리자 선택 정책,
  이전 배포 ZIP/SHA 삭제, 새 internal.4 ZIP 생성과 기존 origin/main push.
  미지정 시 기존 기본 위치의 network-policy.json도 읽지 않는다. 명시된 정책의 누락/오류/거부는
  기본 모드로 자동 전환하지 않는다. 기존 경로/원문/리디렉션/Git 보호와 오프라인 빌드는 유지한다.
  다음: 기본 모드와 선택 정책의 회귀, 전체 verify, ZIP 정리와 새 패키지 검증.

- 2026-09-10 원격 배포 완료: 소스 `5295226`, 패키징 보완 `6b19242`, 릴리스 `2d12cd3`을
  기존 `origin/main`에 일반 push했고 exit 0 및 `e53efc2..2d12cd3` 갱신을 확인했다.
  최초 자동 승인 거부는 기존 origin의 소스/ZIP 배포 이력을 제시한 재검토로 해소했다.
  GitHub의 파일 크기 권고 경고 외 실패 없음. 다음: 사내의 승인된 실제 LLM/DB 시험 결과 확인.

- 2026-09-10 A4 완료: internal.3 빌드 exit 0. 패키지 소스는 `6b19242`, sourceTreeDirty=false.
  x64 worker 32/web 44, npm 268/NuGet 11/Node 포함 SBOM 280 구성요소 통과.
  실제 패키지 API/5종 7페이지/C++/4종×4개 폭: `offline-preview-IgdXfH` 통과.
  실제 패키지 전체 UI: `code-block-ui-bmv0yA`, LLM 전송 5종: `packaged-llm-GjKh3r` 통과.
  ZIP 감사: 1,816 파일, 94,119,000 bytes, 모든 파일과 worker 소스/고지 해시 일치.
  SHA-256 `fd7bc703dab5879711098662f61c1ac73950ac4fbd1efe170e562bc50b850068`.
  실제 정책/런타임 데이터/외부 추론 경로 없음. 1440/390px 화면 직접 확인.
  `SECURITY_AUDIT_REPORT.md`에 검증 명령·결과·사내 시험 절차·미검증 범위를 기록했다.
  현재 미해결 로컬 실패 없음. 다음: ZIP/보고서 릴리스 커밋 및 origin/main 일반 push 확인.

- 2026-09-10 소스 커밋 `5295226` 생성. 미리보기 cb.1/cb.2 ZIP의 기존 해시도 일치하며 이력으로 보존했다.
  패키지 출처를 기록하기 위해 비동기화 작업본에 동일 커밋과 소스를 복제했다.
  샌드박스/빌드 계정 차이로 Git 소유자 검사가 발생하여, 패키지 메타데이터 조회에만 검증된
  projectRoot의 명령별 safe.directory를 지정했다. 사용자 전역 Git 설정은 변경하지 않았다.
  다음: 보완된 패키징 스크립트로 internal.3 완성 및 ZIP/실행 검증.

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
