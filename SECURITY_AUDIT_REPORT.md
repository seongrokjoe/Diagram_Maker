# 사내 LLM 시험용 Windows 배포 검증

최신 `0.1.0-internal.5` 배포와 의미 분석 개선 검증은 [현재 배포 보고서](SEMANTIC_RELIABILITY_REPORT.md)를 참조한다.
아래는 internal.4 당시 보안 검증 이력이며, 이전 ZIP은 사용자의 새 버전 교체 지시에 따라 배포 폴더에서 제거했다.

## internal.4 검증 이력

검증일: 2026-09-10. 배포 버전: `0.1.0-internal.4`.
패키지 소스 커밋: `a7b6ee36668e68a113a68be6263727f9f64e2fb8`.

당시 파일: `DiagramMaker-0.1.0-internal.4-win-x64.zip` 및 SHA-256 파일.
ZIP 크기는 94,119,889바이트이며 파일 1,817개를 포함한다.
SHA-256: `6f4ca9c48e5e8e78b1a41fe290b326983b5f4676cace7b2014d388e829c0a62b`.

코드 블럭 다이어그램과 그룹별 구성/결과 UI를 포함한 사내 전용 배포본이다.
기본 실행에는 LLM 설정만 필요하며 추가 네트워크 허용목록은 관리자 선택 사항이다.
이전 설치의 network-policy.json은 자동으로 읽지 않는다. 명시적으로 선택한 정책은
파일 누락·오류·허용목록 위반 시 거부하며 기본 모드로 전환하지 않는다.
외부 추론 공급자 제거, 비동기화 경로 검사, origin 일치, 프록시·리디렉션 차단,
오프라인 의존성 복원과 라이선스 고지/SBOM 검사는 유지했다.
사용자 승인에 따라 배포 폴더의 구버전 ZIP/SHA를 internal.4로 교체했다.
기존 checkpoint와 커밋 이력은 보존한다.

## 로컬 검증

검증은 OneDrive 밖의 격리 작업본에서 기존 잠금 버전 캐시와 합성 데이터만 사용했다.
공개 npm audit, 패키지 다운로드, 실제 사내 LLM 또는 DB 호출은 수행하지 않았다.

| 검사 | 결과 |
| --- | --- |
| .NET 회귀 | 168/168 통과 |
| Git/C++ 작업자 | 32/32 통과 |
| 프런트엔드 | 45/45 통과 |
| 라이선스/배포 정책 회귀 | 7/7 통과 |
| 시작 정책 | 기본 실행·명시 정책·경로/origin 거부 11종 통과 |
| API | 기본/선택 정책 각각 코드 5종·Git 4종, 근거·답변·편집 충돌·삭제·소유자 검사 통과 |
| Edge UI | 그룹 옵션·결과 트리·키보드·과거 이력·URL 라벨·SVG/PNG 통과 |
| 개발 고지 인벤토리 | npm 268, NuGet 24, SBOM 292 구성요소 |
| Windows x64 빌드 | 작업자 32, web 45, TypeScript, 자체 포함 EXE, 고지 검사 통과 |
| 배포 고지 인벤토리 | npm 268, NuGet 11, Node 포함 SBOM 280 구성요소 |
| 최종 패키지 API/렌더링 | 코드 5종·7페이지, C++ stdin, 기존 프리셋 4종 × 4개 폭 통과 |
| 최종 패키지 UI | 그룹·복원·편집·키보드·5종 URL 라벨·SVG/PNG·3개 화면 폭 통과 |
| 최종 패키지 LLM 전송 | 기본/선택 정책 각각 연결·DiagramIR·Thinking, 잘못된 JSON·리디렉션 거부 5종 통과 |
| 실제 CMD 실행기 | 기본 실행·선택 정책 적용·누락/오류 거부 6종 통과 |
| ZIP 감사 | 전체 파일 바이트/체크섬/소스 일치, 실제 정책·런타임 데이터 없음 |

