# internal.7 신뢰성·요청 수 개선

실행 구조는 Roslyn/Tree-sitter가 확정하고 LLM은 근거가 연결된 한국어 설명을 생성·검토한다.
같은 함수·동작의 설명을 여러 형식에서 공유하여 함수별 중복 실행 계획 요청을 제거했다.

- C++ 캐스트 수신 객체, 타입 별칭과 범위를 보존한다. 캐스트 자체는 호출이 아니며
  미확인 객체·가상 호출·지역 콜백을 이름만으로 내부 함수에 연결하지 않는다.
- 마스킹·별칭 치환 이후 메시지·시스템 지시·스키마와 UTF-8 예산을 전송 전에 검사한다.
  배치를 입력·출력·검토 예산에 맞춰 나누고 너무 큰 항목은 분리한다.
- LLM 전송 전에 모든 생성 가능한 정적 페이지를 저장한다. 단위별 검토 결과를 중간 저장하여
  취소·실패·900초 종료 후 보존하고 이어하기에서 검토된 요청을 재사용한다.
- 생성 중 캔버스를 숨기고 경과 시간·현재 단계·검토 수·실제 진행 시각을 표시한다.
  5분 경고 뒤 계속 생성하며 매초 시계 변경은 접근성 알림을 반복하지 않는다.
- 코드 입력에 스크롤과 동기화된 줄 번호를 추가했다. 기존 작업·편집·정책·근거는 보존한다.
  캐시 버전은 `code-block-v5`, `source-graph-v12`, `shared-semantic-v3`, `shared-requests-v6`이다.

## 합성 회귀

1,212줄·40함수·4클래스, Thinking OFF, 기본 출력 8,000/검토 2,000토큰 기준이다.

| 입력 | 형식 수 | 생성/검토 요청 | 총 전송 | 보존 페이지 | 목표 상한 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 코드 블럭 Flow | 1 | 5/5 | 10 | 41 | 20 |
| 코드 블럭 | 3 | 7/7 | 14 | 43 | 20 |
| Git 변경 | 3 | 15/15 | 30 | 51 | 32 |

새 회귀는 첫 전송 전 정적 저장, 취소·새 실행 재개 시 검토 보존, 과대 항목 이후 정상 항목 처리,
부분 설명의 제어선·근거 보존, 시스템·스키마·UTF-8 예산 초과의 전송 차단을 확인한다.
기본 2,000,000자와 60,000자 설정의 전체 검증 및 패키지 결과는 아래에 최종 기록한다.

## 사내 측정

ZIP의 `benchmarks/`에 회귀와 같은 `before.cpp`, `after.cpp`, 한국어 절차와 결과 CSV를 넣었다.
코드 Flow/3종 및 Git 3종을 새 작업에서 각각 3회 측정한다. 합성 응답은 실제 모델 품질이나
300초 완료 목표를 입증하지 않는다. 사내 LLM·PostgreSQL 실연결·외부 취약점 피드·호스트
통신 캡처는 이 환경에서 수행하지 않았다.

## 최종 검증

`powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1` 실제 exit 0.
비동기화 검증 작업본에서 .NET 284, worker 42, web 46, 정책·폰트·실행기 단위 13개,
API 2모드, 공유 의미 기본/60,000자, SVG 6개, 코드 블럭·디자인 UI와 Windows CMD 14개가 통과했다.
위 요청 수와 페이지 수는 두 문자 제한 모두 동일하다.

줄 번호의 전역 pre 색상 상속을 보정한 후 프런트엔드 빌드와 UI를 다시 통과했다.
원본 증적: `artifacts/internal7/verify-result.json`, `verify.stdout.log`, `verify.stderr.log`.
합성 입력 증적: `shared-semantics-Cc3JOa`, `shared-semantics-KQsYAr`.
최종 줄 번호/UI: `code-block-ui-Dfl7Or`; 전체 디자인: `design-ui-Ye5Ane`.

Windows x64 ZIP은 소스 커밋 `cfe855e332d7fe85c7a7cac97ff38fd22809b811`에서 생성했다.
실제 소스·설정·문서 288개의 SHA-256을 저장소와 패키징 작업본 사이에서 대조했다.
이전 검증 증적의 작업본 차이 때문에 manifest에는 `sourceTreeDirty=true`가 남아 있다.
패키지의 1,831개 파일 전체가 검사한 staging 파일과 일치하고 런타임 데이터·실제 정책은 없다.

- 파일: `artifacts/release/DiagramMaker-0.1.0-internal.7-win-x64.zip` (96,352,927바이트)
- SHA-256: `314e5062764043954922771e3c25d17539dee2bca1470c85a2c1eba43c4fa32d`
- 감사: `artifacts/internal7/package-audit.json`, `source-manifest.json`
- 패키징은 x64 worker 42/web 46, 게시·라이선스·281개 SBOM·사내 전용 검사 후 ZIP/SHA를 생성했다.
  초기 로그 수집기는 프로세스 핸들을 늦게 열어 종료 코드를 null로 수집했다. 이를 exit 0으로
  기록하지 않았고 수집기를 보정해 후속 실행 검사별 실제 종료 코드를 별도로 기록한다.

패키지의 API·5종 코드 블럭, Mermaid 4종 × 4개 폭, UI, 합성 LLM 기본/제한 모드,
공유 의미 기본/60,000자와 Windows CMD 7개 검사 모두 실제 exit 0.
배포 EXE와 동봉 Node에서도 위 요청 수·페이지 수를 동일하게 재현했다.
증적은 `artifacts/internal7/package-*-result.json`과 각 stdout/stderr 로그에 있다.

[줄 번호 화면](artifacts/internal7/package-source-line-numbers.png),
[진행 상태 화면](artifacts/internal7/package-semantic-progress-1440.png),
[모바일 진행 상태](artifacts/internal7/package-semantic-progress-390.png).

승인된 릴리스 정리 범위에 따라 새 ZIP/SHA만 배포 폴더에 남기며 이전 5개 ZIP/SHA 쌍을 정리한다.
이전 소스·릴리스는 Git 이력에 남고 의존성 캐시는 보존한다. 실제 사내 모델과 DB 검증은 미실시다.
