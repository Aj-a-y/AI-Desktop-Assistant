# Local voice pipeline

## Current implementation

- Push-to-talk is available through **Hold to talk**. Hold the mouse button or Space/Enter while speaking; release it to transcribe. Starting push-to-talk cancels the active Ollama turn and stops/clears queued speech immediately.
- The existing **Talk** button still enables the continuous, silence-detected mode. It uses local Whisper and the existing RMS energy detector; the pause threshold and input microphone are configurable in Voice settings.
- Assistant responses use half-duplex audio handling. Microphone frames are ignored from the start of a spoken response through its final audio, so the assistant does not transcribe its own voice. Raw audio exists only in memory and is discarded after recognition.
- The state label shows Idle, Listening, Transcribing, Thinking, Speaking, or Error. Voice request timing is shown after a completed voice request.
- The push-to-talk buffer is bounded to 45 seconds. Audio-device and microphone-permission failures appear in the state/status area; choose another input in settings and retry.

## Wake-word investigation

Wake-word detection is not enabled yet. The first implementation should be exercised before adding an always-on microphone loop. `openWakeWord` is a plausible free, offline option: its official project documents Windows support through ONNX Runtime and local Python installation. A helper process would still need the runtime and model files, and the project has not downloaded or installed them. See the [openWakeWord project](https://github.com/dscripka/openWakeWord).

For voice activity detection, the current RMS detector needs no extra runtime and works with the existing 16 kHz capture. Silero VAD also has CPU ONNX options, including a small quantized model, but it adds a model/runtime dependency. See the [Silero VAD project](https://github.com/snakers4/silero-vad) and its [ONNX model size notes](https://k2-fsa.github.io/sherpa/onnx/vad/silero-vad.html). No new runtime or model was downloaded for this phase.

## Manual check

1. Start the app and open Voice settings. Select the intended microphone and set the silence pause.
2. Hold **Hold to talk**, speak a short phrase, then release. Confirm it transcribes and replies aloud.
3. While a reply is being generated or spoken, hold **Hold to talk** and say a correction. Confirm the old response stops and the correction is handled as the next turn.
4. Start continuous **Talk** mode and verify the state label moves through Listening, Transcribing, Thinking, and Speaking. Confirm it returns to Listening after speech ends.
5. Test with microphone access disabled or the selected device disconnected. Confirm the state changes to Error, then select the Windows default or another microphone and retry.