패키지 manifest의 sourceTreeDirty는 false이며 소스 커밋은 위 SHA와 일치한다.
패키지 검사 증적은 `artifacts/internal4-validation/`에 복사했다. 원래 임시 작업본의 검사 ID는
`offline-preview-lwwbr9`, `code-block-ui-pUFP61`, `packaged-llm-wc4eG1`(기본),
`packaged-llm-q8DEFN`(선택 정책), `windows-launchers-PXf7KG`이며 ZIP 감사도 함께 보존했다.
1440px 구성 화면과 390px 결과 화면도 직접 확인했다.

전체 명령은 `powershell -ExecutionPolicy Bypass -File scripts/verify.ps1`이다.
현재 소스와 일치하는 비동기화 작업본에서 기존 캐시로 오프라인 검증을 수행했다.
최종 실행은 exit 0이며 로그는 `artifacts/internal4-validation/verify.log`에 보존했다.
시작 정책은 `internal-policy-f4zeWT`, 기본/선택 정책 API는 `api-smoke-4cRlkv`와
`api-smoke-V8wrzY`, UI는 `code-block-ui-6412sZ`에서 통과했다.
패키지는 clean detached worktree에서
`build-offline-win-x64.ps1 -Version '0.1.0-internal.4' -SkipTests`로 빌드했다.
SkipTests는 이미 통과한 .NET 재실행만 생략하며 x64 worker/web 검사는 수행한다.

첫 clean checkout에서 Git autocrlf가 khroma 고지 바이트를 바꿔 해시 검사가 실패했다.
`packaging/licenses/** -text`로 원본 고지 바이트를 보존해 해결했으며 검사 기준은 유지했다.
CMD 검사에서 테스트 프로세스 종료 직후 Windows 포트 해제가 늦어지는 문제는
최대 5초의 해제 대기로 해결했다. 이 수정은 패키지에 포함되지 않는 검사 스크립트에만 적용됐다.

검증된 소스와 ZIP/SHA, 보고서는 릴리스 커밋 `7ae2a10`으로 기존 `origin/main`에 푸시했다.
`23c2409..7ae2a10` 갱신과 exit 0을 확인했다. GitHub의 파일 크기 권고 외 실패는 없었다.

## 사내 환경에서 시험하기

1. ZIP과 SHA-256 파일을 함께 받아 해시를 비교하고 `C:\Tools\DiagramMaker` 같은 비동기화 폴더에 압축을 푼다.
2. `configure-llm.cmd`에서 실제 Endpoint, AllowedOrigin, Model을 설정한다. 실제 설정 파일은 Git에 포함하지 않는다.
3. `start.cmd` 실행 후 `health-check.cmd`, `test-llm.cmd` 순서로 검사한다. 별도 네트워크 JSON은 필요하지 않다.
4. 관리자가 추가 허용목록을 적용할 경우에만 `configure-network.cmd`로 정책을 준비하고
   `start-with-network-policy.cmd`로 실행한다. 배포/데이터/저장소/LLM 설정 경로와 승인된 LLM origin/IP를 등록한다.
   `DIAGRAMMAKER_NETWORK_POLICY_PATH`에 절대 경로를 지정하면 `start.cmd`도 해당 정책을 적용한다.
5. 코드 블럭 탭에서 합성 C/C++·C# 코드를 입력하고 의미 설명 완료 여부, 조건/반복/반환,
   블럭 간 호출, 단계별 원본 근거, 저장·새로고침·편집 이력을 확인한다.

정책 파일의 ACL과 호스트 송신 정책은 기존 사내 운영 절차에 따라 적용한다.
LocalFile은 단일 앱 인스턴스용이다. 사내 공유 DB가 필요하면 별도로 PostgreSQL을 검증한다.

## 검증 범위의 한계

합성 loopback 검사는 실제 사내 모델의 해석 품질이나 연결 성공을 증명하지 않는다.
실제 사내 LLM/Thinking 품질, PostgreSQL 실연결·동시성, 사내 취약점 피드,
호스트/브라우저 배경 통신 캡처, Linux 컨테이너 OS 검토는 이번 실행 범위에 포함하지 않았다.
현재 ZIP은 위 사내 연동 시험을 수행할 수 있도록 준비한 배포본이다.
