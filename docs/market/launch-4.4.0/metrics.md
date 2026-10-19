# 4주 출시 성과 기록

## 링크와 캠페인

| 캠페인 ID | 사용하는 곳 | Store 링크 |
| --- | --- | --- |
| community-ko | W1~W2 한국어 커뮤니티 글, 한국어 모집 안내 | https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-ko |
| community-en | W3 영어 커뮤니티 글, 영어 모집 안내 | https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-en |
| video | 개인 SNS, YouTube Shorts, Instagram Reels, 세로 영상을 첨부한 게시글 | https://apps.microsoft.com/detail/9PB33MC095VR?cid=video |
| gh-readme | 별도 작업에서 수정하는 GitHub README | https://apps.microsoft.com/detail/9PB33MC095VR?cid=gh-readme |
| site | 별도 작업의 소개 사이트 https://sukwoo0711-maker.github.io/my-capture/ | https://apps.microsoft.com/detail/9PB33MC095VR?cid=site |

윈도우 포럼은 세로 영상을 첨부하면 video를, 정지 이미지로 대체하면 community-ko를 사용하도록 초안을 구분했습니다. 게시 기록에 실제 사용한 ID를 적습니다. 방문자가 같은 Store 세션에서 다른 개발자의 앱을 획득하면 그 개발자에게도 캠페인 ID가 보일 수 있으므로 개인 정보나 비공개 정보를 ID에 넣지 않습니다.

## Partner Center에서 읽을 항목

Partner Center > MyCapture > Acquisitions report에서 같은 기간과 시장·기기 필터를 유지하여 다음 항목을 기록합니다.

| 정확한 차트 이름 | 기록할 내용 |
| --- | --- |
| App page views and conversions by campaign ID | 각 ID의 페이지 조회와 전환 |
| Total campaign conversions | 캠페인 전체 전환 합계 |
| Acquisition funnel | Page views → Acquisitions → Installs → Usage 단계 |
| App page views and conversions by channel | Store traffic, Custom campaign, Other별 유입 |
| Acquisitions | 새 사용자의 앱 라이선스 획득 |
| Installs | 실제 설치. 재설치·여러 기기 설치도 포함 |

캠페인 전환은 첫 캠페인 클릭 후 24시간 안의 획득입니다. 다른 기기의 획득은 같은 Microsoft 계정이어야 연결됩니다. Acquisition funnel의 Acquisitions는 페이지 조회 후 48시간 안의 획득으로, 캠페인 전환과 기간이 다릅니다. 이 퍼널은 Windows 10/11의 최근 90일 데이터를 보이며 캠페인 ID로 필터링할 수 있습니다.

획득은 로그인한 Microsoft 계정 기준이고 페이지 조회에는 로그아웃 방문자도 포함됩니다. 획득 데이터는 최대 10일 동안 조정될 수 있으므로 조회일을 남깁니다. 마지막 주 판정도 이후 조정값을 확인합니다.

자료: [캠페인 작성·귀속 기준](https://learn.microsoft.com/en-us/windows/apps/publish/create-a-custom-app-promotion-campaign), [획득 보고서](https://learn.microsoft.com/en-us/windows/apps/publish/acquisitions-report). 2026-10-20에 읽었으며 후자는 Partner Center 문서로 이동합니다.

## 같은 ID를 쓰는 채널 구분

한국어 채널들이 community-ko를 공유하므로 계획의 하루 한 채널 게시 간격을 지킵니다. 게시일·시각, 글 URL, 실제 ID, 해당 글의 조회·댓글 수를 함께 기록합니다. D+1 윈도우 포럼, D+2 아이보스, D+3 개인 SNS와 D+8 GeekNews, D+9 OKKY, D+10 디스콰이엇의 반응을 날짜로 비교합니다. 개인 SNS와 세로 영상은 video에 모입니다. 시간 차이는 참고 근거이며 같은 ID의 전환을 특정 게시글에 정확히 배분할 수는 없습니다.

| 게시일·시각(KST) | 채널·글 URL | 실제 ID | 글 조회 수 | 댓글 수 | 반응을 받은 문구·질문 |
| --- | --- | --- | --- | --- | --- |
|  |  |  |  |  |  |
|  |  |  |  |  |  |
|  |  |  |  |  |  |

## 매주 함께 기록할 수치

Ratings and reviews에서 평점 수·평균, 글이 있는 리뷰 수, 미응답 리뷰 수를 기록합니다. 한국 Store에서 `캡처`와 `캡처 도구`를 직접 검색해 순위와 조회일을 적습니다. 검색 지역·기기 조건도 같게 유지합니다.

GitHub 별 수와 릴리스 자산별 다운로드 수를 기록합니다. 아래 명령은 조회용이며 자산별 수치를 유지합니다. 여러 파일 다운로드가 한 사람의 사용일 수 있으므로 사용자 수로 합산하지 않습니다.

```text
gh api repos/sukwoo0711-maker/my-capture/releases --jq '.[] | {tag: .tag_name, assets: [.assets[] | {name, download_count}]}'
```

GitHub Insights > Traffic에서 유입 경로도 매주 기록합니다. 14일 보존 기간 안에 수집해 이전 주 기록과 이어 봅니다. 주간 표의 릴리스 다운로드 칸에는 자산별 기록 위치나 자산별 값을 넣습니다.

## 주간 기록 표

아래 기준값은 요청서에서 제공한 2026-10-20 값입니다. 평점 1개, 리뷰 0개, GitHub 별 0개, `캡처` 검색 25위만 채웠습니다. 나머지는 직접 확인한 뒤 기록합니다. 조회·전환·획득·설치는 해당 주, 평점·리뷰·별은 확인 시점 누적값으로 적으며 평균 평점은 별도로 기록합니다.

| 기간 | community-ko 조회 | community-ko 전환 | community-en 조회 | community-en 전환 | video 조회 | video 전환 | gh-readme 조회 | gh-readme 전환 | site 조회 | site 전환 | 획득 | 설치 | 평점 수 | 평균 평점 | 리뷰 수 | 미응답 | 별 | 릴리스 다운로드 | 비고 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 기준 2026-10-20 |  |  |  |  |  |  |  |  |  |  |  |  | 1 |  | 0 |  | 0 |  | 캡처 검색 25위(2026-10-20), 요청서 제공 |
| W1 |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| W2 |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| W3 |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |
| W4 |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |  |

비고에는 조회일, 검색어별 순위, 적용 필터, 주요 글 반응과 변경한 문구를 적습니다.

## W4 검토

어떤 캠페인에서 조회가 실제 획득으로 이어졌는지, 조회가 늘어도 설치로 이어지지 않은 구간은 어디인지 살펴봅니다. 같은 ID를 쓰는 채널은 게시일과 글별 댓글 내용으로 보완합니다. 개인정보 가리기, 핀, 다시 찾기 중 어떤 설명이 구체적인 사용 질문을 만들었는지 확인합니다. 반복된 질문이나 오해가 있다면 Store 설명과 다음 게시글의 해당 문장을 고치고, 바꾼 날짜와 문구를 기록합니다.
