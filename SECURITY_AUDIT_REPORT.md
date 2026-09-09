# 사내 LLM 시험용 Windows 배포 검증

검증일: 2026-09-10. 배포 버전: `0.1.0-internal.3`.
소스 커밋: `6b19242065bbbbd56bc45e83b382ad5f444473be`.

[ZIP](artifacts/release/DiagramMaker-0.1.0-internal.3-win-x64.zip) ·
[SHA-256 파일](artifacts/release/DiagramMaker-0.1.0-internal.3-win-x64.zip.sha256).
ZIP 크기는 94,119,000바이트이며 파일 1,816개를 포함한다.
SHA-256: `fd7bc703dab5879711098662f61c1ac73950ac4fbd1efe170e562bc50b850068`.

코드 블럭 다이어그램과 그룹별 구성/결과 UI를 포함한 사내 전용 배포본이다.
외부 추론 공급자와 샘플 실행 경로를 제거하고, 별도 네트워크 정책·비동기화 경로 검사,
오프라인 의존성 복원과 라이선스 고지/SBOM 검사를 적용했다.
기존 checkpoint와 cb.1/cb.2·offline.15/offline.16 ZIP은 이력으로 보존한다.
과거 ZIP은 현재 사내 전용 배포 정책을 적용하기 전의 자료다.

## 로컬 검증

검증은 OneDrive 밖의 격리 작업본에서 기존 잠금 버전 캐시와 합성 데이터만 사용했다.
공개 npm audit, 패키지 다운로드, 실제 사내 LLM 또는 DB 호출은 수행하지 않았다.

| 검사 | 결과 |
| --- | --- |
| .NET 회귀 | 163/163 통과 |
| Git/C++ 작업자 | 32/32 통과 |
| 프런트엔드 | 44/44 통과 |
| 라이선스/배포 정책 회귀 | 6/6 통과 |
| 시작 정책 거부 | 5/5 통과 |
| API | 코드 5종·Git 4종, 근거·답변·편집 충돌·삭제·소유자 검사 통과 |
| Edge UI | 그룹 옵션·결과 트리·키보드·과거 이력·URL 라벨·SVG/PNG 통과 |
| 개발 고지 인벤토리 | npm 268, NuGet 24, SBOM 292 구성요소 |
| Windows x64 빌드 | 작업자 32, web 44, TypeScript, 자체 포함 EXE, 고지 검사 통과 |
| 배포 고지 인벤토리 | npm 268, NuGet 11, Node 포함 SBOM 280 구성요소 |
| 최종 패키지 API/렌더링 | 코드 5종·7페이지, C++ stdin, 기존 프리셋 4종 × 4개 폭 통과 |
| 최종 패키지 UI | 그룹·복원·편집·키보드·5종 URL 라벨·SVG/PNG·3개 화면 폭 통과 |
| 최종 패키지 LLM 전송 | 연결·DiagramIR·Thinking, 잘못된 JSON·리디렉션 거부 5종 통과 |
| ZIP 감사 | 전체 파일 바이트/체크섬/소스 일치, 실제 정책·런타임 데이터 없음 |

패키지 manifest의 sourceTreeDirty는 false이며 소스 커밋은 위 SHA와 일치한다.
임시 작업본의 검증 증적은 `artifacts/offline-preview-IgdXfH`, `artifacts/code-block-ui-bmv0yA`,
`artifacts/packaged-llm-GjKh3r`, `artifacts/internal3-package-audit.json`에 남겼다.
1440px 구성 화면과 390px 결과 화면도 직접 확인했다.

전체 명령은 `scripts/verify.ps1`이다. 오프라인 npm ci 후 esbuild의 샌드박스 상위 경로
접근 제한이 발생하여 동일 캐시로 `verify.ps1 -UseInstalledDependencies`를 확장 권한에서
완료했다. 패키지는 `build-offline-win-x64.ps1 -Version '0.1.0-internal.3' -SkipTests`로
빌드했다. 여기서 SkipTests는 이미 통과한 .NET 재실행만 생략하며 x64 worker/web 검사는 수행한다.

이전 패키지 검사의 구형 버튼명과 빌드 계정 간 Git 메타데이터 조회 문제를 수정했다.
Git 신뢰 설정은 검증된 작업 경로에 대한 개별 조회 명령에만 적용한다.

## 사내 환경에서 시험하기

1. ZIP과 SHA-256 파일을 함께 받아 해시를 비교하고 `C:\Tools\DiagramMaker` 같은 비동기화 폴더에 압축을 푼다.
2. `configure-network.cmd`로 네트워크 정책을 준비한다. 배포/데이터/저장소/LLM 정책 폴더를 LocalRoots에,
   승인된 LLM origin과 IP 범위를 LlmOrigins/LlmAddressRanges에 등록한다. 빈 예제는 실행을 거부한다.
3. `configure-llm.cmd`에서 실제 Endpoint, AllowedOrigin, Model을 설정한다. 실제 설정 파일은 Git에 포함하지 않는다.
4. `start.cmd` 실행 후 `health-check.cmd`, `test-llm.cmd` 순서로 검사한다.
5. 코드 블럭 탭에서 합성 C/C++·C# 코드를 입력하고 의미 설명 완료 여부, 조건/반복/반환,
   블럭 간 호출, 단계별 원본 근거, 저장·새로고침·편집 이력을 확인한다.

정책 파일의 ACL과 호스트 송신 정책은 기존 사내 운영 절차에 따라 적용한다.
LocalFile은 단일 앱 인스턴스용이다. 사내 공유 DB가 필요하면 별도로 PostgreSQL을 검증한다.

## 검증 범위의 한계

합성 loopback 검사는 실제 사내 모델의 해석 품질이나 연결 성공을 증명하지 않는다.
실제 사내 LLM/Thinking 품질, PostgreSQL 실연결·동시성, 사내 취약점 피드,
호스트/브라우저 배경 통신 캡처, Linux 컨테이너 OS 검토는 이번 실행 범위에 포함하지 않았다.
현재 ZIP은 위 사내 연동 시험을 수행할 수 있도록 준비한 배포본이다.
