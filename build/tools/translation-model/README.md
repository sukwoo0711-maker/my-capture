# OPUS-MT 영한 번역 모델 가중치 정리

`dedupe_opus_onnx.py`는 앱이 처음 쓸 때 내려받는 OPUS-MT tc-big 영어→한국어 int8 ONNX 모델의 크기를 줄입니다. 인코더, 디코더, 과거 값 사용 디코더 세 그래프는 같은 가중치를 각각 따로 담고 있습니다. 이 도구는 세 그래프에서 `raw_data`가 4,096바이트 이상인 최상위 initializer 394개를 내용이 같은 것끼리 합쳐 256개의 고유한 값으로 만들고, 각 값을 `opus-mt-en-ko-weights-<n>.bin`에 한 번만 저장합니다. 그래프는 ONNX 외부 데이터(`location`, `offset`, `length`)로 그 파일을 가리키도록 다시 씁니다. 텐서 값, 노드, 메타데이터는 바꾸지 않으며, 같은 입력이면 항상 같은 바이트가 나옵니다.

ONNX 파일 세 개의 합계는 433,503,010바이트에서 244,507,815바이트(그래프 3개 839,847 + weights 4개 243,667,968)로 188,995,195바이트(43.6%) 줄어듭니다. 토크나이저 파일 두 개를 더한 내려받기 전체는 246,256,561바이트입니다.

## 원본 모델과 라이선스

