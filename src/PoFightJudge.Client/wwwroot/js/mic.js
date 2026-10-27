// Records one spoken turn and hands it back as a 16 kHz mono WAV.
//
// The samples are taken straight off the Web Audio graph through the same `pcm-capture` worklet a live fight uses,
// rather than recorded into a container and decoded back out. MediaRecorder writes WebM/Opus, and asking
// decodeAudioData to read that back fails outright on some machines (EncodingError) — which looks, from the page,
// exactly like a microphone that heard nothing. There is no container in this path, so there is nothing to decode:
// what the worklet hands over is already 16 kHz little-endian Int16, and all that is left is a WAV header.
window.PoMic = (function () {
  "use strict";

  /** What the worklet resamples to, and therefore what the header says. */
  const RATE = 16000;

  /** How many bars the meter draws. Eight is enough to see a voice move and few enough to stay legible at 6 rem. */
  const BANDS = 8;

  let stream = null;
  let ctx = null;
  let source = null;
  let worklet = null;
  let capturing = false;

  /** The turn so far: 100 ms frames of little-endian Int16 at RATE, in the order they were spoken. */
  let frames = [];

  /** The last turn stop() handed back, kept for the browser's own transcriber (asr.js) to read. */
  let last = [];

  // The meter runs off a second tap on the same source, so the page can show that the microphone is working while
  // somebody is still deciding what to say.
  let analyser = null;
  let samples = null;
  let spectrum = null;
  let meterFrame = 0;

  function publish(value) {
    try {
      document.documentElement.style.setProperty("--po-mic-level", String(value));
    } catch {
      // No document (a worker, a test host): the meter simply does not move.
    }
  }

  // The spectrum, as eight custom properties. Log-spaced, because speech lives in the bottom fifth of the range
  // and eight linear bands would be seven empty bars and one that moves.
  function publishBands() {
    if (!analyser || !spectrum) {
      return;
    }

    analyser.getByteFrequencyData(spectrum);
    const top = Math.floor(spectrum.length * 0.45);
    let from = 1;
    for (let band = 0; band < BANDS; band++) {
      const to = Math.max(from + 1, Math.round(Math.pow(top, (band + 1) / BANDS)));
      let peak = 0;
      for (let i = from; i < to && i < spectrum.length; i++) {
        peak = Math.max(peak, spectrum[i]);
      }

      from = to;
      try {
        document.documentElement.style.setProperty("--po-band-" + band, (peak / 255).toFixed(3));
      } catch {
        return;
      }
    }
  }

  function clearBands() {
    for (let band = 0; band < BANDS; band++) {
      try {
        document.documentElement.style.setProperty("--po-band-" + band, "0");
      } catch {
        return;
      }
    }
  }

  // The same custom property the live fight publishes, so one meter reads either. Painted by the browser rather
  // than pushed from .NET: a level is sixty small changes a second and none of them is worth a round trip.
  const CLIPPING = 0.98;
  let sparkedAt = 0;

  function pump() {
    if (!analyser) {
      return;
    }

    const now = level();
    publish(now);
    publishBands();

    // Pinned at the top of the meter is a microphone being shouted into. The lamp throws sparks, at most once a
    // second, so it reads as a warning rather than as the meter being on fire.
    if (now >= CLIPPING && window.PoParticles && performance.now() - sparkedAt > 1000) {
      sparkedAt = performance.now();
      window.PoParticles.burst(".meter", "sparks", ".lamp");
    }

    meterFrame = requestAnimationFrame(pump);
  }

  function release() {
    if (meterFrame) {
      cancelAnimationFrame(meterFrame);
      meterFrame = 0;
    }

    publish(0);
    clearBands();

    if (worklet) {
      try {
        worklet.port.onmessage = null;
        worklet.disconnect();
      } catch {
        // Already torn down with its context.
      }

      worklet = null;
    }

    if (source) {
      try {
        source.disconnect();
      } catch {
        // Same.
      }

      source = null;
    }

    if (stream) {
      stream.getTracks().forEach(function (t) { t.stop(); });
      stream = null;
    }

    if (ctx) {
      ctx.close().catch(function () { /* already closing */ });
      ctx = null;
    }

    analyser = null;
    samples = null;
    spectrum = null;
    capturing = false;
  }

  /** How loud the microphone is right now, 0 to 1. Root-mean-square, so it tracks speech rather than clicks. */
  function level() {
    if (!analyser) {
      return 0;
    }

    analyser.getFloatTimeDomainData(samples);
    let sum = 0;
    for (let i = 0; i < samples.length; i++) {
      sum += samples[i] * samples[i];
    }

    // Speech sits well below full scale, so the reading is lifted to make an ordinary voice fill the meter.
    return Math.min(1, Math.sqrt(sum / samples.length) * 8);
  }

  /** The name a browser gave a failure, in a form worth showing somebody. */
  function nameOf(err) {
    if (!err) {
      return "Unknown";
    }

    return err.name || err.message || String(err);
  }

  // The chosen microphone, when there is one. `exact` on purpose: silently falling back to the default is how
  // somebody ends up recording on the wrong device after deliberately picking one.
  function constrain(deviceId) {
    const audio = { channelCount: 1, echoCancellation: true, noiseSuppression: true };
    if (deviceId) {
      audio.deviceId = { exact: deviceId };
    }

    return audio;
  }

  // The microphones this browser will admit to. Labels are blank until permission has been given once, which is why
  // the setup screen asks for the microphone before it offers the list.
  async function devices() {
    if (!navigator.mediaDevices || typeof navigator.mediaDevices.enumerateDevices !== "function") {
      return [];
    }

    try {
      const all = await navigator.mediaDevices.enumerateDevices();
      return all
        .filter(function (d) { return d.kind === "audioinput"; })
        .map(function (d, i) {
          return { id: d.deviceId, label: d.label || "Microphone " + (i + 1) };
        });
    } catch (err) {
      return [];
    }
  }

  // Returns "" when recording started, or the browser's error name when it did not. The caller turns that name into
  // a sentence: a refused permission and a missing device need different advice.
  async function start(deviceId) {
    try {
      if (capturing) {
        return "";
      }

      if (!navigator.mediaDevices || typeof navigator.mediaDevices.getUserMedia !== "function" || typeof AudioWorkletNode === "undefined") {
        return "NotSupportedError";
      }

      stream = await navigator.mediaDevices.getUserMedia({ audio: constrain(deviceId) });

      // The device's own rate, not one asked for: a context pinned to 16 kHz is refused outright where the audio
      // device will not run at it. The worklet does the rate conversion, and it does it the same way for everyone.
      ctx = new (window.AudioContext || window.webkitAudioContext)();
      if (ctx.state === "suspended") {
        try {
          await ctx.resume();
        } catch {
          // Needs a gesture. The click that started the match is usually enough; stop() says so if it was not.
        }
      }

      // Absolute: a watch lives at /watch/play, where a relative path would resolve to /watch/js/...
      // A failed fetch of the worklet rejects with AbortError, the same name a stuck microphone gets; it is renamed
      // so the page can tell "reload" from "the microphone would not start".
      try {
        await ctx.audioWorklet.addModule("/js/pcm-worklet.js");
      } catch (e) {
        const loadError = new Error("the audio worklet did not load");
        loadError.name = "AudioLoadError";
        throw loadError;
      }

      source = ctx.createMediaStreamSource(stream);

      analyser = ctx.createAnalyser();
      analyser.fftSize = 1024;
      analyser.smoothingTimeConstant = 0.6;
      samples = new Float32Array(analyser.fftSize);
      spectrum = new Uint8Array(analyser.frequencyBinCount);
      source.connect(analyser);

      frames = [];
      worklet = new AudioWorkletNode(ctx, "pcm-capture", { numberOfInputs: 1, numberOfOutputs: 0 });
      worklet.port.onmessage = function (e) {
        if (e.data && e.data.pcm) {
          frames.push(e.data.pcm);
        }
      };

      source.connect(worklet);
      capturing = true;
      meterFrame = requestAnimationFrame(pump);
      return "";
    } catch (err) {
      release();
      return nameOf(err);
    }
  }

  // Stops and returns { wav, error }: the clip as base64 WAV, or the reason there is not one. Every step that can
  // fail says which step it was, because "nothing came through the microphone" is the wrong thing to tell somebody
  // who watched the meter move for thirty seconds.
  function stop() {
    if (!capturing) {
      return { wav: "", error: "NotRecording" };
    }

    const captured = frames;
    frames = [];
    last = captured;
    const state = ctx ? ctx.state : "closed";
    release();

    if (captured.length === 0) {
      // A suspended context produces no frames at all, and that is a browser waiting for a gesture rather than a
      // room that stayed quiet. They are different problems and they get different names.
      return { wav: "", error: state === "running" ? "NothingCaptured" : "ContextSuspended" };
    }

    try {
      return { wav: base64(wav(captured, RATE)), error: "" };
    } catch (err) {
      return { wav: "", error: "EncodeFailed:" + nameOf(err) };
    }
  }

  function cancel() {
    frames = [];
    last = [];
    release();
  }

  // The frames, with a WAV header in front of them. Nothing is converted here: the worklet already produced 16 kHz
  // little-endian mono Int16, which is what the header goes on to describe.
  function wav(chunks, rate) {
    let length = 0;
    for (let i = 0; i < chunks.length; i++) {
      length += chunks[i].length;
    }

    const out = new Uint8Array(44 + length);
    const view = new DataView(out.buffer);

    function text(offset, s) {
      for (let i = 0; i < s.length; i++) {
        view.setUint8(offset + i, s.charCodeAt(i));
      }
    }

    text(0, "RIFF");
    view.setUint32(4, 36 + length, true);
    text(8, "WAVE");
    text(12, "fmt ");
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, 1, true);
    view.setUint32(24, rate, true);
    view.setUint32(28, rate * 2, true);
    view.setUint16(32, 2, true);
    view.setUint16(34, 16, true);
    text(36, "data");
    view.setUint32(40, length, true);

    let at = 44;
    for (let i = 0; i < chunks.length; i++) {
      out.set(chunks[i], at);
      at += chunks[i].length;
    }

    return out;
  }

  // Chunked so a minute of speech does not blow the argument limit of String.fromCharCode.
  function base64(bytes) {
    let binary = "";
    const step = 0x8000;
    for (let i = 0; i < bytes.length; i += step) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
    }

    return window.btoa(binary);
  }

  // The last turn as Float32 in [-1, 1) at RATE, which is what Whisper reads. A fresh array each time: the caller
  // transfers it to a worker.
  function turnSamples() {
    let length = 0;
    last.forEach(function (bytes) { length += bytes.byteLength >> 1; });
    const out = new Float32Array(length);
    let at = 0;
    last.forEach(function (bytes) {
      const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      for (let i = 0; i + 1 < bytes.byteLength; i += 2) {
        out[at++] = view.getInt16(i, true) / 32768;
      }
    });
    return out;
  }

  return { start: start, stop: stop, cancel: cancel, level: level, devices: devices, samples: turnSamples };
})();
