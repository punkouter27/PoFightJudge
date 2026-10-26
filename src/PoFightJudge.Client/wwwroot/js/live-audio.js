// PoLive: the audio of a live fight. Microphone frames go up to .NET at 16 kHz; the host's voice comes back as
// 24 kHz PCM and is scheduled gaplessly. Kept apart from PoAudio, which plays whole synthesized lines for a watch:
// the two have different contracts and only one of them is ever on screen at a time.
//
// Levels are published as CSS custom properties (--po-mic-level, --po-host-level) so meters animate in the browser
// rather than crossing into .NET sixty times a second.
window.PoLive = (function () {
  "use strict";

  const OUTPUT_RATE = 24000;

  let ctx = null;
  let hostIn = null;
  let analyser = null;
  let levelData = null;
  let stream = null;
  let worklet = null;
  let source = null;
  let dotnet = null;
  let scheduled = [];
  let nextStart = 0;
  let micLevel = 0;

  // Seconds the microphone has heard a voice since capture started, added up frame by frame. The live share of the
  // room is counted from this: a snapshot comes once a second, far too seldom to sample a voice by.
  let voiced = 0;

  /** Where a frame counts as voiced, on the meter's curve: above the hiss of a quiet room, below quiet speech. */
  const VOICED = 0.15;

  // Seconds of it that were loud: raised voices, not ordinary speech. The calm meter divides this by each fighter's
  // talk time. A tuning knob — the microphone runs automatic gain, so this was set by ear, not measured.
  let loud = 0;
  const LOUD = 0.75;
  let hostLevel = 0;
  let meterTimer = 0;
  let micAnalyser = null;
  let micSpectrum = null;

  /** The same eight bars the watch meter draws, so one component reads either recorder. */
  const BANDS = 8;

  function setVar(name, value) {
    document.documentElement.style.setProperty(name, value.toFixed(3));
  }

  // Log-spaced, because speech lives in the bottom fifth of the range: eight linear bands would be seven empty
  // bars and one that moves.
  function publishBands() {
    if (!micAnalyser || !micSpectrum) {
      return;
    }

    micAnalyser.getByteFrequencyData(micSpectrum);
    const top = Math.floor(micSpectrum.length * 0.45);
    let from = 1;
    for (let band = 0; band < BANDS; band++) {
      const to = Math.max(from + 1, Math.round(Math.pow(top, (band + 1) / BANDS)));
      let peak = 0;
      for (let i = from; i < to && i < micSpectrum.length; i++) {
        peak = Math.max(peak, micSpectrum[i]);
      }

      from = to;
      setVar("--po-band-" + band, peak / 255);
    }
  }

  function clearBands() {
    for (let band = 0; band < BANDS; band++) {
      setVar("--po-band-" + band, 0);
    }
  }

  function pumpLevels() {
    if (!ctx || !analyser) {
      return;
    }

    analyser.getByteTimeDomainData(levelData);
    let sum = 0;
    for (let i = 0; i < levelData.length; i++) {
      const v = (levelData[i] - 128) / 128;
      sum += v * v;
    }

    // Curved, because a linear RMS meter sits near zero for ordinary speech and tells the room nothing.
    hostLevel = Math.min(1, Math.pow(Math.sqrt(sum / levelData.length) * 3.2, 0.7));
    setVar("--po-host-level", hostLevel);
    setVar("--po-mic-level", Math.min(1, Math.pow(micLevel * 3.2, 0.7)));
    publishBands();
  }

  let out = null;

  window.addEventListener("po-volume", function (e) {
    if (out) {
      out.gain.value = e.detail;
    }
  });

  async function ensureContext() {
    if (!ctx) {
      ctx = new (window.AudioContext || window.webkitAudioContext)();
      hostIn = ctx.createGain();
      analyser = ctx.createAnalyser();
      analyser.fftSize = 512;
      analyser.smoothingTimeConstant = 0.35;
      levelData = new Uint8Array(analyser.fftSize);
      out = ctx.createGain();
      out.gain.value = window.PoSfx ? window.PoSfx.volume() : 1;
      hostIn.connect(analyser);
      analyser.connect(out);
      out.connect(ctx.destination);
    }

    // Started here rather than with the context: stop() clears it and keeps the context, so a second fight in the
    // same tab used to get a meter that never moved.
    if (!meterTimer) {
      meterTimer = window.setInterval(pumpLevels, 100);
    }

    if (ctx.state === "suspended") {
      try {
        await ctx.resume();
      } catch {
        // Needs a user gesture; the click that starts the fight will do it.
      }
    }

    return ctx;
  }

  // Starts capture. `ref` is a DotNetObjectReference exposing [JSInvokable] OnAudioFrame(byte[]).
  // Returns the device sample rate, or an error name when the microphone was refused.
  async function startCapture(ref, deviceId) {
    try {
      dotnet = ref;
      voiced = 0;
      loud = 0;
      await ensureContext();

      // The chosen microphone, when there is one. `exact` on purpose: silently falling back to the default is how
      // somebody ends up recording a fight on the wrong device after deliberately picking one.
      const audio = { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true };
      if (deviceId) {
        audio.deviceId = { exact: deviceId };
      }

      stream = await navigator.mediaDevices.getUserMedia({ audio: audio });

      // Absolute: the fight page lives at /fight/{id}, where a relative path would resolve to /fight/js/...
      // Renamed on failure, as in mic.js: a worklet that did not load is not a microphone that would not start.
      try {
        await ctx.audioWorklet.addModule("/js/pcm-worklet.js");
      } catch (e) {
        const loadError = new Error("the audio worklet did not load");
        loadError.name = "AudioLoadError";
        throw loadError;
      }
      source = ctx.createMediaStreamSource(stream);

      // A second tap on the same source, for the meter's bars. The worklet reports one number a frame; a voice
      // that can be seen as a shape is what tells somebody the microphone is hearing them and not the room.
      micAnalyser = ctx.createAnalyser();
      micAnalyser.fftSize = 1024;
      micAnalyser.smoothingTimeConstant = 0.6;
      micSpectrum = new Uint8Array(micAnalyser.frequencyBinCount);
      source.connect(micAnalyser);

      worklet = new AudioWorkletNode(ctx, "pcm-capture", { numberOfInputs: 1, numberOfOutputs: 0 });
      worklet.port.onmessage = function (e) {
        micLevel = e.data.rms;
        const level = Math.min(1, Math.pow(micLevel * 3.2, 0.7));
        const seconds = e.data.pcm.byteLength / 2 / 16000;
        if (level > VOICED) {
          voiced += seconds;
        }
        if (level > LOUD) {
          loud += seconds;
        }
        if (dotnet) {
          // A dropped frame is better than a stalled microphone: the next one is 100 ms away.
          dotnet.invokeMethodAsync("OnAudioFrame", e.data.pcm).catch(function () {});
        }
      };

      source.connect(worklet);
      return { sampleRate: ctx.sampleRate, error: "" };
    } catch (err) {
      await stop();
      return { sampleRate: 0, error: (err && err.name) || "NotAllowedError" };
    }
  }

  // Queues 16-bit little-endian 24 kHz mono PCM so consecutive chunks play without a seam.
  function play(bytes) {
    if (!ctx || !bytes || bytes.byteLength < 2) {
      return;
    }

    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const samples = bytes.byteLength >> 1;
    const buffer = ctx.createBuffer(1, samples, OUTPUT_RATE);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < samples; i++) {
      data[i] = view.getInt16(i * 2, true) / 32768;
    }

    const src = ctx.createBufferSource();
    src.buffer = buffer;
    src.connect(hostIn);

    // A little ahead of now, so a late chunk never tries to start in the past and click.
    const startAt = Math.max(ctx.currentTime + 0.05, nextStart);
    src.start(startAt);
    nextStart = startAt + buffer.duration;
    scheduled.push(src);
    src.onended = function () {
      scheduled = scheduled.filter(function (s) { return s !== src; });
    };
  }

  // The host was interrupted: drop what is queued rather than talking over whoever cut in.
  function clear() {
    for (const src of scheduled) {
      try {
        src.stop();
      } catch {
        // Already finished.
      }
    }

    scheduled = [];
    nextStart = 0;
  }

  /**
   * Dips the host for `seconds` and brings them back, so a phase stinger lands over the top of them rather than
   * fighting them. Called by PoSfx on the same beat as the effect; the microphone is untouched.
   */
  function duck(seconds) {
    if (!ctx || !hostIn) {
      return;
    }

    const now = ctx.currentTime;
    const span = seconds > 0 ? seconds : 0.35;
    hostIn.gain.cancelScheduledValues(now);
    hostIn.gain.setValueAtTime(hostIn.gain.value, now);
    hostIn.gain.linearRampToValueAtTime(0.32, now + 0.04);
    hostIn.gain.setValueAtTime(0.32, now + span);
    hostIn.gain.linearRampToValueAtTime(1, now + span + 0.25);
  }

  async function stop() {
    clear();
    dotnet = null;

    if (worklet) {
      worklet.port.onmessage = null;
      worklet.disconnect();
      worklet = null;
    }

    if (source) {
      source.disconnect();
      source = null;
    }

    if (stream) {
      stream.getTracks().forEach(function (t) { t.stop(); });
      stream = null;
    }

    micLevel = 0;
    setVar("--po-mic-level", 0);
    setVar("--po-host-level", 0);
    clearBands();
    micAnalyser = null;
    micSpectrum = null;

    if (meterTimer) {
      window.clearInterval(meterTimer);
      meterTimer = 0;
    }
  }

  return {
    startCapture: startCapture,
    play: play,
    clear: clear,
    duck: duck,
    stop: stop,
    hostLevel: function () { return hostLevel; },

    /** How loud the room is, on the same curve as the meter: what the live fluid pours with while a fighter talks. */
    micLevel: function () { return Math.min(1, Math.pow(micLevel * 3.2, 0.7)); },

    /** Seconds of voice heard since capture started, and how many of them were loud: one call a snapshot. */
    voiced: function () { return { voiced: voiced, loud: loud }; },
  };
})();
