# MyCapture 4.4.0 출시 확산 계획

엄격 평가 21번(출시를 알릴 채널과 일정이 없음)과 05번(평점 1건, 리뷰 0건)을 4주 일정으로 옮긴 문서입니다. 모든 글은 개발자 본인 계정으로 올리고, 이 폴더의 초안을 그대로 붙여 넣으면 되도록 준비했습니다. 제품 문장은 모든 채널에서 같게 씁니다. 한국어는 "개발자를 위한 무료 Windows 캡처·핀 도구", 영어는 "A free Windows screenshot and pinning tool for developers"입니다.

시작 시점(2026-10-20)의 숫자는 Store 평점 1건, 리뷰 0건, 한국 Store '캡처' 검색 25번째, GitHub 별 0개, v4.3.0 설치 파일 다운로드 1회입니다. 4주가 끝나면 [metrics.md](metrics.md)의 같은 칸과 비교합니다.

## 시작 조건

일정의 기준일 D는 4.4.0이 Microsoft Store에 게시된 것을 확인한 날입니다. 월요일을 D로 잡으면 아래 요일이 맞습니다. 4.4.0 게시 전에 글을 올리면 글에 적은 작은 카드, 테마, 보관 기간, 문의하기가 사용자 화면과 달라지므로 D 이전에는 올리지 않습니다.

