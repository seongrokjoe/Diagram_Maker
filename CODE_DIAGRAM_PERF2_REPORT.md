# 공유 의미 응답 검증·입력 한도 수정 기록

기준 커밋: `fc5da78255b41aeb90a26a301ffc1f052e817e38`. 시험 버전: `0.1.0-perf.2`.

## 보고된 실패와 변경

사내 신규 생성에서 `llm-SharedSemanticResponse / LLM_SCHEMA_INVALID / SharedSemanticCoverage`가
보고됐고, 이후 수정 요청이 문자 수 한도를 초과했다. 이전의 `SharedSemanticCoverage`는 여러 검증 실패를
한 코드로 표시했으므로, 그 기록만으로 누락 ID나 한국어 설명 등 최초 세부 원인을 확정할 수 없다.

- 누락·중복·알 수 없는 ID, 추천 형식, 설명의 길이·한국어·코드 구문 등을 개별 코드로 구분한다.
- 수정할 응답을 JSON 객체로 전달해 이중 문자열 변환을 피하고, 근거와 응답 ID에 같은 치환을 적용한다.
- 마스킹과 ID 치환을 끝낸 실제 JSON 길이로 생성·수정·검토의 문자 한도를 검사한다.
- 수정 요청이 커지면 해당 항목 묶음을 나눈다. 검토만 커지면 이미 생성한 응답을 보존하고 검토만 나눈다.
- 중단 후 이어하기에서도 완료된 생성·검토를 재사용하고 항목당 수정 기회 1회를 유지한다.
- 전송 전에 거절된 요청도 진단에 남긴다. 화면에서 최초 및 최근 오류, 단계, 문자·토큰 한도,
  검증 항목 수·필드·길이를 확인할 수 있다. 원문 코드나 실제 ID를 화면 진단에 추가하지 않는다.

원문 근거의 임의 잘라내기, 검증 완화 또는 실패 결과의 성공 처리는 하지 않는다.

## 완성 JSON 예제

`packaging/windows/config/llm-policy.example.json` 및 `LLM_POLICY_KO.txt`를 함께 제공한다.
`Endpoint`, `AllowedOrigin`, `Model`을 사내 승인 값으로 바꿔
`%LOCALAPPDATA%\DiagramMaker\llm-policy.json`에 저장하고 재시작한다.

| 설정 | 예제 값 | 의미 |
| --- | ---: | --- |
| MaxInputCharacters | 2,000,000 | 요청 JSON 문자 수 제한 |
| MaxInputTokens | 200,000 | 사용자가 알려 준 사내 1회 입력 토큰 상한 |
| MaxContextTokens | 200,000 | 입력·출력 예약 합계 제한, 기존 앱 기본값 유지 |
| DiagramOutputTokens | 8,000 | Thinking OFF 생성 출력 예약 |
| ReviewOutputTokens | 2,000 | Thinking OFF 검토 출력 예약 |

60,000자는 60,000토큰이 아니다. 공유 의미 요청에서는 별도의 3,500자 여유분을 빼므로
60,000 설정의 유효 한도는 56,500자, 2,000,000 설정은 1,996,500자다.
문자 한도를 늘려도 토큰 및 전체 문맥 검사를 적용한다. 전체 문맥은 입력 + 요청 출력 예약 +
1,024토큰 이내여야 한다. 사내 서버의 전체 문맥 상한이 확인되지 않았으므로 입력 상한에
출력 예약을 더해 `MaxContextTokens`를 임의로 높이지 않았다.
공유 의미의 입력 48,000토큰 묶음 예산은 문자 한도와 별도로 유지한다.

## 검증

2026-09-11 비동기화 작업본에서 `scripts/verify.ps1` 전체 통과(exit 0).

- .NET 208/208, Git 작업자 32/32, 프런트엔드 46/46, 정책 7/7.
- 시작 정책 11개, API 두 모드, 공유 의미 API 두 문자 설정, SVG 6개, Edge UI 통과.
- 신규 회귀 13개: 세부 검증 10개와 60,000자 설정의 검토 분할·수정 분할·전송 전 차단.
  검토 분할은 두 차례 중단·재개 후에도 생성 HTTP 1회를 유지했고,
  수정 분할은 항목당 수정 최대 1회와 이어하기의 추가 HTTP 0회를 확인했다.
- 1440px 및 390px 화면에서 최초 응답 검증 실패와 후속 전송 전 문자 초과를 구분해 표시하며
  진단 텍스트가 가로로 넘치지 않음을 확인했다.

1,212줄·4개 클래스·40개 함수 C++를 실제 파서·API·저장소·HTTP 계층으로 실행했다.
LLM 응답은 합성 응답이며, 아래 결과는 기본 2,000,000자와 60,000자 설정 모두 같다.

