# Local OCR

Use **Recognize text** on an image to save its text as a new history entry. The original image stays in history. OCR runs locally on the CPU; it does not need the optional AI package.

The worker uses [pure-onnx-ocr 0.3.1](https://github.com/siska-tech/pure-onnx-ocr/tree/59a6c2c33c72e4dc8e7e5500f544d6674b0e6643), PaddlePaddle's Cyrillic PP-OCRv5 mobile recognizer, and PP-OCRv5 mobile detector. `tools/ocr/Build-Ocr.ps1` pins revisions and SHA-256 hashes; `Cargo.lock` pins Rust dependencies. Licenses are included in the generated bundle.

```powershell
pwsh -NoProfile -File tools/ocr/Build-Ocr.ps1
```

The app decrypts image bytes in the background and sends them to the hidden worker over stdin. No plaintext temporary PNG is needed. Each operation starts its own worker with four inference threads and BelowNormal priority; cancellation or app exit terminates it through a kill-on-close job object. The worker does not use the network and includes the C runtime it needs.

Input dimensions, encoded size, response size, JSON fields, confidence, and geometry are validated. Images above a 2048 px long edge are reduced for detection. Inputs over 80 megapixels or 128 MiB are rejected before decoding. User storage limits still apply; oversized results are refused rather than silently truncated.

`OcrText` reconstructs reading order from geometry, joins line fragments, and corrects Cyrillic/Latin lookalikes only when the word has clear script evidence. Ambiguous scripts, low confidence, or column/table layouts can require review. A result is saved before any clipboard replacement; a newer copy or paste prevents automatic replacement. Deleted source entries and failed saves do not publish a result.

## Image translation

Translation requires the optional local AI runtime. It replaces recognized labels with Russian text in a new image, preserving the original dimensions and history entry. `TranslatedImage` samples background and ink colors and fits bundled Plex text into each OCR region. It rejects missing, empty, or overflowing translations rather than saving a partial image. Flat backgrounds work better than detailed textures; there is no generative background reconstruction.

## Accuracy and checks

On the 26-image synthetic corpus used during development, character error rate after layout and script correction was 1.31%; two-column error rate was 0%. Those figures do not predict accuracy on arbitrary screenshots. Review important output, especially code, paths, and tables.

`--ocr-selftest <corpus-directory>` runs the OCR integration checks; `--translation-selftest <test-directory>` exercises translation with synthetic images and a live local model. Both require the relevant built runtime. Check available GPU memory before running translation tests.
