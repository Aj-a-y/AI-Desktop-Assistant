"""Persistent, local Kokoro ONNX worker. Communicates over JSON lines on stdio."""

import json
import os
import sys
import wave


def write_message(message):
    sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main():
    try:
        import numpy as np
        from kokoro_onnx import Kokoro

        model_path, voices_path = sys.argv[1], sys.argv[2]
        tts = Kokoro(model_path, voices_path)
    except Exception as error:  # Report startup errors through the protocol, not raw stdout.
        write_message({"ready": False, "error": str(error)})
        return 2

    write_message({"ready": True})
    for line in sys.stdin:
        try:
            request = json.loads(line)
            text = str(request.get("text", "")).strip()
            voice = str(request.get("voice", "af_heart"))
            speed = float(request.get("speed", 1.0))
            output_path = os.path.abspath(str(request["output_wav"]))
            if not text or len(text) > 3000:
                raise ValueError("Speech text must contain between 1 and 3000 characters.")

            audio, sample_rate = tts.create(
                text,
                voice=voice,
                speed=speed,
                lang="en-us",
            )
            pcm = (np.clip(audio, -1.0, 1.0) * 32767.0).astype(np.int16)
            os.makedirs(os.path.dirname(output_path), exist_ok=True)
            with wave.open(output_path, "wb") as output:
                output.setnchannels(1)
                output.setsampwidth(2)
                output.setframerate(sample_rate)
                output.writeframes(pcm.tobytes())
            write_message({"ok": True})
        except Exception as error:
            write_message({"ok": False, "error": str(error)})

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