| 사례 | 생성·검토 HTTP 요청 | 결과 페이지 |
| --- | ---: | ---: |
| 코드 블럭 Flow | 4 | 41 |
| 코드 블럭 Flow·Class·코드 관계도 | 6 | 43 |
| Git 변경 3형식 | 20 | 51 |

검증 로그·화면·진단은 `artifacts/perf2-validation/`에 보존한다.
사내 실제 모델의 최초 세부 검증 원인, 응답 의미 품질, 5분 완료 여부는 별도 사내 시험이 필요하다.
실제 사내 LLM과 PostgreSQL 연결은 이번 합성 검증에 포함하지 않는다.

## 배포 검증 상태

Windows 시험 패키지 생성과 배포 실행 검증 완료. 이전 세션의 완료 증적을 재개 시 확인하고,
현재 소스 및 전달용 ZIP과 다시 대조했다.

- 파일: `artifacts/release/DiagramMaker-0.1.0-perf.2-win-x64.zip` 및 같은 이름의 `.sha256`.
- 크기 94,221,662바이트, 1,819개 파일.
- SHA-256: `711b777085ef85f181bee4ddfb6efcc6afd430adc0d25c6509331aaf876a61f4`.
- ZIP 전체와 검증한 배포 폴더 일치, 현재 소스 216개와 검증 작업본 일치,
  복사된 검증 증적 75개와 원본 일치를 확인했다. 기존 perf.1 ZIP의 해시도 삭제 전에 확인했다.
- 코드 블럭 파서, 배포 EXE/Node/프런트엔드, 완성 LLM 설정 JSON과 한국어 적용 안내가 포함된다.
  설정 예제의 문자 2,000,000·입력 토큰 200,000·전체 문맥 200,000 및 예시 주소/모델을 검사했다.

배포 EXE로 미리보기·코드 블럭 API/UI, 공유 의미 기본/60,000자, LLM 전송 두 모드,
Windows 실행기까지 7개 검증 명령 모두 통과했다. 배포판에서도 위 표의 요청 수와 페이지 수를 유지했다.
1440px/390px 배포 화면 캡처에서 최초 응답 검증 실패와 후속 전송 전 문자 초과 진단을 확인했다.

`artifacts/perf2-validation/package-audit.json`에 ZIP 대조 결과,
`source-inventory.json`에 정확한 소스 파일 해시를 기록했다.
`.git` 없는 비동기화 작업본에서 빌드했으므로 manifest의 `sourceCommit`은 `unavailable`,
`sourceTreeDirty`는 null이다. 기준 커밋 이후의 미커밋 변경을 위 소스 목록으로 식별한다.
2026-09-11 사용자 요청에 따라 배포 폴더의 perf.1 ZIP/체크섬을 제거하고 최신 perf.2 ZIP/SHA만 남겼다.
삭제 파일과 해시, 복구 커밋 `fc5da78`은 `artifacts/perf2-validation/removed-packages.json`에 기록했다.
소스·최신 패키지·선별 합성 검증 증적과 삭제 내역을 커밋 `dc65ee085aa333e8410e1fe6122fee841edc5ad4`에
반영해 origin/main에 정상 push했다(`fc5da78..dc65ee0`, exit 0). 원격 SHA 일치와 커밋 후 소스 216개 및
ZIP 해시 유지를 확인했다. GitHub의 대용량 파일 권장 한도 경고는 있었으며 업로드는 완료됐다.

## 재현 명령과 남은 사내 시험

검증 작업본은 `%LOCALAPPDATA%\Temp\DiagramMaker-performance-20260910`이다.
동일 소스와 사전 공급된 의존성이 있는 비동기화 작업 디렉터리에서 실행한다.
패키지가 이미 있으면 빌드 스크립트가 덮어쓰기를 거부하므로 기존 배포 파일을 보존한다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
# 새 버전의 최초 빌드 시 사용(.NET은 위 전체 검증에서 실행).
powershell -ExecutionPolicy Bypass -File .\scripts\build-offline-win-x64.ps1 -Version '0.1.0-perf.2' -SkipTests
$package = 'artifacts/stage/DiagramMaker-0.1.0-perf.2-win-x64'
node scripts/smoke-offline-preview.mjs $package
node scripts/smoke-code-block-ui.mjs $package
node scripts/smoke-shared-semantics.mjs $package
node scripts/smoke-shared-semantics.mjs $package --characters-60000
node scripts/smoke-packaged-llm.mjs $package
node scripts/smoke-packaged-llm.mjs $package --basic
node scripts/smoke-windows-launchers.mjs $package
```

로컬 검증의 미해결 실패는 없다. 사내에서는 동봉 `config/LLM_POLICY_KO.txt`에 따라 승인 설정을
적용하고 실제 문제 코드의 최초/후속 오류 진단, 의미·분기·호출·근거 품질과 전체 완료 시간을
확인해야 한다. 합성 응답으로 통과한 결과를 실제 모델 품질이나 5분 완료 검증으로 간주하지 않는다.
