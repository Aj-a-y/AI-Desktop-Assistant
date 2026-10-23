# Local Kokoro speech setup

Kokoro is optional. The app keeps its existing Windows `System.Speech` output and falls back to it when Python, a Kokoro package, or model files are missing. Kokoro runs in a separate, low-priority CPU process, so it does not compete for the RTX 2050's limited VRAM with Ollama.

## Approximate downloads

- Kokoro v1.0 int8 ONNX model: about **114 MB**.
- Kokoro v1.0 voice bundle: about **28 MB**.
- Python 3.12 plus `kokoro-onnx` and its ONNX/phonemizer dependencies: additional disk and download space, typically tens to low hundreds of MB depending on the Python distribution and wheel versions.

The model and voice bundle total about **142 MB**. Nothing in this project downloads or installs those files automatically. Kokoro-ONNX supports Python 3.10–3.13; the standard package uses ONNX Runtime's CPU provider unless an accelerated runtime is separately installed. This setup intentionally uses CPU mode. See the [Kokoro-ONNX project](https://github.com/thewh1teagle/kokoro-onnx) and its [model release sizes](https://github.com/thewh1teagle/kokoro-onnx/releases).

## Manual installation

1. Install 64-bit Python 3.12 from python.org. No cloud account or API key is needed.
2. Open PowerShell and create a dedicated environment:

   ```powershell
   $ttsDir = Join-Path $env:LOCALAPPDATA 'DesktopAssistant\Tts'
   py -3.12 -m venv (Join-Path $ttsDir 'venv')
   & (Join-Path $ttsDir 'venv\Scripts\python.exe') -m pip install --upgrade pip
   & (Join-Path $ttsDir 'venv\Scripts\python.exe') -m pip install kokoro-onnx
   ```

3. Download these two files from the upstream [model-files-v1.1 release](https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.1) into `%LOCALAPPDATA%\DesktopAssistant\Tts`:

   - `kokoro-v1.0.int8.onnx`
   - `voices-v1.0.bin`

4. Start the assistant and open the **voice settings** button. Choose **Automatic (Kokoro if installed)** or **Kokoro ONNX**, then select a voice, speed, and volume. The app uses the default paths above. If Python is installed elsewhere, edit `KokoroPythonPath` in `%LOCALAPPDATA%\DesktopAssistant\ollama-settings.json` to the full path of the environment's `python.exe`, then restart the app.

The first spoken response starts the worker and loads the model. Startup and time-to-first-audio are measured and shown in the status area. If setup fails, the app reports the issue and uses Windows speech instead.

## Fallback and controls

Choosing **Windows system voice** uses installed Windows voices. Voice, speed, and volume are saved in `ollama-settings.json`. The existing microphone button still mutes output; **Stop** cancels active synthesis/playback and clears pending speech. Output is queued in order with a six-sentence buffer; a full buffer applies backpressure to the model stream instead of growing memory without limit.