D 이전에 끝낼 일은 다음과 같습니다. 윈도우 포럼은 가입 후 7일이 지나야 글을 쓸 수 있으므로 D-7보다 먼저 가입합니다. 디스콰이엇은 릴레잇 계정(이메일 매직 링크 로그인)이 필요하고, 제품 등록은 검토를 거쳐 승인됩니다. Reddit은 r/software가 공개하지 않은 기준의 카르마를 요구하므로 W3 전까지 계정 활동 이력을 확인합니다. 캠페인 링크 세 개(`community-ko`, `community-en`, `video`)는 Microsoft 안내의 시험 절차대로 한 번씩 눌러 Partner Center 획득 보고서에 잡히는지 봅니다. 랜딩 페이지(https://sukwoo0711-maker.github.io/my-capture/)가 열리는지도 확인합니다.

## 쓸 자료

큰 미디어는 git이 무시하는 `C:\GitRepositories\my-capture\artifacts\launch-4.4.0\`에 있습니다. 만드는 방법은 [video/README.md](video/README.md)에 있습니다.

| 자료 | 경로 | 쓰는 곳 |
|---|---|---|
| 세로 영상 한국어 1080×1920 | `artifacts\launch-4.4.0\MyCapture-vertical-ko-1080x1920.mp4` | 윈도우 포럼, 개인 SNS, YouTube Shorts, Instagram Reels |
| 세로 영상 영어 1080×1920 | `artifacts\launch-4.4.0\MyCapture-vertical-en-1080x1920.mp4` | 영어 Shorts·Reels |
| 세로 영상 표지 | `artifacts\launch-4.4.0\cover-ko-1080x1920.png`, `cover-en-1080x1920.png` | 영상 표지, 영상을 못 올리는 게시판 |
| 가리기 전후 이미지 | `artifacts\launch-4.4.0\redact-before-after-ko-1080x1350.png`, `-en-` | 윈도우 포럼, 아이보스, GeekNews 본문 설명, OKKY, 디스콰이엇, r/software |
| 30초 트레일러 | `artifacts\promo-2026-10-18\v2\MyCapture-trailer-store-1080p60.mp4`, `v2-en\MyCapture-trailer-store-en-1080p60.mp4` | 영상을 받는 게시판의 가로 영상 |
| Store 스크린샷 | Store 목록(언어별 10장) | 디스콰이엇 제품 이미지 보충 |

## 4주 일정

시각은 한국 시간입니다. 측정 링크는 글에 넣는 Store 주소의 캠페인 ID이고, 게시 뒤에는 글 주소를 [metrics.md](metrics.md) 기록 표에 적습니다. 한 주 안의 한국어 채널은 모두 `community-ko`를 쓰므로 하루에 한 채널만 올려 날짜로 나눠 봅니다.

| 주 | 날짜 | 시각 | 채널 | 형식과 규칙 요약 | 첫 댓글 | 쓸 자료 | 측정 | 초안 |
|---|---|---|---|---|---|---|---|---|
| 1 | D+0 (월) | 20:00 | 지인 메신저 | 첫 사용자 30~50명 모집, 보상 없음 | 없음 | 설치 안내 | `community-ko` | [beta.md](beta.md) |
| 1 | D+1 (화) | 21:00 | 윈도우 포럼 개발 / 자작 | `[자작] 이름 — 설명` 제목, 직접 만든 무료 프로그램만, 링크만 있는 글 금지 | Snipaste·캡처 도구와 다른 점 | 가리기 전후 이미지, 세로 영상 또는 표지 | `community-ko` | [posts/w1-windowsforum-ko.md](posts/w1-windowsforum-ko.md) |
| 1 | D+2 (수) | 10:00 | 아이보스 무료 툴 공유 | 완전 무료·직접 만든 도구만, 만든 이유·방식·해결하는 문제·사용법·데이터 수집 여부 필수, 과장 금지 | 보고서용 캡처에서 쓰는 순서 | 가리기 전후 이미지 | `community-ko` | [posts/w1-iboss-ko.md](posts/w1-iboss-ko.md) |
| 1 | D+3 (목) | 12:00 | 개인 SNS(LinkedIn, Threads, X)와 Shorts·Reels | 본인 계정, 세로 영상 | 없음 | 세로 영상 한국어 | `video` | [posts/w1-social-ko.md](posts/w1-social-ko.md), [posts/video-descriptions.md](posts/video-descriptions.md) |
| 2 | D+7 (월) | 10:00 | 디스콰이엇 제품 등록 | 본인 제품만, 검토 후 승인 | 없음 | 가리기 전후 이미지, 표지, Store 스크린샷 | `community-ko` | [posts/w2-disquiet-ko.md](posts/w2-disquiet-ko.md) |
| 2 | D+8 (화) | 08:30 | GeekNews Show | 직접 만든 것은 Show로, 써 볼 수 있어야 함, YouTube 링크 불가, 같은 프로젝트 재등록 제한(한 번만) | 받고 싶은 피드백 세 가지 | GitHub 저장소 링크 | `community-ko`, GitHub 유입 | [posts/w2-geeknews-show-ko.md](posts/w2-geeknews-show-ko.md) |
| 2 | D+9 (수) | 12:00 | OKKY 피드백 | 사이드 프로젝트·서비스 피드백 요청 게시판, 영리 광고 글은 삭제 | 설치 경로(Store 우선, GitHub ZIP은 서명 없음) | 가리기 전후 이미지 | `community-ko` | [posts/w2-okky-feedback-ko.md](posts/w2-okky-feedback-ko.md) |
| 2 | D+10 (목) | 10:00 | 디스콰이엇 포스트 | 제품에 연결한 포스트만 메인 피드에 노출 | 4.4.0에서 바꾼 점 | 표지 | `community-ko` | [posts/w2-disquiet-ko.md](posts/w2-disquiet-ko.md) |
| 3 | D+14 (월) | 10:00 | r/Windows11 사이드바 확인 | 허가 없는 앱 홍보 금지, 허가 신청은 받지 않는 중 | 없음 | 없음 | 없음 | [posts/w3-reddit-windows11-modmail-en.md](posts/w3-reddit-windows11-modmail-en.md) |
| 3 | D+15 (화) | 22:00 (미 동부 08:00) | Show HN | 직접 만든 것, 써 볼 수 있어야 함, 제목은 `Show HN`으로 시작, 추천 부탁 금지 | 만든 이유·작동 방식·한계 설명(본문 역할) | GitHub 저장소 링크 | `community-en`, GitHub 유입 | [posts/w3-show-hn-en.md](posts/w3-show-hn-en.md) |
| 3 | D+16 (수) | 23:00 (미 동부 수 09:00) | r/software | 오픈 소스만 허용, 수요일에 맞는 플레어, 소프트웨어 말고도 가치 있는 내용, 카르마 기준 | 탐지하지 못하는 경우 | 가리기 전후 이미지 영어 | `community-en` | [posts/w3-reddit-software-en.md](posts/w3-reddit-software-en.md) |
| 3 | D+17 (목) | 12:00 | 영어 Shorts·Reels | 본인 계정, 세로 영상 | 없음 | 세로 영상 영어 | `video` | [posts/video-descriptions.md](posts/video-descriptions.md) |
| 4 | D+21 (월) | 10:00 | 지표 집계 | Partner Center 획득 보고서, 리뷰, GitHub 유입 | 없음 | 없음 | 모든 캠페인 ID | [metrics.md](metrics.md) |
| 4 | D+22~24 | 업무 시간 | 회고와 문구 수정 | 반응이 있던 문장과 없던 문장을 가려 Store 목록, README, 남은 글 수정 | 없음 | 없음 | 없음 | [metrics.md](metrics.md) |

D가 가장 이른 경우에도 3주차는 11월이므로 미국 동부 시간은 서머타임이 끝난(2026-11-01) 뒤의 EST(UTC-5)로 계산했습니다.

## 주마다 할 일

**1주: 한국어 일반·생산성 커뮤니티.** 첫 사용자는 지인과 커뮤니티에서 직접 모읍니다. 앱이 성공한 캡처 10회와 3일이 지난 뒤 한 번 평가를 요청하므로, 설치를 부탁할 때 평가를 따로 부탁하지 않고 며칠 실제로 써 달라고만 합니다. 보상을 걸거나 별 다섯 개를 부탁하는 일은 Store 정책이 금지하는 평점 조작에 해당합니다. 윈도우 포럼과 아이보스 글은 사용자 질문에 하루 안에 답하고, 질문에서 나온 오해는 [review-replies.md](review-replies.md)와 W4 문구 수정 목록에 적어 둡니다.

**2주: 한국 개발자 커뮤니티.** GeekNews Show는 같은 프로젝트를 한동안 다시 올릴 수 없으므로 가장 다듬은 글을 한 번만 올립니다. 링크는 바로 써 볼 수 있는 GitHub 저장소(릴리스와 Store 링크 포함)로 하고, 랜딩 페이지만 링크하지 않습니다. OKKY는 피드백 요청 게시판이므로 끝에 구체적인 질문 세 가지를 둡니다. 디스콰이엇은 월요일에 제품 등록을 먼저 요청해 승인을 받은 뒤 목요일에 제품에 연결한 포스트를 씁니다.

**3주: 영어권.** 영어 개발자 채널은 Show HN으로 정했습니다. MyCapture는 MIT 소스가 공개되어 있고 가입 없이 Store나 포터블 ZIP으로 바로 써 볼 수 있어 Show HN의 조건에 맞고, 개발자라는 타깃과도 겹칩니다. Product Hunt는 출시일 하루의 반응으로 노출이 정해지고 개발자보다 일반 제품 사용자가 많아, 평점과 리뷰가 쌓인 뒤 W4 회고에서 다시 판단합니다. 본문은 첫 댓글에 쓰고 처음 3시간은 댓글에 답합니다. r/software는 수요일에 플레어를 붙여 소프트웨어 소개보다 내용 자체가 쓸모 있는 글(화면 캡처의 개인정보를 PC 안에서 가리는 방법과 한계)로 올립니다. r/Windows11은 허가 없이 앱을 홍보할 수 없고 지금은 허가 신청을 받지 않으므로 글을 올리지 않습니다. 사이드바에서 신청을 다시 받는다고 바뀌었을 때만 준비한 모드메일을 보냅니다.

**4주: 회고와 문구 수정.** 캠페인 ID별 페이지 조회와 전환, 리뷰 내용, 댓글 질문을 [metrics.md](metrics.md)의 표로 모읍니다. 질문이 반복된 내용(예: Snipaste와의 차이, 녹화 소리, 서명 없는 GitHub 파일)은 Store 목록과 README에 미리 답하도록 고칩니다. Store 목록 문구는 [docs/store/listing.md](../../store/listing.md)에서 고치고 다음 제출에 넣습니다.

## 채널 규칙 근거

2026-10-20에 각 채널의 규칙 페이지를 직접 열어 확인했습니다. Reddit은 이 작업의 도구로 reddit.com을 열 수 없어 제3자 사본(2026-09-26에 읽은 규칙)을 근거로 적었으므로, 올리기 전에 사이드바를 한 번 더 확인합니다.

| 채널 | 확인한 규칙 | 근거 |
|---|---|---|
| 윈도우 포럼 개발 / 자작 | 회원이 직접 만든 결과물을 공유하는 곳이고 무료 배포만 가능하며 상업 목적 게재는 제한됩니다. 모든 게시판은 가입 7일 뒤부터 글을 쓸 수 있고, 광고성·홍보성 글과 링크만 있는 글은 금지됩니다. 최근 글 제목은 `[자작] 이름 — 설명` 형식입니다. | [게시판 이용 안내](https://windowsforum.kr/dev/21827413), [이용안내](https://windowsforum.kr/policy/5310234), [게시판](https://windowsforum.kr/dev) |
| 아이보스 무료 툴 공유 | 완전 무료이고 직접 만들었거나 제작에 참여한 도구만 올릴 수 있습니다. 만든 이유, 만든 방식, 해결하는 문제, 사용 방법을 적고, 로그인·데이터 수집 여부와 외부 서비스 사용 범위를 밝혀야 합니다. "최고" 같은 과장은 지양하고, 업데이트는 새 글이 아니라 기존 글 수정이나 댓글로 알립니다. | [이용 규칙(2026-03-23)](https://www.i-boss.co.kr/ab-6884-1), [게시판](https://www.i-boss.co.kr/ab-6883) |
| GeekNews Show | 본인이나 소속 조직이 만든 것은 뉴스가 아니라 Show로 등록합니다. 다른 사람이 직접 써 볼 수 있어야 하고, 블로그 글·소개용 페이지·YouTube 링크는 올릴 수 없습니다. 같은 프로젝트는 일정 기간 다시 등록할 수 없고 큰 업데이트도 예외가 아닙니다. | [이용 가이드라인](https://news.hada.io/guidelines), [Show 목록](https://news.hada.io/show) |
| OKKY 피드백 | 포트폴리오, 사이드 프로젝트, 서비스, 코드 리뷰, 사용성 테스트의 피드백을 요청하는 게시판입니다. 영리 목적의 광고성 글은 예고 없이 삭제됩니다. 실제로 Microsoft Store 링크를 단 Windows 오픈 소스 앱 소개 글이 올라와 있습니다. | [피드백 게시판](https://okky.kr/community/request-for-comments), [홍보·광고 공지](https://okky.kr/articles/1518586) |
| 디스콰이엇 | 본인이 만든 제품만 등록할 수 있고 제품은 작성, 검토 요청, 승인 순서를 거칩니다. 메인 피드에는 제품에 연결한 포스트만 보이며 아티클(메이커로그)은 새로 쓸 수 없습니다. 로그인은 릴레잇 계정의 이메일 링크입니다. | [리뉴얼 공지](https://disquiet.io/announcement) |
| Show HN | 다른 사람이 써 볼 수 있는, 직접 만든 것을 올립니다. 랜딩 페이지와 블로그 글은 해당하지 않고, 제목은 `Show HN`으로 시작합니다. 가입 없이 써 볼 수 있게 하라고 권하며, 지인에게 추천이나 댓글을 부탁하는 것은 허용되지 않습니다. | [Show HN 안내](https://news.ycombinator.com/showhn.html) |
| r/software | 오픈 소스가 아닌 자기 소프트웨어 홍보는 영구 차단 대상이며, 수요일에 맞는 플레어를 달고 소프트웨어 이상의 가치를 주는 글은 예외입니다. 새 프로그램이나 큰 업데이트만 올리고, 결제 없이 모두 쓸 수 있어야 합니다. 카르마 기준이 있습니다. | [제3자 사본(2026-09-26)](https://threadfox.vip/rules/software), [원본 규칙](https://www.reddit.com/r/software/about/rules) |
| r/Windows11 (올리지 않음) | 규칙 7은 허가 없이 소프트웨어·웹사이트·채널을 광고하지 못하게 하고, 허가는 모드메일로 받되 지금은 신청을 받지 않는다고 적혀 있습니다. | [제3자 사본(2026-09-26)](https://threadfox.vip/rules/windows11), [원본 규칙](https://www.reddit.com/r/Windows11/about/rules) |
| 클리앙 (올리지 않음) | 사용기 게시판은 정당한 대가를 내고 경험한 글만 받고 홍보 목적 글을 금지합니다. 새로운소식 게시판은 본인 관련 정보를 사전 허락 없이 올리면 이용 제한 대상이고 작성 조건(가입 500일, 게시물 500개 등)이 높습니다. 직접홍보 게시판은 유료 기업회원 전용입니다. | [사이트 이용규칙](https://www.clien.net/service/board/rule/10707403), [사용기 규칙](https://www.clien.net/service/board/rule/10707407), [새로운소식 규칙](https://www.clien.net/service/board/rule/10707406), [직접홍보 안내](https://www.clien.net/service/board/rule/10707409) |
| Product Hunt (W4에 다시 판단) | 회사 계정은 금지되고 만든 사람이 직접 올릴 수 있습니다. 미국 태평양 시간 00:01 시작을 권하고, 추천을 직접 부탁하지 말고 방문과 댓글을 부탁하라고 안내합니다. | [출시 안내](https://www.producthunt.com/launch) |

Store 쪽 규칙도 함께 지킵니다. 평점과 리뷰를 조작하는 행위는 [Microsoft Store 정책](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)이 금지하고, 리뷰 답변은 1,000자 이하이며 평점을 바꾸는 대가를 제시하거나 광고를 넣을 수 없습니다([평점·리뷰·답변 안내](https://learn.microsoft.com/en-us/windows/apps/publish/analyze-msi-exe/ratings-reviews-performance)). 캠페인 ID는 링크를 누른 뒤 24시간 안에 받은 경우를 전환으로 셉니다([사용자 지정 캠페인 안내](https://learn.microsoft.com/en-us/windows/apps/publish/create-a-custom-app-promotion-campaign)).

## 게시할 때 지킬 것

글은 첫 두 문장 안에 직접 만든 앱이라고 밝힙니다. 같은 날 두 채널에 같은 글을 올리지 않고, 다른 채널의 글 주소를 퍼 나르지 않습니다. 추천, 좋아요, 별점을 부탁하지 않습니다. 댓글에는 게시 당일 안에 답하고, 고칠 결함이 나오면 지원 저장소 이슈로 옮긴 뒤 댓글에 그 주소를 남깁니다. 올린 글의 주소와 날짜는 바로 [metrics.md](metrics.md)에 적습니다.
