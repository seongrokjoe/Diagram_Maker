# 의미 분석·렌더링 개선 검증 결과

2026-09-10. 승인 계획의 구현, 이 PC에서 가능한 로컬 검증, 시험용 Windows 패키지를 완료했다.
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

OneDrive 실행 차단 정책을 유지하고 `%TEMP%/DiagramMaker-semantic-20260910`의 비동기화 작업본에서 실행했다.
검증한 소스·스크립트 202개가 저장소의 작업 파일과 바이트 단위로 일치함을 별도로 확인했다.

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
| ZIP 감사 | 1,817개 파일의 빌드 결과 일치, 필수 파일·작업자·실행기·SHA-256·기존 ZIP 보존 확인 |

예산 중단 회귀는 합성 응답을 대기시킨 상태에서 테스트 예산을 1초로 줄여 수행했다.
실제 모델을 900초 동안 실행한 시험으로 해석하지 않는다. 화면은 390/800/1440px 캡처를 포함한다.

검증 로그와 결과·화면은 [artifacts/semantic-validation](artifacts/semantic-validation/)에 있다.
주요 증적은 [전체 검증 로그](artifacts/semantic-validation/semantic-verify-final.log),
[패키지 API 결과](artifacts/semantic-validation/offline-preview-dANhwV/result.json),
[패키지 UI 결과](artifacts/semantic-validation/code-block-ui-ad8BZz/result.json),
[ZIP 감사](artifacts/semantic-validation/package-audit.json),
[소스 해시 목록](artifacts/semantic-validation/source-snapshot.json)이다.
전체 검증 이후 추가한 HTTP·진단 다운로드 검사도 별도 API/UI 및 실제 패키지 실행에서 통과했다.

## 시험 패키지

- [DiagramMaker-0.1.0-semantic.1-win-x64.zip](artifacts/release/DiagramMaker-0.1.0-semantic.1-win-x64.zip)
- [SHA-256 파일](artifacts/release/DiagramMaker-0.1.0-semantic.1-win-x64.zip.sha256)
- 크기: 94,163,939바이트
- SHA-256: `9f4df338eeaccdda0396d030c1fc119ddba4bc28fc25b8fe2a38b075d14f3966`

이 ZIP은 기준 `17106dda2300bbd5929ac2dd1252a3d9ab403b16` 이후 미커밋 작업본의 시험 패키지다.
빌드 복사본에는 `.git`이 없으므로 내부 manifest는 `sourceCommit=unavailable`, `sourceTreeDirty=null`이다.
위 소스 해시 목록과 ZIP 감사가 검증한 작업본을 식별한다. 신규 커밋·푸시·기존 배포 ZIP 교체는 수행하지 않았다.
internal.4 ZIP의 SHA-256은 기존 `6f4ca9c48e5e8e78b1a41fe290b326983b5f4676cace7b2014d388e829c0a62b`와 같다.

실행할 때는 ZIP을 OneDrive 등 동기화 경로 밖에 풀고 패키지의 `OFFLINE_INSTALL_KO.txt`를 따른다.

## 남은 외부 검증

- 실제 사내 LLM 연결·의미 품질·실제 토큰 사용·처리 시간: 이 PC에서는 미실시.
- PostgreSQL 실연결과 동시성: 서버 환경이 없어 미실시. 구현 빌드 및 다른 저장소의 회귀 통과와 구분한다.
- 외부 취약점 피드와 호스트 전체 송신 캡처: 미실시. 오프라인 검증은 공개 audit 서비스를 호출하지 않았다.

다음 환경 의존 작업은 사내 비동기화 경로에서 Thinking OFF로 고정 합성 사례를 3회 실행하고,
문제 코드·작은 Git·큰 Git 사례의 의미, 원본 근거, 요약 공개 시점, 중단 후 재사용을 확인하는 것이다.
민감정보 없는 작업 진단으로 결과를 기록한다. 현재 해결되지 않은 로컬 검증 실패는 없다.
