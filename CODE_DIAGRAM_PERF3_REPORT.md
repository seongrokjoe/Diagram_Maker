# 항목별 의미 검토와 오류 복구 수정 기록

기준 커밋: `affd8e92a909d1c18b770c1d896b412bf162b486`. 시험 버전: `0.1.0-perf.3`.
사용자는 최신 배포 설정을 변경하지 않았으며 Thinking OFF, 완료 안정성 우선을 확인했다.

## 확인한 원인

- 생성 대상과 원본 근거가 같은 ref 별칭 계열을 사용했고, 응답 ID 스키마에 허용값이 없었다.
  SharedUnknownIds는 반환 ID의 불일치를 뜻한다. 실제 사내 응답이 근거 ID를 혼동한 것인지
  새로운 값을 만든 것인지는 이전 기록만으로 확정할 수 없다.
- 공유 의미 검토는 설정과 별도로 최대 2,000토큰에 제한됐다. 기존 검토 계약은 문제 20개를
  각각 500자까지 설명하도록 허용해 출력 예산과 응답 크기가 맞지 않았다.
- 검토의 accepted/issues 모순은 InvalidReview였으며 생성 수정 처리로 전달됐다.
  유효한 의미 거절도 묶음 전체를 수정 대상으로 처리했다.
- 최초/최근 오류는 전체 실행에서 추린 기록이었다. 같은 묶음의 연쇄 오류라고 단정할 수 없고,
  검토 거절 및 형식 오류는 구체적인 한국어 설명 대신 일반 오류 문구로 표시됐다.

## 구현

- 생성 대상은 item 별칭, 근거는 ref 별칭을 사용한다. 짧은 기존 ID와 충돌하지 않도록 예약한다.
  같은 치환표로 본문/수정 데이터/스키마 enum을 준비하고, 복원 전후 ID를 로컬에서 검증한다.
  미등록 ID를 비슷한 값이나 순서로 추정하지 않는다. 진단은 다른 근거 ID/미등록 별칭 개수만 공개한다.
- 공유 의미 검토는 모든 항목에 id와 issues 배열을 반환한다. 빈 배열은 통과, 최대 3개의
  허용 코드만 거절 사유로 쓴다. accepted와 자유 서술은 없으며 누락/중복/추가 필드/타입도 검사한다.
- 동봉 정책 예제의 검토 출력 2,000토큰을 유지하고 실제 ReviewOutputTokens 설정을 존중한다.
  appsettings.json의 기존 8,000토큰 값도 그대로이며, 정책 파일에서 지정하지 않으면 그 값을 사용한다.
  최대 응답 JSON의 UTF-8 바이트 수에 256토큰을 더한 보수적 예산으로 검토를 사전 분할한다.
  단일 항목의 출력 예산 부족은 검토 HTTP 전송 전에 명확한 진단으로 기록한다.
- 검토 잘림/형식 오류는 검토만 복구한다. 통과한 항목은 보존하고 의미상 거절 항목만 수정한다.
  생성 수정 1회와 검토 교정 1회는 별도이며 분할 자식에게 사용 횟수를 상속한다.
  최대 분할 깊이 12와 실행 900초 한도를 유지한다. 미검증 항목을 성공으로 표시하지 않는다.
- 진단에 요청 묶음/상위 묶음/시도/복구 상태를 추가하고 실제 출력 한도·사용량·종료 사유·방식을
  화면에 표시한다. 코드/프롬프트/응답 본문/실제 ID/서버 주소는 진단에 추가하지 않는다.
  HTTP 전송 전 분할된 중간 묶음의 상위 경로도 저장하여, 여러 단계 분할과 재개 후 의미 수정이
  성공하면 과거 오류를 복구 완료로 표시한다. 40항목 회귀에서 복구 중 표시가 남는 문제를 수정했다.
- 공유 요청 정책 v3와 검토 계약 v2로 체크포인트 키를 구분한다. 구버전 실패 캐시가 새 요청을 막지
  않으며 같은 정책의 완료 요청은 이어하기에서 재사용한다. 기존 결과/근거는 보존하고 정책 변경으로
  요청을 다시 수행하는 경우 화면에 안내한다. API 경로·DB 테이블 변경은 없고 JSON 필드는 선택 추가다.

## 검증

2026-09-11 최종 소스에서 전체 `scripts/verify.ps1` 통과(exit 0).
.NET 233/233, Git 작업자 32/32, 프런트엔드 46/46, 정책 7/7,
시작 정책 11개, API 두 모드, 공유 의미 두 문자 설정, SVG 6종, Edge UI를 확인했다.
새 ID/엄격한 검토 계약/출력 분할/검토만 복구/항목별 수정,
네 오류 연속 재현/교정 횟수 상속/전송 전 출력 부족/중단·재개/진단 비노출을 포함한다.
첫 회귀의 두 실패는 새로운 검토 응답을 생성 응답으로 읽던 기존 성능 테스트였으며,
생성 횟수와 검토 출력 예산을 별도로 검증하도록 수정한 뒤 전체 회귀를 통과했다.

재개 시 작업본의 web-tree-sitter 설치 누락은 오프라인 캐시로 복원했고,
esbuild의 상위 폴더 접근 제한은 확장 권한 검증으로 해소했다.
공유 의미 API fixture는 동봉 예제의 생성 8,000/검토 2,000토큰을 명시한다.
처음에는 검토값 생략으로 appsettings의 8,000토큰을 상속하면서 2,000토큰을 기대해 실패했다.
제품 설정 변경 없이 fixture를 바로잡았으며, 큰 출력 설정을 존중하는 동작은 별도 회귀로 확인했다.
40항목 분할 후 복구 완료 표시와 두 차례 중단·JSON 복원 회귀를 추가했고 관련 25개 검사가 통과했다.

