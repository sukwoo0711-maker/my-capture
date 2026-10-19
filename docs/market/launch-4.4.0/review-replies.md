# Store 리뷰 답변 초안

## 확인과 응답

모든 리뷰에 48시간 안에 답변하는 것을 운영 기준으로 합니다. 매 평일 아침 Partner Center > MyCapture > Ratings and reviews에서 Responses 필터를 미응답으로 설정해 확인합니다. 주말에도 48시간을 넘길 리뷰가 있는지 확인합니다.

답변은 기본적으로 리뷰 아래 공개됩니다. Partner Center에서 비공개 이메일 응답을 선택한 경우는 예외이며, 아래 초안은 공개 답변용입니다. 게시한 답변은 수정할 수 없습니다. 사용자가 원래 리뷰를 수정하면 답변이 사라지며 새 리뷰에 다시 답할 수 있습니다.

Store 답변 한도는 1,000자입니다. 아래 초안은 각각 600자 미만이며, 대괄호를 채운 후에도 600자 미만인지 확인합니다. 실제 리뷰의 문제와 확인한 조치를 반영하고 같은 문장을 여러 사람에게 그대로 반복하지 않습니다. 평점 변경의 대가나 광고·다른 앱 홍보를 넣지 않으며 개인정보를 포함하지 않습니다. 버전이나 해결 상태가 없으면 확인되지 않은 상태를 적고, 예정하지 않은 수정 일정을 약속하지 않습니다.

규칙 출처: https://learn.microsoft.com/en-us/windows/apps/publish/analyze-msi-exe/ratings-reviews-performance

## 칭찬 · Praise

개인화: 리뷰가 좋게 평가한 기능과 실제 사용 상황을 반영하고, 버전은 리뷰에 있을 때만 구체적으로 적습니다.

```text
[유용했다고 적은 기능]을 [리뷰의 사용 상황]에 사용하셨다는 의견 감사합니다. [리뷰에 표시된 버전 또는 버전 미확인]에서 도움이 된 부분으로 기록했습니다. 말씀하신 [구체적으로 좋았던 동작]을 이후 변경을 검토할 때 참고하겠습니다.
```

```text
Thank you for describing how [feature they praised] helped with [their actual task]. I've noted what worked in [version shown in the review, or “the version you used”]. Your detail about [specific behavior] will help me assess future changes.
```

## 버그 신고 · Bug report

개인화: 재현 여부와 조치 상태를 확인한 내용으로 채우며, 아직 재현하지 못했다면 그 사실과 다음 확인 항목을 적습니다.

```text
[구체적인 오류]로 [작업에 생긴 영향]을 겪으신 점 죄송합니다. [버전 또는 버전 미확인]의 해당 문제는 [확인한 상태와 다음 조치]입니다. 4.4.0부터 트레이 메뉴나 설정 > 정보의 문의하기에서 진단 정보와 메시지를 복사하고 GitHub 이슈를 열 수 있습니다. 앱을 열 수 없으면 https://github.com/sukwoo0711-maker/mycapture-support/issues 에 재현 순서를 남겨 주십시오. GitHub 계정이 필요하며 고객 정보는 제외해 주십시오.
```

```text
I'm sorry [specific bug] interrupted [their task]. For [version or “the reported version”], [confirmed status and next step]. As of 4.4.0, Contact support in the tray menu or Settings > About can copy diagnostics and your message and open a GitHub issue. If the app won't open, use https://github.com/sukwoo0711-maker/mycapture-support/issues with reproduction steps. A GitHub account is required; please omit customer data.
```

## 기능 요청 · Feature request

개인화: 원하는 기능의 사용 목적을 적고, 구현 예정·검토 중·계획 없음 중 실제 상태와 이유를 넣습니다.

```text
[요청 기능]이 [리뷰에 적힌 상황]에 필요하다는 의견을 확인했습니다. [사용 버전 또는 버전 미확인]에서는 [현재 가능한 동작이나 한계]입니다. 이 요청은 [실제 검토 상태와 이유]이며, [확인된 다음 조치 또는 현재 계획이 없다는 설명]입니다. 사용 목적을 구체적으로 남겨 주셔서 감사합니다.
```

```text
I understand why you need [requested feature] for [their stated task]. In [version or “the current version”], [current behavior or limit]. The request is [actual status and reason], and [confirmed next step or clear statement that it isn't planned]. Thank you for explaining the use case.
```

## 설치 실패 · Installation failure

개인화: Store·ZIP 중 사용 경로와 이미 시도한 조치를 반영하고, Store 오류 코드와 Windows 버전은 미제공인 항목만 요청합니다.

```text
[설치 단계나 오류]로 설치하지 못하신 점 죄송합니다. [설치하려던 버전 또는 버전 미확인]의 [확인한 상태나 다음 확인 사항]을 기록했습니다. Windows 11 또는 Windows 10 22H2 x64가 필요하며 ARM64 PC는 Windows 11이 필요합니다. Store 오류 코드와 Windows 버전·빌드를 https://github.com/sukwoo0711-maker/mycapture-support/issues 에 남겨 주십시오. GitHub 계정이 필요하며 계정 정보는 제외해 주십시오.
```

```text
I'm sorry installation stopped at [step or error]. For [target version or “this installation”], [confirmed status or next check]. MyCapture requires Windows 11 or Windows 10 22H2 x64; ARM64 PCs need Windows 11. Please share the Store error code and Windows version/build at https://github.com/sukwoo0711-maker/mycapture-support/issues. A GitHub account is required; please omit account details.
```

## 낮은 평점 · Low rating

개인화: 평점 자체를 논박하지 않고 구체적인 문제를 인정합니다. 조치·계획이 없으면 이유를 적습니다. 무음 녹화에 대한 의견에는 소리 녹화가 현재 계획에 없다는 범위로 답합니다.

```text
[구체적인 문제] 때문에 [기대했던 작업]을 하지 못하신 점을 확인했습니다. [버전 또는 버전 미확인]에서는 [현재 동작과 한계]입니다. [확정한 변경 내용과 시점 또는 변경 계획이 없는 이유]입니다. 남겨 주신 [구체적인 상황]은 사용 안내와 다음 변경을 검토할 때 참고하겠습니다.
```

```text
I understand that [specific problem] prevented [their intended task]. In [version or “the current version”], [actual behavior and limit]. [Confirmed change and timing, or the reason a change isn't planned]. I'll use your detail about [specific situation] when reviewing the guidance and future changes.
```
