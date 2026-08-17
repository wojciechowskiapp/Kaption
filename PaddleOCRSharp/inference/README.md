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

The compatibility profile uses PP-OCRv4 Mobile models from the official
PaddleOCR model family. The runtime copies are ONNX conversions pinned here so
clean source builds and official releases use identical assets:

- Detection `Det/V4/PP-OCRv4_mobile_det_infer/slim.onnx`:
  `C0F2E256776E81D9E38F49E7CC2A37864A326EE8097E84ADF30A8E0EBCC0B24B`
- English recognition `Rec/V4/PP-OCRv4_mobile_rec_infer/slim.onnx`:
  `DF79157F86AA181EE0DAA43364203CFC892F98E2A1B425614A1C98E0B96D7393`
- Japanese recognition `Rec/V4/jp_PP-OCRv4_mobile_rec_infer/slim.onnx`:
  `E1075A67DBA758ECFC7EBC78A10AE61C95AC8FB66A9C86FAB5541E33F085CB7A`

Upstream model catalogue:
https://paddlepaddle.github.io/PaddleOCR/main/version2.x/ppocr/model_list.html