- 번역 모델: [Helsinki-NLP/opus-mt-tc-big-en-ko](https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-ko), 리비전 `ae8606b7b29a495f31ce679cee2007f536a3a5ce`. 개발은 Language Technology Research Group at the University of Helsinki(OPUS-MT 프로젝트)입니다. 라이선스는 [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)입니다.
- ONNX 내보내기와 동적 int8 양자화: Hugging Face의 [noticemkjung/opus-mt-tc-big-en-ko-ONNX](https://huggingface.co/noticemkjung/opus-mt-tc-big-en-ko-ONNX), 리비전 `aea67600956847d47609f96b364f33a734007d50`.
- `source.spm`과 `vocab.json`은 번역 모델 저장소의 같은 리비전에서 받은 파일입니다.

변경 내용은 다음과 같습니다.

- 모델의 ONNX 변환과 int8 양자화는 위 ONNX 저장소에서 이루어졌고, 이 도구는 관여하지 않습니다.
- 이 도구는 양자화된 ONNX의 가중치를 중복 없이 별도 파일로 옮길 뿐입니다. 텐서 값은 바이트 단위로 같습니다.
- `source.spm`과 `vocab.json`은 이름만 바꿔 그대로 복사합니다.

## 토크나이저 어휘 구조

이 모델은 입력과 출력의 어휘가 분리되어 있습니다. 입력 id는 `source.spm`에서 조각이 놓인 위치이고, `vocab.json`은 `target.spm`과 같은 순서의 출력 id 표입니다. `vocab.json`으로 입력 문장을 id로 바꾸면 번역이 깨집니다.

## 실행

```powershell
python -I -X utf8 build/tools/translation-model/dedupe_opus_onnx.py --source <입력 폴더> --output <출력 폴더>
```

입력 폴더는 다음 구조여야 합니다. 파일 이름과 내용은 아래 '입력 파일' 표와 같아야 합니다.

```text
<입력 폴더>/onnx/encoder_model_quantized.onnx
<입력 폴더>/onnx/decoder_model_quantized.onnx
<입력 폴더>/onnx/decoder_with_past_model_quantized.onnx
<입력 폴더>/source.spm
<입력 폴더>/vocab.json
```

Python 3.12와 표준 라이브러리만 필요합니다. 출력 폴더는 비어 있거나 없어야 합니다. 도구는 먼저 입력 파일 다섯 개의 SHA-256을 표의 값과 대조하고, 하나라도 다르면 아무것도 쓰지 않고 중단합니다. 실행에는 몇 초가 걸립니다.

## 배치 규칙

- 최상위 initializer 중 `raw_data`가 4,096바이트 이상인 것만 옮깁니다. 더 작은 텐서, `float_data`·`int32_data`처럼 형식 지정 필드를 쓰는 텐서, Constant 노드의 속성 텐서, 중첩 그래프의 텐서는 그대로 둡니다. 이번 입력에서는 `raw_data`를 가진 텐서 394개가 모두 옮겨지고, 4바이트짜리 양자화 `_scale`·`_zero_point` 스칼라 304개(`float_data`·`int32_data`)가 그래프에 남습니다.
- 데이터 형식, dims, 바이트의 SHA-256이 같은 텐서는 한 번만 저장합니다. 인코더, 디코더, 과거 값 사용 디코더 순서로 그래프 안 순서대로 처음 나온 텐서가 저장 위치를 정합니다.
- weights 파일 하나는 64 MiB(67,108,864바이트) 이하이고 텐서를 나누어 담지 않습니다. 다음 텐서가 현재 파일에 들어가지 않으면 새 파일을 시작합니다. 1 MiB 이상인 텐서는 4,096바이트 경계에서, 그보다 작은 텐서는 64바이트 경계에서 시작하며 빈 곳은 0으로 채웁니다.
- 옮긴 텐서는 `raw_data`가 빠지고 `external_data` 항목 `location`·`offset`·`length`와 `data_location = EXTERNAL`이 붙습니다. `location`은 weights 파일 이름이므로, 세 그래프와 weights 파일 네 개를 같은 폴더에 두고 그래프 경로로 세션을 만들면 onnxruntime이 weights 파일을 읽습니다.

## 입력 파일

ONNX 세 개는 `https://huggingface.co/noticemkjung/opus-mt-tc-big-en-ko-ONNX/resolve/aea67600956847d47609f96b364f33a734007d50/<경로>`에서, 나머지 두 개는 `https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-ko/resolve/ae8606b7b29a495f31ce679cee2007f536a3a5ce/<경로>`에서 받습니다.

| 파일 | 바이트 | SHA-256 |
| --- | --- | --- |
| `onnx/encoder_model_quantized.onnx` | 109,777,877 | `2056A550237F923C9AACA2FBBCF8C623D69E0EDD3E06780B86E09757ABB00F43` |
| `onnx/decoder_model_quantized.onnx` | 168,199,418 | `292E6A8CDF39A17EA509F5B92D45F77284366C57EE111F0E716504AF63DCA4C1` |
| `onnx/decoder_with_past_model_quantized.onnx` | 155,525,715 | `6B5321765CB284CAF39CEEE468F623AE2F52250A6C00DB40D178417B57C3E784` |
| `source.spm` | 789,870 | `3D0591E65C49541D82F48DF33D7B322C3D4EE7AA0EE8747F9A7F9355DBF22C95` |
| `vocab.json` | 958,876 | `9E03014979D240F6A6D1C5DC53CD14F4841351326D345D437D48D115EFF67B5F` |

## 출력 파일

`manifest.json`에는 아래 파일과 입력 파일의 이름, 바이트, SHA-256, 입력 URL, 합계가 들어 있습니다.

| 파일 | 바이트 | SHA-256 |
| --- | --- | --- |
| `opus-mt-en-ko-encoder.onnx` | 150,396 | `68B8712EDF70DE3E02D693CD192D5162D1288A63AE3F5B853789FC3715CC81EE` |
| `opus-mt-en-ko-decoder.onnx` | 366,443 | `246718FA107A2CD96EC9D3DE4A736303E56D8E66E734180C2E0EF6F9735378E1` |
| `opus-mt-en-ko-decoder-with-past.onnx` | 323,008 | `2973DC754D0157C614D2F729F353C0B476C4F4B444A06CEB792E44F52DA93B82` |
| `opus-mt-en-ko-weights-1.bin` | 63,500,288 | `BF640EE406A2C7676DB2582696DA71951882DB357329B0FB631074241A32482D` |
| `opus-mt-en-ko-weights-2.bin` | 66,658,304 | `B64E4CE1120BCBB7B125BFA601569E12AA531F2ACAFB6A6CB82ACAC4F8352E09` |
| `opus-mt-en-ko-weights-3.bin` | 67,108,864 | `BC78727FC6D0671E94097919842220084A25A6EB64218B0AEF275A54D1E5817F` |
| `opus-mt-en-ko-weights-4.bin` | 46,400,512 | `EDADB3B70C7F35623C79B0A47CE4B9D935318F03A95B2DDFE71A83B4998716D8` |
| `opus-mt-en-ko-source.spm` | 789,870 | `3D0591E65C49541D82F48DF33D7B322C3D4EE7AA0EE8747F9A7F9355DBF22C95` |
| `opus-mt-en-ko-vocab.json` | 958,876 | `9E03014979D240F6A6D1C5DC53CD14F4841351326D345D437D48D115EFF67B5F` |
| `manifest.json` | 3,377 | `AD256037CE7C29BA5AA5ACE087EE2D47AB06CD1A0A7109A24D9F65D7BE580589` |

## 한영 모델 (opus-mt-tc-big-ko-en)

핀 번역은 앱 표시 언어를 목표로 하므로 영어 표시에서는 한국어 글을 영어로 번역합니다. 한영 모델은 영한 모델과 같은 방식으로 만듭니다.

- 원본 모델: [Helsinki-NLP/opus-mt-tc-big-ko-en](https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-ko-en), 리비전 `fa26583a41d95346933f26b4cb8f9b700da0d445`, 라이선스 CC BY 4.0. 이 모델의 ONNX 내보내기는 공개되어 있지 않아 `export_quantize_ko_en.py`로 직접 만듭니다(optimum 1.27.0, opset 14, onnxruntime 1.30.0 동적 int8 양자화).
- `dedupe_opus_onnx_ko_en.py`는 영한 도구를 한영 입력 해시와 파일 이름에 맞춘 사본입니다. 세 그래프의 같은 가중치를 `opus-mt-ko-en-weights-<n>.bin` 네 개에 한 번만 담습니다. 출력은 한영 9개 파일, 합계 246,480,501바이트입니다. 크기와 SHA-256은 `TranslationModelCatalog.KoreanToEnglish`에 고정되어 있습니다.
- 어휘 구조는 영한과 같습니다. 입력 id는 `source.spm`의 조각 위치이고 `vocab.json`은 출력 id 표입니다. Hugging Face의 기본 토크나이저 설정(`separate_vocabs=false`)으로 한국어 입력을 `vocab.json`에서 찾으면 번역이 깨집니다.
- 출시 때 아홉 파일을 `translation-models-2` 사전 릴리스에 올려야 앱이 내려받을 수 있습니다. 이 PR은 파일을 올리지 않았습니다.