1,212줄·40함수 C++의 합성 HTTP 응답을 실제 파서/API/저장소와 연결해 검사했다.
기본 2,000,000자와 60,000자 설정 모두 아래 결과를 유지했다.

| 사례 | 생성 HTTP | 검토 HTTP | 전체 HTTP | 결과 페이지 |
| --- | ---: | ---: | ---: | ---: |
| 코드 블럭 Flow | 2 | 8 | 10 | 41 |
| 코드 블럭 Flow·Class·코드 관계도 | 3 | 10 | 13 | 43 |
| Git 변경 3형식 | 10 | 28 | 38 | 51 |

perf.2의 생성 횟수와 페이지 수는 유지되며, 보수적 검토 분할로 검토 횟수가 증가했다.
동일한 완료 결과 재사용 시 추가 HTTP 요청은 0회다.
1440px/390px 화면에서 미해결/복구 완료/과거 기록, 출력 한도와 사용량, 상위 묶음,
정책 갱신 안내가 가로 넘침 없이 표시되는지 확인했다.
생성 중에는 결과 페이지를 요청하지 않으며 중단 뒤 부분 결과 열기도 통과했다.

최종 전체 검증 로그는 `artifacts/perf3-validation/perf3-verify-release.log`에 보존한다.
사내 실모델의 의미 품질과 완료 시간, PostgreSQL 실연결은 이 환경에서 검증하지 않는다.

## Windows 배포 검증

`0.1.0-perf.3` Windows x64 시험 패키지 빌드 및 실제 배포 EXE 검증을 완료했다.

- 파일: `artifacts/release/DiagramMaker-0.1.0-perf.3-win-x64.zip` 및 같은 이름의 `.sha256`.
- 크기: 94,236,262바이트, 1,819개 파일.
- SHA-256: `a05aad6cf07c24170a6d7fa103806ac74febf92ba330053f51df24e4182659c8`.
- ZIP 전체와 배포 폴더, 소스 218개와 검증 작업본, 전달용 ZIP/체크섬 복사본의 일치를 확인했다.
- 기존 perf.2 ZIP/SHA의 해시를 삭제 전에 확인했고 Git 체크포인트를 보존했다.
- 배포 EXE의 미리보기·코드 블럭 API/UI, 공유 의미 기본/60,000자, LLM 전송 두 모드,
  Windows 실행기까지 7개 검증 명령이 모두 통과했다. 두 전송 모드는 각각 5개, 실행기는 6개 검사를 수행했다.
- 배포본도 위 표의 생성/검토 요청 수와 페이지 수를 유지했다. 1440px/390px 배포 진단 캡처를 직접 확인했다.
- 한국어 사내 시험 안내, 완성 LLM JSON 예제, `LLM_POLICY_KO.txt`와 소스의 일치를 검사했다.

`artifacts/perf3-validation/`에 선별 로그·화면·결과 108개와 해시 목록을 저장했다.
`package-audit.json`은 ZIP 대조 결과, `source-inventory.json`은 검증한 소스의 SHA-256 목록,
`evidence-inventory.json`은 복사한 증적 목록이다. PowerShell 로그는 내용 보존 후 UTF-8로 정규화했다.

`.git` 없는 비동기화 작업본에서 빌드했으므로 manifest의 sourceCommit은 `unavailable`,
sourceTreeDirty는 null이다. 기준 커밋 이후 변경은 소스 해시 목록으로 식별한다.
2026-09-11 사용자 요청에 따라 배포 폴더의 perf.2 ZIP/SHA를 제거하고 최신 perf.3 ZIP/SHA만 남겼다.
삭제 파일의 해시와 복구 커밋 `affd8e9`는 `artifacts/perf3-validation/removed-packages.json`에 기록했다.
소스·문서·최신 패키지·선별 검증 증적·구버전 삭제를 커밋하고 origin/main에 배포하는 중이다.
제품 소스 218개와 최신 ZIP은 검증 시점의 해시를 유지하며 미해결 로컬 검증 실패는 없다.

## 재현 명령과 남은 사내 시험

검증 작업본은 `%LOCALAPPDATA%\Temp\DiagramMaker-performance-20260910`이다.
같은 소스와 사전 공급 의존성을 갖춘 비동기화 경로에서 실행한다.
기존 패키지는 덮어쓰지 않으므로 다시 빌드하려면 별도 새 버전을 지정한다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
# 최초 패키징에 사용한 명령. .NET은 위 전체 검증에서 실행했다.
powershell -ExecutionPolicy Bypass -File .\scripts\build-offline-win-x64.ps1 -Version '0.1.0-perf.3' -SkipTests
$package = 'artifacts/stage/DiagramMaker-0.1.0-perf.3-win-x64'
node scripts/smoke-offline-preview.mjs $package
node scripts/smoke-code-block-ui.mjs $package
node scripts/smoke-shared-semantics.mjs $package
node scripts/smoke-shared-semantics.mjs $package --characters-60000
node scripts/smoke-packaged-llm.mjs $package
node scripts/smoke-packaged-llm.mjs $package --basic
node scripts/smoke-windows-launchers.mjs $package
```

사내에서는 동봉 설정 안내에 따라 승인 주소/모델/한도를 적용하고 실제 문제 코드의 의미 품질,
분기·호출·근거, 오류 복구와 전체 완료 시간을 확인한다. 사내 LLM/PostgreSQL 실연결,
취약점 피드와 호스트 외부 통신 캡처는 이번 로컬 합성 검증에 포함하지 않았다.
