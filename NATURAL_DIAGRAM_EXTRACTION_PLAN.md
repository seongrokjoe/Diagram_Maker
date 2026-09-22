# internal.13 자연어 추출·검토 개선

승인: 2026-09-21, Implement the plan. 기준 `8cc4f1b`.

- 사내 internal.12 내장 short-approval은 NATURAL_REQUIREMENTS_INVALID,
  table-interlock은 NATURAL_REQUIREMENTS_REJECTED/NaturalSemanticIssue로 실패했다.
  사내 파일 반출 없이 고정 입력과 메타데이터로 검증한다.
- 원문 추출 전용 DTO에서 origin/인용문을 제거한다. 근거 검증 후 기존 저장 계약으로 변환하며,
  설계 가정은 후속 설계에서 표시한다. 원문 의미 검토는 유지한다.
- 의미 검토는 고정 오류 코드, 원문 ID, 요구사항 ID, 내부 수정 지시를 사용한다.
  명시된 내용만 검사하며 추가 설계 제안을 추출 실패로 처리하지 않는다.
- 추출 최초+보정 최대 3회, 각 후보의 검토 계약 최초+보정 최대 3회로 분리한다.
  단위당 최대 12 논리 호출 및 기존 전체 시간 예산. 동일 후보 거부 검토를 재사용하고,
  무변화 소진은 NaturalRepairNoProgress. 정상 항목 보존과 대상 교체를 검증한다.
- 메타데이터 진단/사례별 복사용 요약을 제공한다. 본문/주소를 기록하지 않는다.
- natural-v9/natural-design-v5, 구형 결과 보존 및 체크포인트 분리.
- 회귀/실제 HTTP/API/UI/전체 verify/오프라인 internal.13 ZIP 및 패키지 검증.
- 실제 사내 확인은 사용자가 내장 검사와 기존 실패 입력의 캐시 없는 3회 재생성을 수행한다.
  사내 결과 전에는 로컬 완료/실제 모델 확인 대기로 기록한다.
