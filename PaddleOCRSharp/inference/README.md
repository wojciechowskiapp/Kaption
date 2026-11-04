# OCR model assets

Kaption's recommended profile uses the official Apache-2.0 PP-OCRv6 Small
ONNX models published by the PaddleOCR project:

- Detection: https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx
- Recognition: https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx

Pinned file hashes (SHA-256):

- Detection `inference.onnx`: `D73E0058B7A8086BBD57F3D10B8BCD4FF95363F67E06E2762B5E814FE9C9410E`
- Recognition `inference.onnx`: `5435FD747C9E0EFE15A96D0B378D5BD157E9492ED8FD80EDF08F30D02FA24634`

The V6 recognition dictionary and preprocessing metadata are stored in the
adjacent official `inference.yml`.

The compatibility profile keeps the PP-OCRv4 Mobile detector. English reads
with the official en_PP-OCRv5 Mobile recognizer (since session 58), Japanese
with the PP-OCRv4 Mobile Japanese recognizer.

- English recognition `Rec/V5/en_PP-OCRv5_mobile_rec_infer/inference.onnx`:
  `B5F833DFC5D0EB71DA397B4EFA06EBEEE9B431B690A47D6AF40D77D8EABC557F`.
  Source: https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec_onnx at
  commit `3fafbc3b5dcf93dd72add9f48368be8a3a2cd33b`. The adjacent official
  `inference.yml` (SHA-256 `27E91D0582F40168AA218303C76E184BC78FA7A5D105AAD0CFBAD8458B441067`
  for the upstream LF bytes; a Windows checkout may hold CRLF) holds its
  436-character dictionary.

The two PP-OCRv4 files are ONNX conversions pinned here so clean source builds
and official releases use identical assets:

- Detection `Det/V4/PP-OCRv4_mobile_det_infer/slim.onnx`:
  `C0F2E256776E81D9E38F49E7CC2A37864A326EE8097E84ADF30A8E0EBCC0B24B`
- Japanese recognition `Rec/V4/jp_PP-OCRv4_mobile_rec_infer/slim.onnx`:
  `E1075A67DBA758ECFC7EBC78A10AE61C95AC8FB66A9C86FAB5541E33F085CB7A`

Upstream model catalogue:
https://paddlepaddle.github.io/PaddleOCR/main/version2.x/ppocr/model_list.html
